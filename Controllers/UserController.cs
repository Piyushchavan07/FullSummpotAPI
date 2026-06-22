using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using FullSummpotAPI.Data;
using FullSummpotAPI.DTOs;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        private readonly PasswordService _passwordService;
        private static readonly HashSet<string> AllowedNiches = new(StringComparer.OrdinalIgnoreCase)
        { "Gaming","Tech","Education","Music","Comedy","Vlogging","Finance","Fitness","Food","Travel","Other" };

        public UserController(NpgsqlDbContext db, PasswordService passwordService)
        { _db = db; _passwordService = passwordService; }

        private int GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrEmpty(claim) ? 0 : Convert.ToInt32(claim);
        }

        [HttpGet("search")]
        public IActionResult Search([FromQuery] string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Ok(new { users = Array.Empty<object>(), communities = Array.Empty<object>() });

            int uid = GetCurrentUserId();
            using var conn = _db.GetConnection();
            conn.Open();

            using var userCmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.avatar_url,
                       COALESCE(f.status, 'NONE') AS follow_status
                FROM users u
                LEFT JOIN follows f ON f.following_id = u.user_id AND f.follower_id = @uid
                WHERE LOWER(u.username) LIKE @q AND u.user_id != @uid
                LIMIT 30", conn);
            userCmd.Parameters.AddWithValue("uid", uid);
            userCmd.Parameters.AddWithValue("q", $"%{query.ToLower()}%");
            using var userReader = userCmd.ExecuteReader();
            var users = new List<object>();
            while (userReader.Read())
            {
                users.Add(new
                {
                    userId       = Convert.ToInt32(userReader["user_id"]),
                    username     = userReader["username"].ToString(),
                    avatarUrl    = userReader["avatar_url"] == DBNull.Value ? null : userReader["avatar_url"].ToString(),
                    followStatus = userReader["follow_status"].ToString()
                });
            }
            userReader.Close();

            using var commCmd = new NpgsqlCommand(@"
                SELECT c.community_id, c.name, c.niche, u.username AS creator_name
                FROM communities c JOIN users u ON c.created_by = u.user_id
                WHERE LOWER(c.name) LIKE @q OR LOWER(c.niche) LIKE @q OR LOWER(u.username) LIKE @q
                LIMIT 30", conn);
            commCmd.Parameters.AddWithValue("q", $"%{query.ToLower()}%");
            using var commReader = commCmd.ExecuteReader();
            var communities = new List<object>();
            while (commReader.Read())
            {
                communities.Add(new
                {
                    communityId = Convert.ToInt32(commReader["community_id"]),
                    name        = commReader["name"].ToString(),
                    niche       = commReader["niche"].ToString(),
                    creatorName = commReader["creator_name"].ToString()
                });
            }
            return Ok(new { users, communities });
        }

        [HttpGet("profile")]
        public IActionResult GetProfile()
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT user_id, username, email, content_niche,
                       available_points, avatar_url, created_at,
                       phone_number, is_email_verified, is_phone_verified
                FROM users WHERE user_id = @id", conn);
            cmd.Parameters.AddWithValue("id", uid);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound();
            return Ok(new
            {
                id              = Convert.ToInt32(reader["user_id"]),
                username        = reader["username"].ToString(),
                email           = reader["email"].ToString(),
                contentNiche    = reader["content_niche"] == DBNull.Value ? null : reader["content_niche"].ToString(),
                availablePoints = reader["available_points"] == DBNull.Value ? 0 : Convert.ToInt32(reader["available_points"]),
                avatarUrl       = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(),
                createdAt       = reader["created_at"] == DBNull.Value ? null : Convert.ToDateTime(reader["created_at"]).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                phoneNumber     = reader["phone_number"] == DBNull.Value ? null : reader["phone_number"].ToString(),
                isEmailVerified = Convert.ToBoolean(reader["is_email_verified"]),
                isPhoneVerified = Convert.ToBoolean(reader["is_phone_verified"])
            });
        }

        [HttpGet("{id:int}/public-profile")]
        public IActionResult GetPublicProfile(int id)
        {
            int uid = GetCurrentUserId();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.content_niche, u.available_points, u.avatar_url, u.created_at,
                    (SELECT COUNT(*) FROM follows WHERE following_id = u.user_id AND status = 'ACCEPTED') AS followers_count,
                    (SELECT COUNT(*) FROM follows WHERE follower_id  = u.user_id AND status = 'ACCEPTED') AS following_count,
                    (SELECT COUNT(*) FROM communities WHERE created_by = u.user_id) AS communities_created,
                    (SELECT COUNT(*) FROM links WHERE user_id = u.user_id) AS links_submitted,
                    (SELECT COALESCE(SUM(clicks),0) FROM links WHERE user_id = u.user_id) AS total_clicks
                FROM users u WHERE u.user_id = @id", conn);
            cmd.Parameters.AddWithValue("id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound(new { message = "User not found" });

            string? followStatus = null;
            if (uid != 0 && uid != id)
            {
                reader.Close();
                using var followCmd = new NpgsqlCommand(
                    "SELECT status FROM follows WHERE follower_id = @f AND following_id = @t", conn);
                followCmd.Parameters.AddWithValue("f", uid);
                followCmd.Parameters.AddWithValue("t", id);
                var result = followCmd.ExecuteScalar();
                followStatus = (result == null || result == DBNull.Value) ? "NONE" : result.ToString();

                using var cmd2 = new NpgsqlCommand(@"
                    SELECT u.user_id, u.username, u.content_niche, u.available_points, u.avatar_url, u.created_at,
                        (SELECT COUNT(*) FROM follows WHERE following_id = u.user_id AND status = 'ACCEPTED') AS followers_count,
                        (SELECT COUNT(*) FROM follows WHERE follower_id  = u.user_id AND status = 'ACCEPTED') AS following_count,
                        (SELECT COUNT(*) FROM communities WHERE created_by = u.user_id) AS communities_created,
                        (SELECT COUNT(*) FROM links WHERE user_id = u.user_id) AS links_submitted,
                        (SELECT COALESCE(SUM(clicks),0) FROM links WHERE user_id = u.user_id) AS total_clicks
                    FROM users u WHERE u.user_id = @id", conn);
                cmd2.Parameters.AddWithValue("id", id);
                using var reader2 = cmd2.ExecuteReader();
                if (!reader2.Read()) return NotFound(new { message = "User not found" });
                return Ok(BuildPublicProfile(reader2, followStatus, uid, id));
            }

            return Ok(BuildPublicProfile(reader, followStatus, uid, id));
        }

        private static object BuildPublicProfile(NpgsqlDataReader reader, string? followStatus, int uid, int id) => new
        {
            userId             = Convert.ToInt32(reader["user_id"]),
            username           = reader["username"].ToString(),
            contentNiche       = reader["content_niche"] == DBNull.Value ? null : reader["content_niche"].ToString(),
            availablePoints    = Convert.ToInt32(reader["available_points"]),
            avatarUrl          = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(),
            createdAt          = reader["created_at"] == DBNull.Value ? null : Convert.ToDateTime(reader["created_at"]).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            followersCount     = Convert.ToInt32(reader["followers_count"]),
            followingCount     = Convert.ToInt32(reader["following_count"]),
            communitiesCreated = Convert.ToInt32(reader["communities_created"]),
            linksSubmitted     = Convert.ToInt32(reader["links_submitted"]),
            totalClicks        = Convert.ToInt32(reader["total_clicks"]),
            followStatus,
            isOwnProfile       = uid == id
        };

        [HttpPut("profile")]
        public IActionResult UpdateProfile([FromBody] UpdateProfileDto dto)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();

            if (!string.IsNullOrEmpty(dto.Username))
            {
                if (dto.Username.Length < 3 || dto.Username.Length > 30)
                    return BadRequest(new { message = "Username must be 3-30 characters." });
                if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Username, @"^[a-zA-Z0-9_]+$"))
                    return BadRequest(new { message = "Username can only contain letters, numbers and underscores." });
            }

            if (!string.IsNullOrEmpty(dto.ContentNiche) && !AllowedNiches.Contains(dto.ContentNiche))
                return BadRequest(new { message = "Invalid niche selected." });

            using var conn = _db.GetConnection();
            conn.Open();

            if (!string.IsNullOrEmpty(dto.Username))
            {
                using var checkCmd = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM users WHERE LOWER(username) = LOWER(@u) AND user_id != @id", conn);
                checkCmd.Parameters.AddWithValue("u", dto.Username);
                checkCmd.Parameters.AddWithValue("id", uid);
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "Username is already taken." });
            }

            using var cmd = new NpgsqlCommand(@"
                UPDATE users SET
                    username      = COALESCE(@username, username),
                    content_niche = COALESCE(@niche, content_niche)
                WHERE user_id = @id", conn);
            cmd.Parameters.AddWithValue("username", string.IsNullOrEmpty(dto.Username) ? DBNull.Value : (object)dto.Username);
            cmd.Parameters.AddWithValue("niche",    string.IsNullOrEmpty(dto.ContentNiche) ? DBNull.Value : (object)dto.ContentNiche);
            cmd.Parameters.AddWithValue("id", uid);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Profile updated successfully" });
        }

        [HttpPost("upload-avatar")]
        public async Task<IActionResult> UploadAvatar(IFormFile file)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            if (file == null || file.Length == 0) return BadRequest(new { message = "No file uploaded." });
            if (file.Length > 5 * 1024 * 1024) return BadRequest(new { message = "File must be under 5 MB." });
            var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "image/gif" };
            if (!allowedTypes.Contains(file.ContentType.ToLower())) return BadRequest(new { message = "Only JPEG, PNG, WebP and GIF images are allowed." });
            var ext = Path.GetExtension(file.FileName).ToLower();
            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
            if (!allowedExts.Contains(ext)) return BadRequest(new { message = "Invalid file extension." });

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "avatars");
            Directory.CreateDirectory(uploadsPath);

            using (var conn2 = _db.GetConnection())
            {
                conn2.Open();
                using var oldCmd = new NpgsqlCommand("SELECT avatar_url FROM users WHERE user_id = @id", conn2);
                oldCmd.Parameters.AddWithValue("id", uid);
                var oldUrl = oldCmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(oldUrl))
                {
                    var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", oldUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }
            }

            var safeFileName = $"avatar_{uid}_{DateTime.UtcNow.Ticks}{ext}";
            var filePath = Path.Combine(uploadsPath, safeFileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            var publicUrl = $"/uploads/avatars/{safeFileName}";
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand("UPDATE users SET avatar_url = @url WHERE user_id = @id", conn);
            cmd.Parameters.AddWithValue("url", publicUrl);
            cmd.Parameters.AddWithValue("id",  uid);
            cmd.ExecuteNonQuery();
            return Ok(new { avatarUrl = publicUrl });
        }

        [HttpPost("follow/{id:int}")]
        public IActionResult Follow(int id)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            if (uid == id) return BadRequest(new { message = "You cannot follow yourself." });

            using var conn = _db.GetConnection();
            conn.Open();

            using (var existsCmd = new NpgsqlCommand("SELECT COUNT(*) FROM users WHERE user_id = @id", conn))
            {
                existsCmd.Parameters.AddWithValue("id", id);
                if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0) return NotFound(new { message = "User not found." });
            }

            using (var checkCmd = new NpgsqlCommand("SELECT COUNT(*) FROM follows WHERE follower_id = @f AND following_id = @t", conn))
            {
                checkCmd.Parameters.AddWithValue("f", uid);
                checkCmd.Parameters.AddWithValue("t", id);
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "Already following or request pending." });
            }

            using (var followCmd = new NpgsqlCommand("INSERT INTO follows (follower_id, following_id, status) VALUES (@f, @t, 'PENDING')", conn))
            {
                followCmd.Parameters.AddWithValue("f", uid);
                followCmd.Parameters.AddWithValue("t", id);
                followCmd.ExecuteNonQuery();
            }

            using var nameCmd = new NpgsqlCommand("SELECT username FROM users WHERE user_id = @id", conn);
            nameCmd.Parameters.AddWithValue("id", uid);
            var senderName = nameCmd.ExecuteScalar()?.ToString() ?? $"User {uid}";

            using var notifCmd = new NpgsqlCommand(@"
                INSERT INTO notifications (user_id, sender_id, type, message)
                VALUES (@u, @s, 'FOLLOW_REQUEST', @m)", conn);
            notifCmd.Parameters.AddWithValue("u", id);
            notifCmd.Parameters.AddWithValue("s", uid);
            notifCmd.Parameters.AddWithValue("m", $"{senderName} wants to follow you.");
            notifCmd.ExecuteNonQuery();
            return Ok(new { message = "Follow request sent" });
        }

        [HttpPost("follow-accept/{senderId:int}")]
        public IActionResult AcceptFollow(int senderId)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var updateCmd = new NpgsqlCommand(
                "UPDATE follows SET status = 'ACCEPTED' WHERE follower_id = @s AND following_id = @u AND status = 'PENDING'", conn);
            updateCmd.Parameters.AddWithValue("s", senderId);
            updateCmd.Parameters.AddWithValue("u", uid);
            if (updateCmd.ExecuteNonQuery() == 0) return BadRequest(new { message = "No pending request found." });

            using var delNotif = new NpgsqlCommand(
                "DELETE FROM notifications WHERE user_id = @u AND sender_id = @s AND type = 'FOLLOW_REQUEST'", conn);
            delNotif.Parameters.AddWithValue("u", uid);
            delNotif.Parameters.AddWithValue("s", senderId);
            delNotif.ExecuteNonQuery();
            return Ok(new { message = "Follow request accepted" });
        }

        [HttpPost("follow-decline/{senderId:int}")]
        public IActionResult DeclineFollow(int senderId)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "DELETE FROM follows WHERE follower_id = @s AND following_id = @u AND status = 'PENDING'", conn);
            cmd.Parameters.AddWithValue("s", senderId);
            cmd.Parameters.AddWithValue("u", uid);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Follow request declined" });
        }

        [HttpPost("{id:int}/unfollow")]
        public IActionResult Unfollow(int id)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand("DELETE FROM follows WHERE follower_id = @f AND following_id = @t", conn);
            cmd.Parameters.AddWithValue("f", uid);
            cmd.Parameters.AddWithValue("t", id);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Unfollowed" });
        }

        [HttpPost("{id:int}/block")]
        public IActionResult Block(int id)
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            if (uid == id) return BadRequest(new { message = "Cannot block yourself." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                DELETE FROM follows
                WHERE (follower_id = @a AND following_id = @b)
                   OR (follower_id = @b AND following_id = @a)", conn);
            cmd.Parameters.AddWithValue("a", uid);
            cmd.Parameters.AddWithValue("b", id);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "User blocked" });
        }

        [HttpPost("{id:int}/unblock")]
        public IActionResult Unblock(int id) => Ok(new { message = "User unblocked" });

        [HttpGet("followers")]
        public IActionResult GetFollowers()
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.avatar_url
                FROM follows f JOIN users u ON f.follower_id = u.user_id
                WHERE f.following_id = @u AND f.status = 'ACCEPTED'", conn);
            cmd.Parameters.AddWithValue("u", uid);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
                list.Add(new { userId = Convert.ToInt32(reader["user_id"]), username = reader["username"].ToString(), avatarUrl = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString() });
            return Ok(list);
        }

        [HttpGet("following")]
        public IActionResult GetFollowing()
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.avatar_url,
                       (SELECT COUNT(*) FROM communities WHERE created_by = u.user_id) AS community_count
                FROM follows f JOIN users u ON f.following_id = u.user_id
                WHERE f.follower_id = @u AND f.status = 'ACCEPTED'", conn);
            cmd.Parameters.AddWithValue("u", uid);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
                list.Add(new { userId = Convert.ToInt32(reader["user_id"]), username = reader["username"].ToString(), avatarUrl = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(), communityCount = Convert.ToInt32(reader["community_count"]) });
            return Ok(list);
        }

        [HttpGet("notifications")]
        public IActionResult GetNotifications()
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT n.notification_id, n.sender_id, n.type, n.message,
                       n.is_read, n.created_at, u.username AS sender_name
                FROM notifications n JOIN users u ON n.sender_id = u.user_id
                WHERE n.user_id = @u ORDER BY n.created_at DESC LIMIT 50", conn);
            cmd.Parameters.AddWithValue("u", uid);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    id         = Convert.ToInt32(reader["notification_id"]),
                    senderId   = Convert.ToInt32(reader["sender_id"]),
                    senderName = reader["sender_name"].ToString(),
                    type       = reader["type"].ToString(),
                    message    = reader["message"].ToString(),
                    isRead     = Convert.ToBoolean(reader["is_read"]),
                    createdAt  = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("created_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpPost("notifications/read-all")]
        public IActionResult MarkNotificationsRead()
        {
            int uid = GetCurrentUserId();
            if (uid == 0) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand("UPDATE notifications SET is_read = TRUE WHERE user_id = @u", conn);
            cmd.Parameters.AddWithValue("u", uid);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "All notifications marked as read" });
        }
    }
}
