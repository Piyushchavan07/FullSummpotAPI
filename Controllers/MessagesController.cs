using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Oracle.ManagedDataAccess.Client;
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
        private readonly OracleDbContext _db;
        private readonly IHubContext<ChatHub> _hub;

        public MessagesController(OracleDbContext db, IHubContext<ChatHub> hub)
        {
            _db = db;
            _hub = hub;
        }

        private int GetCurrentUserId() =>
            Convert.ToInt32(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

        [HttpPost("send")]
        public IActionResult SendMessage([FromBody] SendMessageDto dto)
        {
            int senderId = GetCurrentUserId();
            if (senderId == 0) return Unauthorized();
            if (senderId == dto.RecipientId) return BadRequest(new { message = "Cannot message yourself." });

            // --- Input validation ---
            if (string.IsNullOrWhiteSpace(dto.Content))
                return BadRequest(new { message = "Message cannot be empty." });
            dto.Content = dto.Content.Trim();
            if (dto.Content.Length > 1000)
                return BadRequest(new { message = "Message must not exceed 1000 characters." });

            using var conn = _db.GetConnection();
            conn.Open();

            try
            {
                var mutualCmd = new OracleCommand(@"
                    SELECT COUNT(*) FROM FOLLOWS f1
                    JOIN FOLLOWS f2 ON f2.FOLLOWER_ID = f1.FOLLOWING_ID AND f2.FOLLOWING_ID = f1.FOLLOWER_ID
                    WHERE f1.FOLLOWER_ID = :senderId AND f1.FOLLOWING_ID = :recipientId
                    AND f1.STATUS = 'ACCEPTED' AND f2.STATUS = 'ACCEPTED'", conn);
                mutualCmd.BindByName = true;
                mutualCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                mutualCmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = dto.RecipientId;
                bool isMutual = Convert.ToInt32(mutualCmd.ExecuteScalar()) > 0;

                if (isMutual)
                {
                    var existingCmd = new OracleCommand(@"
                        SELECT c.CONVERSATION_ID FROM CONVERSATIONS c
                        WHERE c.IS_ACTIVE = 1
                        AND EXISTS (SELECT 1 FROM CONVERSATION_PARTICIPANTS WHERE CONVERSATION_ID = c.CONVERSATION_ID AND USER_ID = :senderId)
                        AND EXISTS (SELECT 1 FROM CONVERSATION_PARTICIPANTS WHERE CONVERSATION_ID = c.CONVERSATION_ID AND USER_ID = :recipientId)
                        AND (SELECT COUNT(*) FROM CONVERSATION_PARTICIPANTS WHERE CONVERSATION_ID = c.CONVERSATION_ID) = 2
                        AND ROWNUM = 1", conn);
                    existingCmd.BindByName = true;
                    existingCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                    existingCmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = dto.RecipientId;
                    var existingId = existingCmd.ExecuteScalar();

                    int conversationId;
                    if (existingId != null && existingId != DBNull.Value)
                    {
                        conversationId = Convert.ToInt32(existingId);
                    }
                    else
                    {
                        var createCmd = new OracleCommand("INSERT INTO CONVERSATIONS (IS_ACTIVE) VALUES (1)", conn);
                        createCmd.ExecuteNonQuery();
                        var getIdCmd = new OracleCommand("SELECT MAX(CONVERSATION_ID) FROM CONVERSATIONS", conn);
                        conversationId = Convert.ToInt32(getIdCmd.ExecuteScalar());

                        var addP1 = new OracleCommand("INSERT INTO CONVERSATION_PARTICIPANTS (CONVERSATION_ID, USER_ID) VALUES (:convId, :participantId)", conn);
                        addP1.BindByName = true;
                        addP1.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                        addP1.Parameters.Add("participantId", OracleDbType.Int32).Value = senderId;
                        addP1.ExecuteNonQuery();

                        var addP2 = new OracleCommand("INSERT INTO CONVERSATION_PARTICIPANTS (CONVERSATION_ID, USER_ID) VALUES (:convId, :participantId)", conn);
                        addP2.BindByName = true;
                        addP2.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                        addP2.Parameters.Add("participantId", OracleDbType.Int32).Value = dto.RecipientId;
                        addP2.ExecuteNonQuery();
                    }

                    var msgCmd = new OracleCommand(@"
                        INSERT INTO MESSAGES (CONVERSATION_ID, SENDER_ID, CONTENT, SENT_AT)
                        VALUES (:convId, :senderId, :msgContent, SYS_EXTRACT_UTC(SYSTIMESTAMP))", conn);
                    msgCmd.BindByName = true;
                    msgCmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                    msgCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                    msgCmd.Parameters.Add("msgContent", OracleDbType.Varchar2).Value = dto.Content;
                    msgCmd.ExecuteNonQuery();

                    // Push real-time event to recipient
                    _ = _hub.Clients.Group($"user_{dto.RecipientId}").SendAsync("NewMessage", new { conversationId, senderId });

                    return Ok(new { conversationId, type = "direct" });
                }
                else
                {
                    var reqCheck = new OracleCommand(@"
                        SELECT COUNT(*) FROM MESSAGE_REQUESTS
                        WHERE SENDER_ID = :senderId AND RECIPIENT_ID = :recipientId", conn);
                    reqCheck.BindByName = true;
                    reqCheck.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                    reqCheck.Parameters.Add("recipientId", OracleDbType.Int32).Value = dto.RecipientId;

                    if (Convert.ToInt32(reqCheck.ExecuteScalar()) > 0)
                    {
                        var updateReq = new OracleCommand(@"
                            UPDATE MESSAGE_REQUESTS SET FIRST_MESSAGE = :msgContent, STATUS = 'PENDING'
                            WHERE SENDER_ID = :senderId AND RECIPIENT_ID = :recipientId", conn);
                        updateReq.BindByName = true;
                        updateReq.Parameters.Add("msgContent", OracleDbType.Varchar2).Value = dto.Content;
                        updateReq.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                        updateReq.Parameters.Add("recipientId", OracleDbType.Int32).Value = dto.RecipientId;
                        updateReq.ExecuteNonQuery();
                        return Ok(new { type = "request" });
                    }

                    var reqCmd = new OracleCommand(@"
                        INSERT INTO MESSAGE_REQUESTS (SENDER_ID, RECIPIENT_ID, FIRST_MESSAGE)
                        VALUES (:senderId, :recipientId, :msgContent)", conn);
                    reqCmd.BindByName = true;
                    reqCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                    reqCmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = dto.RecipientId;
                    reqCmd.Parameters.Add("msgContent", OracleDbType.Varchar2).Value = dto.Content;
                    reqCmd.ExecuteNonQuery();

                    var nameCmd = new OracleCommand("SELECT USERNAME FROM USERS WHERE USER_ID = :senderId", conn);
                    nameCmd.BindByName = true;
                    nameCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                    var name = nameCmd.ExecuteScalar()?.ToString() ?? "Someone";

                    var notifCmd = new OracleCommand(@"
                        INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE)
                        VALUES (:recipientId, :senderId, 'MESSAGE_REQUEST', :msgContent)", conn);
                    notifCmd.BindByName = true;
                    notifCmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = dto.RecipientId;
                    notifCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
                    notifCmd.Parameters.Add("msgContent", OracleDbType.Varchar2).Value =
                        $"@{name} sent you a message request.";
                    notifCmd.ExecuteNonQuery();

                    return Ok(new { type = "request" });
                }
            }
            catch
            {
                return StatusCode(500, new { message = "An error occurred while sending the message." });
            }
        }

        [HttpGet("conversations")]
        public IActionResult GetConversations()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT
                    c.CONVERSATION_ID,
                    u.USER_ID as OTHER_USER_ID,
                    u.USERNAME as OTHER_USERNAME,
                    u.AVATAR_URL as OTHER_AVATAR,
                    (SELECT m2.CONTENT FROM MESSAGES m2
                     WHERE m2.CONVERSATION_ID = c.CONVERSATION_ID
                     AND m2.SENT_AT = (SELECT MAX(m3.SENT_AT) FROM MESSAGES m3 WHERE m3.CONVERSATION_ID = c.CONVERSATION_ID)
                     AND ROWNUM = 1) as LAST_MESSAGE,
                    (SELECT MAX(m4.SENT_AT) FROM MESSAGES m4 WHERE m4.CONVERSATION_ID = c.CONVERSATION_ID) as LAST_MESSAGE_AT,
                    (SELECT COUNT(*) FROM MESSAGES m5
                     WHERE m5.CONVERSATION_ID = c.CONVERSATION_ID
                     AND m5.IS_READ = 0 AND m5.SENDER_ID != :currentUserId) as UNREAD_COUNT
                FROM CONVERSATIONS c
                JOIN CONVERSATION_PARTICIPANTS cp ON cp.CONVERSATION_ID = c.CONVERSATION_ID AND cp.USER_ID = :currentUserId
                JOIN CONVERSATION_PARTICIPANTS cp2 ON cp2.CONVERSATION_ID = c.CONVERSATION_ID AND cp2.USER_ID != :currentUserId
                JOIN USERS u ON u.USER_ID = cp2.USER_ID
                WHERE c.IS_ACTIVE = 1
                ORDER BY LAST_MESSAGE_AT DESC NULLS LAST", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("currentUserId", OracleDbType.Int32).Value = userId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    conversationId = Convert.ToInt32(reader["CONVERSATION_ID"]),
                    otherUserId = Convert.ToInt32(reader["OTHER_USER_ID"]),
                    otherUsername = reader["OTHER_USERNAME"].ToString(),
                    otherAvatar = reader["OTHER_AVATAR"] == DBNull.Value ? null : reader["OTHER_AVATAR"].ToString(),
                    lastMessage = reader["LAST_MESSAGE"] == DBNull.Value ? null : reader["LAST_MESSAGE"].ToString(),
                    lastMessageAt = reader["LAST_MESSAGE_AT"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(
                            reader.GetDateTime(reader.GetOrdinal("LAST_MESSAGE_AT")),
                            DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),  // ← FIXED: Utc not Local
                    unreadCount = Convert.ToInt32(reader["UNREAD_COUNT"])
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

            var checkCmd = new OracleCommand("SELECT COUNT(*) FROM CONVERSATION_PARTICIPANTS WHERE CONVERSATION_ID = :convId AND USER_ID = :participantId", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
            checkCmd.Parameters.Add("participantId", OracleDbType.Int32).Value = userId;
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                return StatusCode(403, new { message = "Not a participant." });

            var markCmd = new OracleCommand(@"
                UPDATE MESSAGES SET IS_READ = 1
                WHERE CONVERSATION_ID = :convId AND SENDER_ID != :participantId AND IS_READ = 0", conn);
            markCmd.BindByName = true;
            markCmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
            markCmd.Parameters.Add("participantId", OracleDbType.Int32).Value = userId;
            markCmd.ExecuteNonQuery();

            var cmd = new OracleCommand(@"
                SELECT m.MESSAGE_ID, m.SENDER_ID, m.CONTENT, m.IS_READ, m.SENT_AT, u.USERNAME
                FROM MESSAGES m
                JOIN USERS u ON u.USER_ID = m.SENDER_ID
                WHERE m.CONVERSATION_ID = :convId
                ORDER BY m.SENT_AT ASC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;

            using var reader = cmd.ExecuteReader();
            var messages = new List<object>();
            while (reader.Read())
            {
                messages.Add(new
                {
                    messageId = Convert.ToInt32(reader["MESSAGE_ID"]),
                    senderId = Convert.ToInt32(reader["SENDER_ID"]),
                    username = reader["USERNAME"].ToString(),
                    content = reader["CONTENT"].ToString(),
                    isRead = Convert.ToInt32(reader["IS_READ"]) == 1,
                    sentAt = DateTime.SpecifyKind(
                        reader.GetDateTime(reader.GetOrdinal("SENT_AT")),
                        DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),  // ← FIXED: Utc not Local
                    isMine = Convert.ToInt32(reader["SENDER_ID"]) == userId
                });
            }
            return Ok(messages);
        }

        [HttpPost("conversations/{conversationId:int}")]
        public IActionResult SendToConversation(int conversationId, [FromBody] MessageContentDto dto)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();

            // --- Input validation ---
            if (string.IsNullOrWhiteSpace(dto.Content))
                return BadRequest(new { message = "Message cannot be empty." });
            dto.Content = dto.Content.Trim();
            if (dto.Content.Length > 1000)
                return BadRequest(new { message = "Message must not exceed 1000 characters." });

            using var conn = _db.GetConnection();
            conn.Open();

            try
            {
                var checkCmd = new OracleCommand("SELECT COUNT(*) FROM CONVERSATION_PARTICIPANTS WHERE CONVERSATION_ID = :convId AND USER_ID = :participantId", conn);
                checkCmd.BindByName = true;
                checkCmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                checkCmd.Parameters.Add("participantId", OracleDbType.Int32).Value = userId;
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Not a participant." });

                var cmd = new OracleCommand(@"
                    INSERT INTO MESSAGES (CONVERSATION_ID, SENDER_ID, CONTENT, SENT_AT)
                    VALUES (:convId, :senderId, :msgContent, SYS_EXTRACT_UTC(SYSTIMESTAMP))", conn);
                cmd.BindByName = true;
                cmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                cmd.Parameters.Add("senderId", OracleDbType.Int32).Value = userId;
                cmd.Parameters.Add("msgContent", OracleDbType.Varchar2).Value = dto.Content;
                cmd.ExecuteNonQuery();

                // Find the other user in the conversation and push event
                var otherCmd = new OracleCommand("SELECT USER_ID FROM CONVERSATION_PARTICIPANTS WHERE CONVERSATION_ID = :convId AND USER_ID != :me", conn);
                otherCmd.BindByName = true;
                otherCmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                otherCmd.Parameters.Add("me", OracleDbType.Int32).Value = userId;
                var otherUserId = otherCmd.ExecuteScalar();
                if (otherUserId != null && otherUserId != DBNull.Value)
                {
                    _ = _hub.Clients.Group($"user_{otherUserId}").SendAsync("NewMessage", new { conversationId, senderId = userId });
                }

                return Ok(new { message = "Sent" });
            }
            catch
            {
                return StatusCode(500, new { message = "An error occurred while sending the message." });
            }
        }

        [HttpGet("requests/sent")]
        public IActionResult GetSentRequests()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT mr.REQUEST_ID, mr.RECIPIENT_ID, mr.FIRST_MESSAGE, mr.CREATED_AT, mr.STATUS,
                       u.USERNAME, u.AVATAR_URL
                FROM MESSAGE_REQUESTS mr
                JOIN USERS u ON u.USER_ID = mr.RECIPIENT_ID
                WHERE mr.SENDER_ID = :senderId AND mr.STATUS = 'PENDING'
                ORDER BY mr.CREATED_AT DESC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("senderId", OracleDbType.Int32).Value = userId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    requestId = Convert.ToInt32(reader["REQUEST_ID"]),
                    recipientId = Convert.ToInt32(reader["RECIPIENT_ID"]),
                    username = reader["USERNAME"].ToString(),
                    avatarUrl = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                    firstMessage = reader["FIRST_MESSAGE"].ToString(),
                    status = reader["STATUS"].ToString(),
                    createdAt = reader.GetDateTime(reader.GetOrdinal("CREATED_AT")).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpGet("requests")]
        public IActionResult GetRequests()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT mr.REQUEST_ID, mr.SENDER_ID, mr.FIRST_MESSAGE, mr.CREATED_AT,
                       u.USERNAME, u.AVATAR_URL
                FROM MESSAGE_REQUESTS mr
                JOIN USERS u ON u.USER_ID = mr.SENDER_ID
                WHERE mr.RECIPIENT_ID = :recipientId AND mr.STATUS = 'PENDING'
                ORDER BY mr.CREATED_AT DESC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = userId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    requestId = Convert.ToInt32(reader["REQUEST_ID"]),
                    senderId = Convert.ToInt32(reader["SENDER_ID"]),
                    username = reader["USERNAME"].ToString(),
                    avatarUrl = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                    firstMessage = reader["FIRST_MESSAGE"].ToString(),
                    createdAt = reader.GetDateTime(reader.GetOrdinal("CREATED_AT")).ToString("yyyy-MM-ddTHH:mm:ssZ")
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

            var reqCmd = new OracleCommand("SELECT SENDER_ID, FIRST_MESSAGE FROM MESSAGE_REQUESTS WHERE REQUEST_ID = :requestId AND RECIPIENT_ID = :recipientId AND STATUS = 'PENDING'", conn);
            reqCmd.BindByName = true;
            reqCmd.Parameters.Add("requestId", OracleDbType.Int32).Value = requestId;
            reqCmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = userId;

            using var reader = reqCmd.ExecuteReader();
            if (!reader.Read()) return NotFound(new { message = "Request not found." });

            int senderId = Convert.ToInt32(reader["SENDER_ID"]);
            string firstMessage = reader["FIRST_MESSAGE"].ToString()!;
            reader.Close();

            var createCmd = new OracleCommand("INSERT INTO CONVERSATIONS (IS_ACTIVE) VALUES (1)", conn);
            createCmd.ExecuteNonQuery();
            var getIdCmd = new OracleCommand("SELECT MAX(CONVERSATION_ID) FROM CONVERSATIONS", conn);
            int conversationId = Convert.ToInt32(getIdCmd.ExecuteScalar());

            foreach (var pid in new[] { senderId, userId })
            {
                var addP = new OracleCommand("INSERT INTO CONVERSATION_PARTICIPANTS (CONVERSATION_ID, USER_ID) VALUES (:convId, :participantId)", conn);
                addP.BindByName = true;
                addP.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
                addP.Parameters.Add("participantId", OracleDbType.Int32).Value = pid;
                addP.ExecuteNonQuery();
            }

            var msgCmd = new OracleCommand(@"
                INSERT INTO MESSAGES (CONVERSATION_ID, SENDER_ID, CONTENT, SENT_AT)
                VALUES (:convId, :senderId, :msgContent, SYS_EXTRACT_UTC(SYSTIMESTAMP))", conn);
            msgCmd.BindByName = true;
            msgCmd.Parameters.Add("convId", OracleDbType.Int32).Value = conversationId;
            msgCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = senderId;
            msgCmd.Parameters.Add("msgContent", OracleDbType.Varchar2).Value = firstMessage;
            msgCmd.ExecuteNonQuery();

            var updateCmd = new OracleCommand("UPDATE MESSAGE_REQUESTS SET STATUS = 'ACCEPTED' WHERE REQUEST_ID = :requestId", conn);
            updateCmd.BindByName = true;
            updateCmd.Parameters.Add("requestId", OracleDbType.Int32).Value = requestId;
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

            var cmd = new OracleCommand("UPDATE MESSAGE_REQUESTS SET STATUS = 'DECLINED' WHERE REQUEST_ID = :requestId AND RECIPIENT_ID = :recipientId", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("requestId", OracleDbType.Int32).Value = requestId;
            cmd.Parameters.Add("recipientId", OracleDbType.Int32).Value = userId;
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

            var msgCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM MESSAGES m
                JOIN CONVERSATION_PARTICIPANTS cp ON cp.CONVERSATION_ID = m.CONVERSATION_ID
                WHERE cp.USER_ID = :participantId AND m.SENDER_ID != :notSenderId AND m.IS_READ = 0", conn);
            msgCmd.BindByName = true;
            msgCmd.Parameters.Add("participantId", OracleDbType.Int32).Value = userId;
            msgCmd.Parameters.Add("notSenderId", OracleDbType.Int32).Value = userId;
            int unreadMessages = Convert.ToInt32(msgCmd.ExecuteScalar());

            var reqCmd = new OracleCommand("SELECT COUNT(*) FROM MESSAGE_REQUESTS WHERE RECIPIENT_ID = :reqRecipientId AND STATUS = 'PENDING'", conn);
            reqCmd.BindByName = true;
            reqCmd.Parameters.Add("reqRecipientId", OracleDbType.Int32).Value = userId;
            int pendingRequests = Convert.ToInt32(reqCmd.ExecuteScalar());

            return Ok(new { unreadMessages, pendingRequests, total = unreadMessages + pendingRequests });
        }
    }

    public class SendMessageDto
    {
        public int RecipientId { get; set; }
        public string Content { get; set; } = "";
    }

    public class MessageContentDto
    {
        public string Content { get; set; } = "";
    }
}
