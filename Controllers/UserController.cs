using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using FullSummpotAPI.Data;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly OracleDbContext _db;
        private readonly PasswordService _passwordService;
        private static readonly HashSet<string> AllowedNiches = new(StringComparer.OrdinalIgnoreCase)
        {
            "Gaming","Tech","Education","Music","Comedy","Vlogging",
            "Finance","Fitness","Food","Travel","Other"
        };

        public UserController(OracleDbContext db, PasswordService passwordService)
        {
            _db = db;
            _passwordService = passwordService;
        }

        private int GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrEmpty(claim) ? 0 : Convert.ToInt32(claim);
        }

        // -- Search --------------------------------------------------------------
        [HttpGet("search")]
        public IActionResult Search([FromQuery] string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Ok(new { users = Array.Empty<object>(), communities = Array.Empty<object>() });

            int currentUserId = GetCurrentUserId();

            using var conn = _db.GetConnection();
            conn.Open();

            var userCmd = new OracleCommand(@"
                SELECT u.USER_ID, u.USERNAME, u.AVATAR_URL,
                       NVL(f.STATUS, 'NONE') as FOLLOW_STATUS
                FROM USERS u
                LEFT JOIN FOLLOWS f ON f.FOLLOWING_ID = u.USER_ID AND f.FOLLOWER_ID = :currentUserId
                WHERE LOWER(u.USERNAME) LIKE :q
                  AND u.USER_ID != :currentUserId
                FETCH FIRST 30 ROWS ONLY", conn);
            userCmd.BindByName = true;
            userCmd.Parameters.Add("currentUserId", OracleDbType.Int32).Value = currentUserId;
            userCmd.Parameters.Add("q", OracleDbType.Varchar2).Value = $"%{query.ToLower()}%";

            using var userReader = userCmd.ExecuteReader();
            var users = new List<object>();
            while (userReader.Read())
            {
                users.Add(new
                {
                    userId    = Convert.ToInt32(userReader["USER_ID"]),
                    username  = userReader["USERNAME"].ToString(),
                    avatarUrl = userReader["AVATAR_URL"] == DBNull.Value ? null : userReader["AVATAR_URL"].ToString(),
                    followStatus = userReader["FOLLOW_STATUS"].ToString()
                });
            }

            var commCmd = new OracleCommand(@"
                SELECT c.COMMUNITY_ID, c.NAME, c.NICHE, u.USERNAME as CREATOR_NAME
                FROM COMMUNITIES c
                JOIN USERS u ON c.CREATED_BY = u.USER_ID
                WHERE LOWER(c.NAME) LIKE :q OR LOWER(c.NICHE) LIKE :q OR LOWER(u.USERNAME) LIKE :q
                FETCH FIRST 30 ROWS ONLY", conn);
            commCmd.BindByName = true;
            commCmd.Parameters.Add("q", OracleDbType.Varchar2).Value = $"%{query.ToLower()}%";

            using var commReader = commCmd.ExecuteReader();
            var communities = new List<object>();
            while (commReader.Read())
            {
                communities.Add(new
                {
                    communityId  = Convert.ToInt32(commReader["COMMUNITY_ID"]),
                    name         = commReader["NAME"].ToString(),
                    niche        = commReader["NICHE"].ToString(),
                    creatorName  = commReader["CREATOR_NAME"].ToString()
                });
            }

            return Ok(new { users, communities });
        }

        // -- Profile -------------------------------------------------------------
        [HttpGet("profile")]
        public IActionResult GetProfile()
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT USER_ID, USERNAME, EMAIL, CONTENT_NICHE,
                       AVAILABLE_POINTS, AVATAR_URL, CREATED_AT,
                       PHONE_NUMBER, IS_EMAIL_VERIFIED, IS_PHONE_VERIFIED
                FROM USERS WHERE USER_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound();

            return Ok(new
            {
                id             = Convert.ToInt32(reader["USER_ID"]),
                username       = reader["USERNAME"].ToString(),
                email          = reader["EMAIL"].ToString(),
                contentNiche   = reader["CONTENT_NICHE"] == DBNull.Value ? null : reader["CONTENT_NICHE"].ToString(),
                availablePoints= reader["AVAILABLE_POINTS"] == DBNull.Value ? 0 : Convert.ToInt32(reader["AVAILABLE_POINTS"]),
                avatarUrl      = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                createdAt      = reader["CREATED_AT"] == DBNull.Value ? null : Convert.ToDateTime(reader["CREATED_AT"]).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                phoneNumber    = reader["PHONE_NUMBER"] == DBNull.Value ? null : reader["PHONE_NUMBER"].ToString(),
                isEmailVerified = Convert.ToInt32(reader["IS_EMAIL_VERIFIED"]) == 1,
                isPhoneVerified = Convert.ToInt32(reader["IS_PHONE_VERIFIED"]) == 1
            });
        }

        [HttpGet("{id:int}/public-profile")]
        public IActionResult GetPublicProfile(int id)
        {
            int currentUserId = GetCurrentUserId();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT u.USER_ID, u.USERNAME, u.CONTENT_NICHE, u.AVAILABLE_POINTS,
                       u.AVATAR_URL, u.CREATED_AT,
                       (SELECT COUNT(*) FROM FOLLOWS WHERE FOLLOWING_ID = u.USER_ID AND STATUS = 'ACCEPTED') as FOLLOWERS_COUNT,
                       (SELECT COUNT(*) FROM FOLLOWS WHERE FOLLOWER_ID  = u.USER_ID AND STATUS = 'ACCEPTED') as FOLLOWING_COUNT,
                       (SELECT COUNT(*) FROM COMMUNITIES WHERE CREATED_BY = u.USER_ID) as COMMUNITIES_CREATED,
                       (SELECT COUNT(*) FROM LINKS WHERE USER_ID = u.USER_ID)          as LINKS_SUBMITTED,
                       (SELECT NVL(SUM(CLICKS),0) FROM LINKS WHERE USER_ID = u.USER_ID) as TOTAL_CLICKS
                FROM USERS u WHERE u.USER_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = id;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound(new { message = "User not found" });

            string? followStatus = null;
            if (currentUserId != 0 && currentUserId != id)
            {
                var followCmd = new OracleCommand(
                    "SELECT STATUS FROM FOLLOWS WHERE FOLLOWER_ID = :f AND FOLLOWING_ID = :t", conn);
                followCmd.BindByName = true;
                followCmd.Parameters.Add("f", OracleDbType.Int32).Value = currentUserId;
                followCmd.Parameters.Add("t", OracleDbType.Int32).Value = id;
                var result = followCmd.ExecuteScalar();
                followStatus = (result == null || result == DBNull.Value) ? "NONE" : result.ToString();
            }

            return Ok(new
            {
                userId              = Convert.ToInt32(reader["USER_ID"]),
                username            = reader["USERNAME"].ToString(),
                contentNiche        = reader["CONTENT_NICHE"] == DBNull.Value ? null : reader["CONTENT_NICHE"].ToString(),
                availablePoints     = Convert.ToInt32(reader["AVAILABLE_POINTS"]),
                avatarUrl           = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                createdAt           = reader["CREATED_AT"] == DBNull.Value ? null : Convert.ToDateTime(reader["CREATED_AT"]).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                followersCount      = Convert.ToInt32(reader["FOLLOWERS_COUNT"]),
                followingCount      = Convert.ToInt32(reader["FOLLOWING_COUNT"]),
                communitiesCreated  = Convert.ToInt32(reader["COMMUNITIES_CREATED"]),
                linksSubmitted      = Convert.ToInt32(reader["LINKS_SUBMITTED"]),
                totalClicks         = Convert.ToInt32(reader["TOTAL_CLICKS"]),
                followStatus        = followStatus,
                isOwnProfile        = currentUserId == id
            });
        }

        [HttpPut("profile")]
        public IActionResult UpdateProfile([FromBody] UpdateProfileDto dto)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            if (!string.IsNullOrEmpty(dto.Username))
            {
                if (dto.Username.Length < 3 || dto.Username.Length > 30)
                    return BadRequest(new { message = "Username must be 3�30 characters." });

                // Only allow alphanumeric + underscore
                if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Username, @"^[a-zA-Z0-9_]+$"))
                    return BadRequest(new { message = "Username can only contain letters, numbers and underscores." });
            }

            if (!string.IsNullOrEmpty(dto.ContentNiche) && !AllowedNiches.Contains(dto.ContentNiche))
                return BadRequest(new { message = "Invalid niche selected." });

            using var conn = _db.GetConnection();
            conn.Open();

            if (!string.IsNullOrEmpty(dto.Username))
            {
                var checkCmd = new OracleCommand(
                    "SELECT COUNT(*) FROM USERS WHERE LOWER(USERNAME) = LOWER(:username) AND USER_ID != :id", conn);
                checkCmd.BindByName = true;
                checkCmd.Parameters.Add("username", OracleDbType.Varchar2).Value = dto.Username;
                checkCmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "Username is already taken." });
            }

            var cmd = new OracleCommand(@"
                UPDATE USERS SET
                    USERNAME      = COALESCE(:username, USERNAME),
                    CONTENT_NICHE = COALESCE(:niche, CONTENT_NICHE)
                WHERE USER_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("username", OracleDbType.Varchar2).Value =
                string.IsNullOrEmpty(dto.Username) ? (object)DBNull.Value : dto.Username;
            cmd.Parameters.Add("niche", OracleDbType.Varchar2).Value =
                string.IsNullOrEmpty(dto.ContentNiche) ? (object)DBNull.Value : dto.ContentNiche;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
            cmd.ExecuteNonQuery();

            return Ok(new { message = "Profile updated successfully" });
        }

        // -- Avatar upload --------------------------------------------------------
        [HttpPost("upload-avatar")]
        public async Task<IActionResult> UploadAvatar(IFormFile file)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            if (file == null || file.Length == 0)
                return BadRequest(new { message = "No file uploaded." });

            if (file.Length > 5 * 1024 * 1024)
                return BadRequest(new { message = "File must be under 5 MB." });

            var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "image/gif" };
            if (!allowedTypes.Contains(file.ContentType.ToLower()))
                return BadRequest(new { message = "Only JPEG, PNG, WebP and GIF images are allowed." });

            var ext = Path.GetExtension(file.FileName).ToLower();
            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
            if (!allowedExts.Contains(ext))
                return BadRequest(new { message = "Invalid file extension." });

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "avatars");
            Directory.CreateDirectory(uploadsPath);

            // Delete old avatar file if it exists
            using (var conn2 = _db.GetConnection())
            {
                conn2.Open();
                var oldCmd = new OracleCommand("SELECT AVATAR_URL FROM USERS WHERE USER_ID = :id", conn2);
                oldCmd.BindByName = true;
                oldCmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
                var oldUrl = oldCmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(oldUrl))
                {
                    var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", oldUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }
            }

            var safeFileName = $"avatar_{currentUserId}_{DateTime.UtcNow.Ticks}{ext}";
            var filePath = Path.Combine(uploadsPath, safeFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            var publicUrl = $"/uploads/avatars/{safeFileName}";

            using var conn = _db.GetConnection();
            conn.Open();
            var cmd = new OracleCommand("UPDATE USERS SET AVATAR_URL = :url WHERE USER_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("url", OracleDbType.Varchar2).Value = publicUrl;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
            cmd.ExecuteNonQuery();

            return Ok(new { avatarUrl = publicUrl });
        }

        // -- Follow system --------------------------------------------------------
        [HttpPost("follow/{id:int}")]
        public IActionResult Follow(int id)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();
            if (currentUserId == id) return BadRequest(new { message = "You cannot follow yourself." });

            using var conn = _db.GetConnection();
            conn.Open();

            // Check target user exists
            var existsCmd = new OracleCommand("SELECT COUNT(*) FROM USERS WHERE USER_ID = :id", conn);
            existsCmd.BindByName = true;
            existsCmd.Parameters.Add("id", OracleDbType.Int32).Value = id;
            if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0)
                return NotFound(new { message = "User not found." });

            var checkCmd = new OracleCommand(
                "SELECT COUNT(*) FROM FOLLOWS WHERE FOLLOWER_ID = :f AND FOLLOWING_ID = :t", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("f", OracleDbType.Int32).Value = currentUserId;
            checkCmd.Parameters.Add("t", OracleDbType.Int32).Value = id;
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                return BadRequest(new { message = "Already following or request pending." });

            var followCmd = new OracleCommand(
                "INSERT INTO FOLLOWS (FOLLOWER_ID, FOLLOWING_ID, STATUS) VALUES (:f, :t, 'PENDING')", conn);
            followCmd.BindByName = true;
            followCmd.Parameters.Add("f", OracleDbType.Int32).Value = currentUserId;
            followCmd.Parameters.Add("t", OracleDbType.Int32).Value = id;
            followCmd.ExecuteNonQuery();

            var nameCmd = new OracleCommand("SELECT USERNAME FROM USERS WHERE USER_ID = :id", conn);
            nameCmd.BindByName = true;
            nameCmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
            var senderName = nameCmd.ExecuteScalar()?.ToString() ?? $"User {currentUserId}";

            var notifCmd = new OracleCommand(@"
                INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE)
                VALUES (:u, :s, 'FOLLOW_REQUEST', :m)", conn);
            notifCmd.BindByName = true;
            notifCmd.Parameters.Add("u", OracleDbType.Int32).Value = id;
            notifCmd.Parameters.Add("s", OracleDbType.Int32).Value = currentUserId;
            notifCmd.Parameters.Add("m", OracleDbType.Varchar2).Value = $"{senderName} wants to follow you.";
            notifCmd.ExecuteNonQuery();

            return Ok(new { message = "Follow request sent" });
        }

        [HttpPost("follow-accept/{senderId:int}")]
        public IActionResult AcceptFollow(int senderId)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var updateCmd = new OracleCommand(
                "UPDATE FOLLOWS SET STATUS = 'ACCEPTED' WHERE FOLLOWER_ID = :s AND FOLLOWING_ID = :u AND STATUS = 'PENDING'", conn);
            updateCmd.BindByName = true;
            updateCmd.Parameters.Add("s", OracleDbType.Int32).Value = senderId;
            updateCmd.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;
            if (updateCmd.ExecuteNonQuery() == 0)
                return BadRequest(new { message = "No pending request found." });

            var delNotif = new OracleCommand(@"
                DELETE FROM NOTIFICATIONS
                WHERE USER_ID = :u AND SENDER_ID = :s AND TYPE = 'FOLLOW_REQUEST'", conn);
            delNotif.BindByName = true;
            delNotif.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;
            delNotif.Parameters.Add("s", OracleDbType.Int32).Value = senderId;
            delNotif.ExecuteNonQuery();

            return Ok(new { message = "Follow request accepted" });
        }

        [HttpPost("follow-decline/{senderId:int}")]
        public IActionResult DeclineFollow(int senderId)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var deleteCmd = new OracleCommand(
                "DELETE FROM FOLLOWS WHERE FOLLOWER_ID = :s AND FOLLOWING_ID = :u AND STATUS = 'PENDING'", conn);
            deleteCmd.BindByName = true;
            deleteCmd.Parameters.Add("s", OracleDbType.Int32).Value = senderId;
            deleteCmd.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;
            deleteCmd.ExecuteNonQuery();

            return Ok(new { message = "Follow request declined" });
        }

        [HttpPost("{id:int}/unfollow")]
        public IActionResult Unfollow(int id)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                "DELETE FROM FOLLOWS WHERE FOLLOWER_ID = :f AND FOLLOWING_ID = :t", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("f", OracleDbType.Int32).Value = currentUserId;
            cmd.Parameters.Add("t", OracleDbType.Int32).Value = id;
            cmd.ExecuteNonQuery();

            return Ok(new { message = "Unfollowed" });
        }

        [HttpPost("{id:int}/block")]
        public IActionResult Block(int id)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();
            if (currentUserId == id) return BadRequest(new { message = "Cannot block yourself." });

            using var conn = _db.GetConnection();
            conn.Open();

            // Remove any existing follow relationship
            var delFollow = new OracleCommand(@"
                DELETE FROM FOLLOWS
                WHERE (FOLLOWER_ID = :a AND FOLLOWING_ID = :b)
                   OR (FOLLOWER_ID = :b AND FOLLOWING_ID = :a)", conn);
            delFollow.BindByName = true;
            delFollow.Parameters.Add("a", OracleDbType.Int32).Value = currentUserId;
            delFollow.Parameters.Add("b", OracleDbType.Int32).Value = id;
            delFollow.ExecuteNonQuery();

            return Ok(new { message = "User blocked" });
        }

        [HttpPost("{id:int}/unblock")]
        public IActionResult Unblock(int id)
        {
            // Placeholder � extend with a BLOCKS table if needed
            return Ok(new { message = "User unblocked" });
        }

        // -- Followers / Following ------------------------------------------------
        [HttpGet("followers")]
        public IActionResult GetFollowers()
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT u.USER_ID, u.USERNAME, u.AVATAR_URL
                FROM FOLLOWS f
                JOIN USERS u ON f.FOLLOWER_ID = u.USER_ID
                WHERE f.FOLLOWING_ID = :u AND f.STATUS = 'ACCEPTED'", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    userId    = Convert.ToInt32(reader["USER_ID"]),
                    username  = reader["USERNAME"].ToString(),
                    avatarUrl = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString()
                });
            }
            return Ok(list);
        }

        [HttpGet("following")]
        public IActionResult GetFollowing()
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT u.USER_ID, u.USERNAME, u.AVATAR_URL,
                       (SELECT COUNT(*) FROM COMMUNITIES WHERE CREATED_BY = u.USER_ID) as COMMUNITY_COUNT
                FROM FOLLOWS f
                JOIN USERS u ON f.FOLLOWING_ID = u.USER_ID
                WHERE f.FOLLOWER_ID = :u AND f.STATUS = 'ACCEPTED'", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    userId         = Convert.ToInt32(reader["USER_ID"]),
                    username       = reader["USERNAME"].ToString(),
                    avatarUrl      = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                    communityCount = Convert.ToInt32(reader["COMMUNITY_COUNT"])
                });
            }
            return Ok(list);
        }

        // -- Notifications --------------------------------------------------------
        [HttpGet("notifications")]
        public IActionResult GetNotifications()
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT n.NOTIFICATION_ID, n.SENDER_ID, n.TYPE, n.MESSAGE,
                       n.IS_READ, n.CREATED_AT, u.USERNAME as SENDER_NAME
                FROM NOTIFICATIONS n
                JOIN USERS u ON n.SENDER_ID = u.USER_ID
                WHERE n.USER_ID = :u
                ORDER BY n.CREATED_AT DESC
                FETCH FIRST 50 ROWS ONLY", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    id         = Convert.ToInt32(reader["NOTIFICATION_ID"]),
                    senderId   = Convert.ToInt32(reader["SENDER_ID"]),
                    senderName = reader["SENDER_NAME"].ToString(),
                    type       = reader["TYPE"].ToString(),
                    message    = reader["MESSAGE"].ToString(),
                    isRead     = Convert.ToInt32(reader["IS_READ"]) == 1,
                    createdAt  = DateTime.SpecifyKind(
                        reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
                        DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpPost("notifications/read-all")]
        public IActionResult MarkNotificationsRead()
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                "UPDATE NOTIFICATIONS SET IS_READ = 1 WHERE USER_ID = :u AND IS_READ = 0", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("u", OracleDbType.Int32).Value = currentUserId;
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, message = "Notifications marked as read" });
        }

        [HttpDelete("notifications/{notificationId:int}")]
        public IActionResult DeleteNotification(int notificationId)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            // Scoped to current user � prevents deleting other users notifications
            var cmd = new OracleCommand(
                "DELETE FROM NOTIFICATIONS WHERE NOTIFICATION_ID = :id AND USER_ID = :userId", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("id", OracleDbType.Int32).Value = notificationId;
            cmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
            cmd.ExecuteNonQuery();

            return Ok(new { success = true });
        }

        // -- Change Password ------------------------------------------------------
        [HttpPost("change-password")]
        public IActionResult ChangePassword([FromBody] ChangePasswordDto dto)
        {
            int currentUserId = GetCurrentUserId();
            if (currentUserId == 0) return Unauthorized();

            if (string.IsNullOrWhiteSpace(dto.CurrentPassword) ||
                string.IsNullOrWhiteSpace(dto.NewPassword))
                return BadRequest(new { message = "Both passwords are required." });

            if (dto.NewPassword.Length < 8)
                return BadRequest(new { message = "New password must be at least 8 characters." });

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                "SELECT PASSWORD_HASH FROM USERS WHERE USER_ID = :idParam", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("idParam", OracleDbType.Int32).Value = currentUserId;
            var storedHash = cmd.ExecuteScalar()?.ToString();

            if (storedHash == null || !_passwordService.VerifyPassword(dto.CurrentPassword, storedHash))
                return BadRequest(new { message = "Current password is incorrect." });

            var newHash = _passwordService.HashPassword(dto.NewPassword);

            var updateCmd = new OracleCommand(
                "UPDATE USERS SET PASSWORD_HASH = :hashParam WHERE USER_ID = :idParam", conn);
            updateCmd.BindByName = true;
            updateCmd.Parameters.Add("hashParam", OracleDbType.Varchar2).Value = newHash;
            updateCmd.Parameters.Add("idParam", OracleDbType.Int32).Value = currentUserId;
            updateCmd.ExecuteNonQuery();

            return Ok(new { message = "Password updated successfully." });
        }

        // -- DTOs -----------------------------------------------------------------
        public class UpdateProfileDto
        {
            public string? Username { get; set; }
            public string? ContentNiche { get; set; }
        }
    }

    public class ChangePasswordDto
    {
        public string CurrentPassword { get; set; } = "";
        public string NewPassword { get; set; } = "";
    }
}
