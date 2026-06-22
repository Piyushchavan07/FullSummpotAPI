using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using FullSummpotAPI.Data;
using FullSummpotAPI.Hubs;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class MessagesController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        private readonly IHubContext<ChatHub> _hub;
        public MessagesController(NpgsqlDbContext db, IHubContext<ChatHub> hub) { _db = db; _hub = hub; }
        private int GetCurrentUserId() => Convert.ToInt32(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

        [HttpPost("send")]
        public IActionResult SendMessage([FromBody] SendMessageDto dto)
        {
            int senderId = GetCurrentUserId();
            if (senderId == 0) return Unauthorized();
            if (senderId == dto.RecipientId) return BadRequest(new { message = "Cannot message yourself." });
            if (string.IsNullOrWhiteSpace(dto.Content)) return BadRequest(new { message = "Message cannot be empty." });
            dto.Content = dto.Content.Trim();
            if (dto.Content.Length > 1000) return BadRequest(new { message = "Message must not exceed 1000 characters." });

            using var conn = _db.GetConnection();
            conn.Open();
            try
            {
                bool isMutual;
                using (var mutualCmd = new NpgsqlCommand(@"
                    SELECT COUNT(*) FROM follows f1
                    JOIN follows f2 ON f2.follower_id = f1.following_id AND f2.following_id = f1.follower_id
                    WHERE f1.follower_id = @sid AND f1.following_id = @rid
                    AND f1.status = 'ACCEPTED' AND f2.status = 'ACCEPTED'", conn))
                {
                    mutualCmd.Parameters.AddWithValue("sid", senderId);
                    mutualCmd.Parameters.AddWithValue("rid", dto.RecipientId);
                    isMutual = Convert.ToInt32(mutualCmd.ExecuteScalar()) > 0;
                }

                if (isMutual)
                {
                    int conversationId;
                    using (var existingCmd = new NpgsqlCommand(@"
                        SELECT c.conversation_id FROM conversations c
                        WHERE c.is_active = TRUE
                        AND EXISTS (SELECT 1 FROM conversation_participants WHERE conversation_id = c.conversation_id AND user_id = @sid)
                        AND EXISTS (SELECT 1 FROM conversation_participants WHERE conversation_id = c.conversation_id AND user_id = @rid)
                        AND (SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = c.conversation_id) = 2
                        LIMIT 1", conn))
                    {
                        existingCmd.Parameters.AddWithValue("sid", senderId);
                        existingCmd.Parameters.AddWithValue("rid", dto.RecipientId);
                        var existingId = existingCmd.ExecuteScalar();
                        if (existingId != null && existingId != DBNull.Value)
                        {
                            conversationId = Convert.ToInt32(existingId);
                        }
                        else
                        {
                            using var createCmd = new NpgsqlCommand(
                                "INSERT INTO conversations (is_active) VALUES (TRUE) RETURNING conversation_id", conn);
                            conversationId = Convert.ToInt32(createCmd.ExecuteScalar());
                            foreach (var pid in new[] { senderId, dto.RecipientId })
                            {
                                using var addP = new NpgsqlCommand(
                                    "INSERT INTO conversation_participants (conversation_id, user_id) VALUES (@cid, @pid)", conn);
                                addP.Parameters.AddWithValue("cid", conversationId);
                                addP.Parameters.AddWithValue("pid", pid);
                                addP.ExecuteNonQuery();
                            }
                        }
                    }

                    using var msgCmd = new NpgsqlCommand(@"
                        INSERT INTO messages (conversation_id, sender_id, content, sent_at)
                        VALUES (@cid, @sid, @content, NOW() AT TIME ZONE 'UTC')", conn);
                    msgCmd.Parameters.AddWithValue("cid",     conversationId);
                    msgCmd.Parameters.AddWithValue("sid",     senderId);
                    msgCmd.Parameters.AddWithValue("content", dto.Content);
                    msgCmd.ExecuteNonQuery();

                    _ = _hub.Clients.Group($"user_{dto.RecipientId}").SendAsync("NewMessage", new { conversationId, senderId });
                    return Ok(new { conversationId, type = "direct" });
                }
                else
                {
                    bool reqExists;
                    using (var reqCheck = new NpgsqlCommand(@"
                        SELECT COUNT(*) FROM message_requests
                        WHERE sender_id = @sid AND recipient_id = @rid", conn))
                    {
                        reqCheck.Parameters.AddWithValue("sid", senderId);
                        reqCheck.Parameters.AddWithValue("rid", dto.RecipientId);
                        reqExists = Convert.ToInt32(reqCheck.ExecuteScalar()) > 0;
                    }

                    if (reqExists)
                    {
                        using var updateReq = new NpgsqlCommand(@"
                            UPDATE message_requests SET first_message = @content, status = 'PENDING'
                            WHERE sender_id = @sid AND recipient_id = @rid", conn);
                        updateReq.Parameters.AddWithValue("content", dto.Content);
                        updateReq.Parameters.AddWithValue("sid",     senderId);
                        updateReq.Parameters.AddWithValue("rid",     dto.RecipientId);
                        updateReq.ExecuteNonQuery();
                        return Ok(new { type = "request" });
                    }

                    using var reqCmd = new NpgsqlCommand(@"
                        INSERT INTO message_requests (sender_id, recipient_id, first_message)
                        VALUES (@sid, @rid, @content)", conn);
                    reqCmd.Parameters.AddWithValue("sid",     senderId);
                    reqCmd.Parameters.AddWithValue("rid",     dto.RecipientId);
                    reqCmd.Parameters.AddWithValue("content", dto.Content);
                    reqCmd.ExecuteNonQuery();

                    using var nameCmd = new NpgsqlCommand("SELECT username FROM users WHERE user_id = @sid", conn);
                    nameCmd.Parameters.AddWithValue("sid", senderId);
                    var name = nameCmd.ExecuteScalar()?.ToString() ?? "Someone";

                    using var notifCmd = new NpgsqlCommand(@"
                        INSERT INTO notifications (user_id, sender_id, type, message)
                        VALUES (@rid, @sid, 'MESSAGE_REQUEST', @msg)", conn);
                    notifCmd.Parameters.AddWithValue("rid", dto.RecipientId);
                    notifCmd.Parameters.AddWithValue("sid", senderId);
                    notifCmd.Parameters.AddWithValue("msg", $"@{name} sent you a message request.");
                    notifCmd.ExecuteNonQuery();
                    return Ok(new { type = "request" });
                }
            }
            catch { return StatusCode(500, new { message = "An error occurred while sending the message." }); }
        }

        [HttpGet("conversations")]
        public IActionResult GetConversations()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT c.conversation_id,
                       u.user_id AS other_user_id, u.username AS other_username, u.avatar_url AS other_avatar,
                       (SELECT m2.content FROM messages m2 WHERE m2.conversation_id = c.conversation_id
                        ORDER BY m2.sent_at DESC LIMIT 1) AS last_message,
                       (SELECT MAX(m4.sent_at) FROM messages m4 WHERE m4.conversation_id = c.conversation_id) AS last_message_at,
                       (SELECT COUNT(*) FROM messages m5 WHERE m5.conversation_id = c.conversation_id
                        AND m5.is_read = FALSE AND m5.sender_id != @uid) AS unread_count
                FROM conversations c
                JOIN conversation_participants cp  ON cp.conversation_id  = c.conversation_id AND cp.user_id  = @uid
                JOIN conversation_participants cp2 ON cp2.conversation_id = c.conversation_id AND cp2.user_id != @uid
                JOIN users u ON u.user_id = cp2.user_id
                WHERE c.is_active = TRUE
                ORDER BY last_message_at DESC NULLS LAST", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    conversationId = Convert.ToInt32(reader["conversation_id"]),
                    otherUserId    = Convert.ToInt32(reader["other_user_id"]),
                    otherUsername  = reader["other_username"].ToString(),
                    otherAvatar    = reader["other_avatar"] == DBNull.Value ? null : reader["other_avatar"].ToString(),
                    lastMessage    = reader["last_message"] == DBNull.Value ? null : reader["last_message"].ToString(),
                    lastMessageAt  = reader["last_message_at"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("last_message_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    unreadCount    = Convert.ToInt32(reader["unread_count"])
                });
            }
            return Ok(list);
        }

        [HttpGet("conversations/{conversationId:int}")]
        public IActionResult GetMessages(int conversationId)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();

            using (var checkCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @cid AND user_id = @uid", conn))
            {
                checkCmd.Parameters.AddWithValue("cid", conversationId);
                checkCmd.Parameters.AddWithValue("uid", userId);
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Not a participant." });
            }

            using (var markCmd = new NpgsqlCommand(@"
                UPDATE messages SET is_read = TRUE
                WHERE conversation_id = @cid AND sender_id != @uid AND is_read = FALSE", conn))
            {
                markCmd.Parameters.AddWithValue("cid", conversationId);
                markCmd.Parameters.AddWithValue("uid", userId);
                markCmd.ExecuteNonQuery();
            }

            using var cmd = new NpgsqlCommand(@"
                SELECT m.message_id, m.sender_id, m.content, m.is_read, m.sent_at, u.username
                FROM messages m JOIN users u ON u.user_id = m.sender_id
                WHERE m.conversation_id = @cid ORDER BY m.sent_at ASC", conn);
            cmd.Parameters.AddWithValue("cid", conversationId);
            using var reader = cmd.ExecuteReader();
            var messages = new List<object>();
            while (reader.Read())
            {
                messages.Add(new
                {
                    messageId = Convert.ToInt32(reader["message_id"]),
                    senderId  = Convert.ToInt32(reader["sender_id"]),
                    username  = reader["username"].ToString(),
                    content   = reader["content"].ToString(),
                    isRead    = Convert.ToBoolean(reader["is_read"]),
                    sentAt    = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("sent_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    isMine    = Convert.ToInt32(reader["sender_id"]) == userId
                });
            }
            return Ok(messages);
        }

        [HttpPost("conversations/{conversationId:int}")]
        public IActionResult SendToConversation(int conversationId, [FromBody] MessageContentDto dto)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            if (string.IsNullOrWhiteSpace(dto.Content)) return BadRequest(new { message = "Message cannot be empty." });
            dto.Content = dto.Content.Trim();
            if (dto.Content.Length > 1000) return BadRequest(new { message = "Message must not exceed 1000 characters." });

            using var conn = _db.GetConnection();
            conn.Open();
            try
            {
                using (var checkCmd = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM conversation_participants WHERE conversation_id = @cid AND user_id = @uid", conn))
                {
                    checkCmd.Parameters.AddWithValue("cid", conversationId);
                    checkCmd.Parameters.AddWithValue("uid", userId);
                    if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                        return StatusCode(403, new { message = "Not a participant." });
                }

                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO messages (conversation_id, sender_id, content, sent_at)
                    VALUES (@cid, @uid, @content, NOW() AT TIME ZONE 'UTC')", conn);
                cmd.Parameters.AddWithValue("cid",     conversationId);
                cmd.Parameters.AddWithValue("uid",     userId);
                cmd.Parameters.AddWithValue("content", dto.Content);
                cmd.ExecuteNonQuery();

                using var otherCmd = new NpgsqlCommand(
                    "SELECT user_id FROM conversation_participants WHERE conversation_id = @cid AND user_id != @uid", conn);
                otherCmd.Parameters.AddWithValue("cid", conversationId);
                otherCmd.Parameters.AddWithValue("uid", userId);
                var otherUserId = otherCmd.ExecuteScalar();
                if (otherUserId != null && otherUserId != DBNull.Value)
                    _ = _hub.Clients.Group($"user_{otherUserId}").SendAsync("NewMessage", new { conversationId, senderId = userId });

                return Ok(new { message = "Sent" });
            }
            catch { return StatusCode(500, new { message = "An error occurred while sending the message." }); }
        }

        [HttpGet("requests")]
        public IActionResult GetRequests()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT mr.request_id, mr.sender_id, mr.first_message, mr.created_at, u.username, u.avatar_url
                FROM message_requests mr JOIN users u ON u.user_id = mr.sender_id
                WHERE mr.recipient_id = @uid AND mr.status = 'PENDING'
                ORDER BY mr.created_at DESC", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    requestId    = Convert.ToInt32(reader["request_id"]),
                    senderId     = Convert.ToInt32(reader["sender_id"]),
                    username     = reader["username"].ToString(),
                    avatarUrl    = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(),
                    firstMessage = reader["first_message"].ToString(),
                    createdAt    = reader.GetDateTime(reader.GetOrdinal("created_at")).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpGet("requests/sent")]
        public IActionResult GetSentRequests()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT mr.request_id, mr.recipient_id, mr.first_message, mr.created_at, mr.status, u.username, u.avatar_url
                FROM message_requests mr JOIN users u ON u.user_id = mr.recipient_id
                WHERE mr.sender_id = @uid AND mr.status = 'PENDING'
                ORDER BY mr.created_at DESC", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    requestId    = Convert.ToInt32(reader["request_id"]),
                    recipientId  = Convert.ToInt32(reader["recipient_id"]),
                    username     = reader["username"].ToString(),
                    avatarUrl    = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(),
                    firstMessage = reader["first_message"].ToString(),
                    status       = reader["status"].ToString(),
                    createdAt    = reader.GetDateTime(reader.GetOrdinal("created_at")).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpPost("requests/{requestId:int}/accept")]
        public IActionResult AcceptRequest(int requestId)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();

            int senderId; string firstMessage;
            using (var reqCmd = new NpgsqlCommand(
                "SELECT sender_id, first_message FROM message_requests WHERE request_id = @rid AND recipient_id = @uid AND status = 'PENDING'", conn))
            {
                reqCmd.Parameters.AddWithValue("rid", requestId);
                reqCmd.Parameters.AddWithValue("uid", userId);
                using var r = reqCmd.ExecuteReader();
                if (!r.Read()) return NotFound(new { message = "Request not found." });
                senderId     = Convert.ToInt32(r["sender_id"]);
                firstMessage = r["first_message"].ToString()!;
            }

            using var createCmd = new NpgsqlCommand(
                "INSERT INTO conversations (is_active) VALUES (TRUE) RETURNING conversation_id", conn);
            int conversationId = Convert.ToInt32(createCmd.ExecuteScalar());

            foreach (var pid in new[] { senderId, userId })
            {
                using var addP = new NpgsqlCommand(
                    "INSERT INTO conversation_participants (conversation_id, user_id) VALUES (@cid, @pid)", conn);
                addP.Parameters.AddWithValue("cid", conversationId);
                addP.Parameters.AddWithValue("pid", pid);
                addP.ExecuteNonQuery();
            }

            using var msgCmd = new NpgsqlCommand(@"
                INSERT INTO messages (conversation_id, sender_id, content, sent_at)
                VALUES (@cid, @sid, @content, NOW() AT TIME ZONE 'UTC')", conn);
            msgCmd.Parameters.AddWithValue("cid",     conversationId);
            msgCmd.Parameters.AddWithValue("sid",     senderId);
            msgCmd.Parameters.AddWithValue("content", firstMessage);
            msgCmd.ExecuteNonQuery();

            using var updateCmd = new NpgsqlCommand(
                "UPDATE message_requests SET status = 'ACCEPTED' WHERE request_id = @rid", conn);
            updateCmd.Parameters.AddWithValue("rid", requestId);
            updateCmd.ExecuteNonQuery();

            return Ok(new { conversationId });
        }

        [HttpPost("requests/{requestId:int}/decline")]
        public IActionResult DeclineRequest(int requestId)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "UPDATE message_requests SET status = 'DECLINED' WHERE request_id = @rid AND recipient_id = @uid", conn);
            cmd.Parameters.AddWithValue("rid", requestId);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Request declined" });
        }

        [HttpGet("unread")]
        public IActionResult GetUnreadCount()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();

            int unreadMessages;
            using (var msgCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM messages m
                JOIN conversation_participants cp ON cp.conversation_id = m.conversation_id
                WHERE cp.user_id = @uid AND m.sender_id != @uid AND m.is_read = FALSE", conn))
            {
                msgCmd.Parameters.AddWithValue("uid", userId);
                unreadMessages = Convert.ToInt32(msgCmd.ExecuteScalar());
            }

            int pendingRequests;
            using (var reqCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM message_requests WHERE recipient_id = @uid AND status = 'PENDING'", conn))
            {
                reqCmd.Parameters.AddWithValue("uid", userId);
                pendingRequests = Convert.ToInt32(reqCmd.ExecuteScalar());
            }

            return Ok(new { unreadMessages, pendingRequests, total = unreadMessages + pendingRequests });
        }
    }

    public class SendMessageDto  { public int RecipientId { get; set; } public string Content { get; set; } = ""; }
    public class MessageContentDto { public string Content { get; set; } = ""; }
}
