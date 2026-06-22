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
    public class LinksController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        private readonly IHubContext<ChatHub> _hub;
        public LinksController(NpgsqlDbContext db, IHubContext<ChatHub> hub) { _db = db; _hub = hub; }

        [HttpPost]
        public IActionResult Create([FromBody] CreateLinkDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest(new { message = "Link title is required." });
            dto.Title = dto.Title.Trim();
            if (dto.Title.Length < 3) return BadRequest(new { message = "Link title must be at least 3 characters." });
            if (dto.Title.Length > 200) return BadRequest(new { message = "Link title must not exceed 200 characters." });
            if (string.IsNullOrWhiteSpace(dto.Url)) return BadRequest(new { message = "Link URL is required." });
            dto.Url = dto.Url.Trim();
            if (!dto.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                !dto.Url.Contains("youtu.be",    StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only YouTube links are allowed." });

            using var conn = _db.GetConnection();
            conn.Open();
            try
            {
                using (var creatorCheck = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM communities WHERE community_id = @cid AND created_by = @uid", conn))
                {
                    creatorCheck.Parameters.AddWithValue("cid", dto.CommunityId);
                    creatorCheck.Parameters.AddWithValue("uid", uid);
                    if (Convert.ToInt32(creatorCheck.ExecuteScalar()) == 0)
                        return StatusCode(403, new { message = "Only the community creator can submit links." });
                }
                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO links (title, url, community_id, user_id, created_at)
                    VALUES (@title, @url, @cid, @uid, NOW() AT TIME ZONE 'UTC')", conn);
                cmd.Parameters.AddWithValue("title", dto.Title);
                cmd.Parameters.AddWithValue("url",   dto.Url);
                cmd.Parameters.AddWithValue("cid",   dto.CommunityId);
                cmd.Parameters.AddWithValue("uid",   uid);
                cmd.ExecuteNonQuery();
                return Ok(new { message = "Link added successfully" });
            }
            catch { return StatusCode(500, new { message = "An error occurred while adding the link." }); }
        }

        [HttpPut("{id:int}")]
        public IActionResult Update(int id, [FromBody] UpdateLinkDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest(new { message = "Link title is required." });
            dto.Title = dto.Title.Trim();
            if (dto.Title.Length < 3) return BadRequest(new { message = "Link title must be at least 3 characters." });
            if (dto.Title.Length > 200) return BadRequest(new { message = "Link title must not exceed 200 characters." });
            if (string.IsNullOrWhiteSpace(dto.Url)) return BadRequest(new { message = "Link URL is required." });
            dto.Url = dto.Url.Trim();
            if (!dto.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                !dto.Url.Contains("youtu.be",    StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only YouTube links are allowed." });

            using var conn = _db.GetConnection();
            conn.Open();
            try
            {
                using (var check = new NpgsqlCommand("SELECT COUNT(*) FROM links WHERE link_id = @lid AND user_id = @uid", conn))
                {
                    check.Parameters.AddWithValue("lid", id);
                    check.Parameters.AddWithValue("uid", uid);
                    if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                        return StatusCode(403, new { message = "You can only edit your own links." });
                }
                using var cmd = new NpgsqlCommand("UPDATE links SET title = @title, url = @url WHERE link_id = @lid", conn);
                cmd.Parameters.AddWithValue("title", dto.Title);
                cmd.Parameters.AddWithValue("url",   dto.Url);
                cmd.Parameters.AddWithValue("lid",   id);
                cmd.ExecuteNonQuery();
                return Ok(new { message = "Link updated successfully" });
            }
            catch { return StatusCode(500, new { message = "An error occurred while updating the link." }); }
        }

        [HttpDelete("{id:int}")]
        public IActionResult Delete(int id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();
            try
            {
                using (var check = new NpgsqlCommand(@"
                    SELECT COUNT(*) FROM links l
                    JOIN communities c ON c.community_id = l.community_id
                    WHERE l.link_id = @lid AND (l.user_id = @uid OR c.created_by = @uid)", conn))
                {
                    check.Parameters.AddWithValue("lid", id);
                    check.Parameters.AddWithValue("uid", uid);
                    if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                        return StatusCode(403, new { message = "You don't have permission to delete this link." });
                }
                using var cmd = new NpgsqlCommand("DELETE FROM links WHERE link_id = @lid", conn);
                cmd.Parameters.AddWithValue("lid", id);
                int rows = cmd.ExecuteNonQuery();
                if (rows == 0) return NotFound(new { message = "Link not found." });
                return Ok(new { message = "Link deleted successfully" });
            }
            catch { return StatusCode(500, new { message = "An error occurred while deleting the link." }); }
        }

        [HttpGet("community/{communityId:int}")]
        public IActionResult GetByCommunity(int communityId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int currentUserId = string.IsNullOrEmpty(userId) ? 0 : Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT l.link_id, l.title, l.url, l.clicks, l.created_at,
                       u.user_id, u.username, u.avatar_url AS creator_avatar,
                       CASE WHEN lc.clicker_user_id IS NOT NULL THEN TRUE ELSE FALSE END AS is_clicked_by_me
                FROM links l
                JOIN users u ON u.user_id = l.user_id
                LEFT JOIN link_clicks lc ON lc.link_id = l.link_id AND lc.clicker_user_id = @uid
                WHERE l.community_id = @cid
                ORDER BY l.created_at DESC", conn);
            cmd.Parameters.AddWithValue("uid", currentUserId);
            cmd.Parameters.AddWithValue("cid", communityId);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    linkId        = Convert.ToInt32(reader["link_id"]),
                    title         = reader["title"]?.ToString(),
                    url           = reader["url"]?.ToString(),
                    clicks        = Convert.ToInt32(reader["clicks"]),
                    userId        = Convert.ToInt32(reader["user_id"]),
                    username      = reader["username"]?.ToString(),
                    creatorAvatar = reader["creator_avatar"] == DBNull.Value ? null : reader["creator_avatar"].ToString(),
                    createdAt     = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("created_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    isClickedByMe = Convert.ToBoolean(reader["is_clicked_by_me"])
                });
            }
            return Ok(list);
        }

        [HttpGet("community/{communityId:int}/count")]
        public IActionResult GetLinkCount(int communityId)
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM links WHERE community_id = @cid", conn);
            cmd.Parameters.AddWithValue("cid", communityId);
            return Ok(new { count = Convert.ToInt32(cmd.ExecuteScalar()) });
        }

        [HttpPost("{linkId:int}/click")]
        public IActionResult RegisterClick(int linkId, [FromQuery] string? referrer = null)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            using var checkCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM link_clicks WHERE link_id = @lid AND clicker_user_id = @uid", conn);
            checkCmd.Parameters.AddWithValue("lid", linkId);
            checkCmd.Parameters.AddWithValue("uid", uid);
            bool alreadyClicked = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            // INSERT ... ON CONFLICT replaces Oracle MERGE
            using var mergeCmd = new NpgsqlCommand(@"
                INSERT INTO link_clicks (link_id, clicker_user_id, referrer_page, click_count, clicked_at)
                VALUES (@lid, @uid, @ref, 1, NOW() AT TIME ZONE 'UTC')
                ON CONFLICT (link_id, clicker_user_id) DO UPDATE
                    SET click_count   = link_clicks.click_count + 1,
                        clicked_at    = NOW() AT TIME ZONE 'UTC',
                        referrer_page = COALESCE(@ref, link_clicks.referrer_page)", conn);
            mergeCmd.Parameters.AddWithValue("lid", linkId);
            mergeCmd.Parameters.AddWithValue("uid", uid);
            mergeCmd.Parameters.AddWithValue("ref", string.IsNullOrEmpty(referrer) ? DBNull.Value : (object)referrer);
            mergeCmd.ExecuteNonQuery();

            if (!alreadyClicked)
            {
                using var updateCmd = new NpgsqlCommand("UPDATE links SET clicks = clicks + 1 WHERE link_id = @lid", conn);
                updateCmd.Parameters.AddWithValue("lid", linkId);
                updateCmd.ExecuteNonQuery();

                using var isCreatorCmd = new NpgsqlCommand(@"
                    SELECT COUNT(*) FROM (
                        SELECT 1 FROM communities WHERE created_by = @uid
                        UNION ALL
                        SELECT 1 FROM links WHERE user_id = @uid LIMIT 1
                    ) x", conn);
                isCreatorCmd.Parameters.AddWithValue("uid", uid);
                bool isCreator = Convert.ToInt32(isCreatorCmd.ExecuteScalar()) > 0;

                if (isCreator)
                {
                    using var ownerCmd = new NpgsqlCommand("SELECT user_id FROM links WHERE link_id = @lid", conn);
                    ownerCmd.Parameters.AddWithValue("lid", linkId);
                    var ownerIdObj = ownerCmd.ExecuteScalar();
                    if (ownerIdObj != null && ownerIdObj != DBNull.Value)
                    {
                        int ownerId = Convert.ToInt32(ownerIdObj);
                        if (ownerId != uid)
                        {
                            using var nameCmd = new NpgsqlCommand("SELECT username FROM users WHERE user_id = @id", conn);
                            nameCmd.Parameters.AddWithValue("id", uid);
                            var clickerName = nameCmd.ExecuteScalar()?.ToString() ?? "A creator";

                            using var titleCmd = new NpgsqlCommand("SELECT title FROM links WHERE link_id = @lid", conn);
                            titleCmd.Parameters.AddWithValue("lid", linkId);
                            var linkTitle = titleCmd.ExecuteScalar()?.ToString() ?? "your link";

                            using var notifCmd = new NpgsqlCommand(@"
                                INSERT INTO notifications (user_id, sender_id, type, message)
                                VALUES (@oid, @sid, 'CREATOR_CLICKED_YOUR_LINK', @msg)", conn);
                            notifCmd.Parameters.AddWithValue("oid", ownerId);
                            notifCmd.Parameters.AddWithValue("sid", uid);
                            notifCmd.Parameters.AddWithValue("msg", $"@{clickerName} (creator) clicked your link \"{linkTitle}\"! +1 point");
                            notifCmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            return Ok(new { message = alreadyClicked ? "Already clicked" : "Click registered", alreadyClicked });
        }

        [HttpGet("supporters/{linkId:int}")]
        public IActionResult GetClickers(int linkId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            using var linkCheck = new NpgsqlCommand("SELECT user_id FROM links WHERE link_id = @lid", conn);
            linkCheck.Parameters.AddWithValue("lid", linkId);
            var ownerIdObj = linkCheck.ExecuteScalar();
            if (ownerIdObj == null || ownerIdObj == DBNull.Value)
                return NotFound(new { message = "Link not found." });
            int ownerId = Convert.ToInt32(ownerIdObj);

            int totalClicks = 0, uniqueUsers = 0, creatorClicks = 0;
            using (var statsCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) AS total_clicks,
                       COUNT(DISTINCT lc.clicker_user_id) AS unique_users,
                       SUM(CASE WHEN (SELECT COUNT(*) FROM communities WHERE created_by = lc.clicker_user_id) > 0
                                OR (SELECT COUNT(*) FROM links WHERE user_id = lc.clicker_user_id LIMIT 1) > 0
                                THEN 1 ELSE 0 END) AS creator_clicks
                FROM link_clicks lc WHERE lc.link_id = @lid", conn))
            {
                statsCmd.Parameters.AddWithValue("lid", linkId);
                using var sr = statsCmd.ExecuteReader();
                if (sr.Read())
                {
                    totalClicks   = Convert.ToInt32(sr["total_clicks"]);
                    uniqueUsers   = Convert.ToInt32(sr["unique_users"]);
                    creatorClicks = sr["creator_clicks"] == DBNull.Value ? 0 : Convert.ToInt32(sr["creator_clicks"]);
                }
            }

            var supporters = new List<object>();
            if (ownerId == uid)
            {
                using var clickersCmd = new NpgsqlCommand(@"
                    SELECT u.user_id, u.username, u.avatar_url, lc.clicked_at, lc.referrer_page,
                           COALESCE(lc.click_count, 1) AS click_count,
                           CASE WHEN (SELECT COUNT(*) FROM communities WHERE created_by = u.user_id) > 0
                                OR (SELECT COUNT(*) FROM links WHERE user_id = u.user_id LIMIT 1) > 0
                                THEN TRUE ELSE FALSE END AS is_creator,
                           CASE WHEN u.user_id = @oid THEN 'SELF'
                                ELSE COALESCE((SELECT status FROM follows WHERE follower_id = @oid AND following_id = u.user_id), 'NONE')
                           END AS follow_status
                    FROM link_clicks lc
                    JOIN users u ON u.user_id = lc.clicker_user_id
                    WHERE lc.link_id = @lid
                    ORDER BY is_creator DESC, COALESCE(lc.click_count, 1) DESC, lc.clicked_at DESC", conn);
                clickersCmd.Parameters.AddWithValue("oid", ownerId);
                clickersCmd.Parameters.AddWithValue("lid", linkId);
                using var reader = clickersCmd.ExecuteReader();
                while (reader.Read())
                {
                    supporters.Add(new
                    {
                        userId       = Convert.ToInt32(reader["user_id"]),
                        username     = reader["username"].ToString(),
                        avatarUrl    = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(),
                        clickedAt    = reader.GetDateTime(reader.GetOrdinal("clicked_at")).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        referrerPage = reader["referrer_page"] == DBNull.Value ? null : reader["referrer_page"].ToString(),
                        clickCount   = Convert.ToInt32(reader["click_count"]),
                        isCreator    = Convert.ToBoolean(reader["is_creator"]),
                        followStatus = reader["follow_status"].ToString()
                    });
                }
            }

            return Ok(new { totalClicks, uniqueUsers, creatorClicks, supporters });
        }

        [HttpPost("{linkId:int}/shoutout/{targetUserId:int}")]
        public IActionResult ShoutOut(int linkId, int targetUserId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            using (var check = new NpgsqlCommand("SELECT COUNT(*) FROM links WHERE link_id = @lid AND user_id = @uid", conn))
            {
                check.Parameters.AddWithValue("lid", linkId);
                check.Parameters.AddWithValue("uid", uid);
                if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Only the link owner can send shout outs." });
            }

            using var nameCmd = new NpgsqlCommand("SELECT username FROM users WHERE user_id = @id", conn);
            nameCmd.Parameters.AddWithValue("id", uid);
            var senderName = nameCmd.ExecuteScalar()?.ToString() ?? "A creator";

            using var notifCmd = new NpgsqlCommand(@"
                INSERT INTO notifications (user_id, sender_id, type, message)
                VALUES (@tid, @sid, 'SHOUT_OUT', @msg)", conn);
            notifCmd.Parameters.AddWithValue("tid", targetUserId);
            notifCmd.Parameters.AddWithValue("sid", uid);
            notifCmd.Parameters.AddWithValue("msg", $"@{senderName} gave you a shout out for supporting their link!");
            notifCmd.ExecuteNonQuery();
            return Ok(new { message = "Shout out sent!" });
        }

        [HttpPost("{linkId:int}/like")]
        public IActionResult ToggleLike(int linkId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            bool alreadyLiked;
            using (var check = new NpgsqlCommand("SELECT COUNT(*) FROM link_likes WHERE link_id = @lid AND user_id = @uid", conn))
            {
                check.Parameters.AddWithValue("lid", linkId);
                check.Parameters.AddWithValue("uid", uid);
                alreadyLiked = Convert.ToInt32(check.ExecuteScalar()) > 0;
            }

            if (alreadyLiked)
            {
                using var del = new NpgsqlCommand("DELETE FROM link_likes WHERE link_id = @lid AND user_id = @uid", conn);
                del.Parameters.AddWithValue("lid", linkId);
                del.Parameters.AddWithValue("uid", uid);
                del.ExecuteNonQuery();
            }
            else
            {
                using var ins = new NpgsqlCommand("INSERT INTO link_likes (link_id, user_id) VALUES (@lid, @uid)", conn);
                ins.Parameters.AddWithValue("lid", linkId);
                ins.Parameters.AddWithValue("uid", uid);
                ins.ExecuteNonQuery();
            }

            using var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM link_likes WHERE link_id = @lid", conn);
            countCmd.Parameters.AddWithValue("lid", linkId);
            return Ok(new { liked = !alreadyLiked, likeCount = Convert.ToInt32(countCmd.ExecuteScalar()) });
        }

        [HttpGet("{linkId:int}/likes")]
        public IActionResult GetLikes(int linkId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int uid = string.IsNullOrEmpty(userId) ? 0 : Convert.ToInt32(userId);
            using var conn = _db.GetConnection();
            conn.Open();

            int likeCount;
            using (var c = new NpgsqlCommand("SELECT COUNT(*) FROM link_likes WHERE link_id = @lid", conn))
            { c.Parameters.AddWithValue("lid", linkId); likeCount = Convert.ToInt32(c.ExecuteScalar()); }

            bool isLikedByMe;
            using (var c = new NpgsqlCommand("SELECT COUNT(*) FROM link_likes WHERE link_id = @lid AND user_id = @uid", conn))
            { c.Parameters.AddWithValue("lid", linkId); c.Parameters.AddWithValue("uid", uid); isLikedByMe = Convert.ToInt32(c.ExecuteScalar()) > 0; }

            return Ok(new { likeCount, isLikedByMe });
        }

        [HttpGet("{linkId:int}/comments")]
        public IActionResult GetComments(int linkId)
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT lc.comment_id, lc.user_id, lc.content, lc.created_at, u.username
                FROM link_comments lc
                JOIN users u ON u.user_id = lc.user_id
                WHERE lc.link_id = @lid ORDER BY lc.created_at ASC", conn);
            cmd.Parameters.AddWithValue("lid", linkId);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    commentId = Convert.ToInt32(reader["comment_id"]),
                    userId    = Convert.ToInt32(reader["user_id"]),
                    username  = reader["username"].ToString(),
                    content   = reader["content"].ToString(),
                    createdAt = reader.GetDateTime(reader.GetOrdinal("created_at")).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpPost("{linkId:int}/comments")]
        public IActionResult AddComment(int linkId, [FromBody] AddCommentDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            if (string.IsNullOrWhiteSpace(dto.Content)) return BadRequest(new { message = "Comment cannot be empty." });
            dto.Content = dto.Content.Trim();
            if (dto.Content.Length > 500) return BadRequest(new { message = "Comment must not exceed 500 characters." });

            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO link_comments (link_id, user_id, content) VALUES (@lid, @uid, @content)", conn);
            cmd.Parameters.AddWithValue("lid",     linkId);
            cmd.Parameters.AddWithValue("uid",     uid);
            cmd.Parameters.AddWithValue("content", dto.Content);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Comment added" });
        }
    }

    public class AddCommentDto { public string Content { get; set; } = ""; }
}
