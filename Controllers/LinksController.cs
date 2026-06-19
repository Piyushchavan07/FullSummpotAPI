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
    public class LinksController : ControllerBase
    {
        private readonly OracleDbContext _db;
        private readonly IHubContext<ChatHub> _hub;

        public LinksController(OracleDbContext db, IHubContext<ChatHub> hub)
        {
            _db  = db;
            _hub = hub;
        }

        [HttpPost]
        public IActionResult Create([FromBody] CreateLinkDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int userIdInt = Convert.ToInt32(userId);

            // --- Input validation ---
            if (string.IsNullOrWhiteSpace(dto.Title))
                return BadRequest(new { message = "Link title is required." });
            dto.Title = dto.Title.Trim();
            if (dto.Title.Length < 3)
                return BadRequest(new { message = "Link title must be at least 3 characters." });
            if (dto.Title.Length > 200)
                return BadRequest(new { message = "Link title must not exceed 200 characters." });

            if (string.IsNullOrWhiteSpace(dto.Url))
                return BadRequest(new { message = "Link URL is required." });
            dto.Url = dto.Url.Trim();
            if (!dto.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                !dto.Url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only YouTube links are allowed (youtube.com or youtu.be)." });

            using var conn = _db.GetConnection();
            conn.Open();

            try
            {
                var creatorCheck = new OracleCommand(
                    "SELECT COUNT(*) FROM COMMUNITIES WHERE COMMUNITY_ID = :communityIdParam AND CREATED_BY = :userIdParam", conn);
                creatorCheck.BindByName = true;
                creatorCheck.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = dto.CommunityId;
                creatorCheck.Parameters.Add("userIdParam", OracleDbType.Int32).Value = userIdInt;
                if (Convert.ToInt32(creatorCheck.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Only the community creator can submit links." });

                var cmd = new OracleCommand(
                    "INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CREATED_AT) VALUES (:titleParam, :urlParam, :communityParam, :userParam, SYS_EXTRACT_UTC(SYSTIMESTAMP))", conn);
                cmd.BindByName = true;
                cmd.Parameters.Add("titleParam", OracleDbType.Varchar2).Value = dto.Title;
                cmd.Parameters.Add("urlParam", OracleDbType.Varchar2).Value = dto.Url;
                cmd.Parameters.Add("communityParam", OracleDbType.Int32).Value = dto.CommunityId;
                cmd.Parameters.Add("userParam", OracleDbType.Int32).Value = userIdInt;
                cmd.ExecuteNonQuery();

                return Ok(new { message = "Link added successfully" });
            }
            catch
            {
                return StatusCode(500, new { message = "An error occurred while adding the link." });
            }
        }

        [HttpPut("{id:int}")]
        public IActionResult Update(int id, [FromBody] UpdateLinkDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int userIdInt = Convert.ToInt32(userId);

            // --- Input validation ---
            if (string.IsNullOrWhiteSpace(dto.Title))
                return BadRequest(new { message = "Link title is required." });
            dto.Title = dto.Title.Trim();
            if (dto.Title.Length < 3)
                return BadRequest(new { message = "Link title must be at least 3 characters." });
            if (dto.Title.Length > 200)
                return BadRequest(new { message = "Link title must not exceed 200 characters." });

            if (string.IsNullOrWhiteSpace(dto.Url))
                return BadRequest(new { message = "Link URL is required." });
            dto.Url = dto.Url.Trim();
            if (!dto.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                !dto.Url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only YouTube links are allowed (youtube.com or youtu.be)." });

            using var conn = _db.GetConnection();
            conn.Open();

            try
            {
                var ownerCheck = new OracleCommand(
                    "SELECT COUNT(*) FROM LINKS WHERE LINK_ID = :linkId AND USER_ID = :userId", conn);
                ownerCheck.BindByName = true;
                ownerCheck.Parameters.Add("linkId", OracleDbType.Int32).Value = id;
                ownerCheck.Parameters.Add("userId", OracleDbType.Int32).Value = userIdInt;
                if (Convert.ToInt32(ownerCheck.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "You can only edit your own links." });

                var cmd = new OracleCommand(
                    "UPDATE LINKS SET TITLE = :title, URL = :url WHERE LINK_ID = :linkId", conn);
                cmd.BindByName = true;
                cmd.Parameters.Add("title", OracleDbType.Varchar2).Value = dto.Title;
                cmd.Parameters.Add("url", OracleDbType.Varchar2).Value = dto.Url;
                cmd.Parameters.Add("linkId", OracleDbType.Int32).Value = id;
                cmd.ExecuteNonQuery();

                return Ok(new { message = "Link updated successfully" });
            }
            catch
            {
                return StatusCode(500, new { message = "An error occurred while updating the link." });
            }
        }

        [HttpDelete("{id:int}")]
        public IActionResult Delete(int id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int userIdInt = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            try
            {
                var ownerCheck = new OracleCommand(@"
                    SELECT COUNT(*) FROM LINKS l
                    JOIN COMMUNITIES c ON c.COMMUNITY_ID = l.COMMUNITY_ID
                    WHERE l.LINK_ID = :linkId
                    AND (l.USER_ID = :userId OR c.CREATED_BY = :userId)", conn);
                ownerCheck.BindByName = true;
                ownerCheck.Parameters.Add("linkId", OracleDbType.Int32).Value = id;
                ownerCheck.Parameters.Add("userId", OracleDbType.Int32).Value = userIdInt;
                if (Convert.ToInt32(ownerCheck.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "You don't have permission to delete this link." });

                var cmd = new OracleCommand("DELETE FROM LINKS WHERE LINK_ID = :linkId", conn);
                cmd.BindByName = true;
                cmd.Parameters.Add("linkId", OracleDbType.Int32).Value = id;
                int rows = cmd.ExecuteNonQuery();

                if (rows == 0) return NotFound(new { message = "Link not found." });
                return Ok(new { message = "Link deleted successfully" });
            }
            catch
            {
                return StatusCode(500, new { message = "An error occurred while deleting the link." });
            }
        }

        [HttpGet("community/{communityId:int}")]
        public IActionResult GetByCommunity(int communityId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int currentUserId = string.IsNullOrEmpty(userId) ? 0 : Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT l.LINK_ID, l.TITLE, l.URL, l.CLICKS, l.CREATED_AT,
                       u.USER_ID, u.USERNAME, u.AVATAR_URL as CREATOR_AVATAR,
                       CASE WHEN lc.CLICKER_USER_ID IS NOT NULL THEN 1 ELSE 0 END as IS_CLICKED_BY_ME
                FROM LINKS l
                JOIN USERS u ON u.USER_ID = l.USER_ID
                LEFT JOIN LINK_CLICKS lc ON lc.LINK_ID = l.LINK_ID AND lc.CLICKER_USER_ID = :currentUserId
                WHERE l.COMMUNITY_ID = :communityIdParam
                ORDER BY l.CREATED_AT DESC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("currentUserId", OracleDbType.Int32).Value = currentUserId;
            cmd.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = communityId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    linkId = Convert.ToInt32(reader["LINK_ID"]),
                    title = reader["TITLE"]?.ToString(),
                    url = reader["URL"]?.ToString(),
                    clicks = Convert.ToInt32(reader["CLICKS"]),
                    userId = Convert.ToInt32(reader["USER_ID"]),
                    username = reader["USERNAME"]?.ToString(),
                    creatorAvatar = reader["CREATOR_AVATAR"] == DBNull.Value ? null : reader["CREATOR_AVATAR"].ToString(),
                    createdAt = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CREATED_AT")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    isClickedByMe = Convert.ToInt32(reader["IS_CLICKED_BY_ME"]) == 1
                });
            }
            return Ok(list);
        }

        [HttpGet("community/{communityId:int}/count")]
        public IActionResult GetLinkCount(int communityId)
        {
            using var conn = _db.GetConnection();
            conn.Open();
            var cmd = new OracleCommand("SELECT COUNT(*) FROM LINKS WHERE COMMUNITY_ID = :communityId", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("communityId", OracleDbType.Int32).Value = communityId;
            return Ok(new { count = Convert.ToInt32(cmd.ExecuteScalar()) });
        }

        [HttpPost("{linkId:int}/click")]
        public IActionResult RegisterClick(int linkId, [FromQuery] string? referrer = null)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int currentUserId = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            // Check if this user has clicked before (used for first-click-only side effects)
            var checkCmd = new OracleCommand(
                "SELECT COUNT(*) FROM LINK_CLICKS WHERE LINK_ID = :linkId AND CLICKER_USER_ID = :userId", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            checkCmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
            bool alreadyClicked = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            // MERGE: insert on first click, increment CLICK_COUNT on subsequent clicks.
            // This respects the UNIQUE(LINK_ID, CLICKER_USER_ID) constraint while still
            // accumulating a per-supporter click count for the tier/supporters panel.
            var mergeCmd = new OracleCommand(@"
                MERGE INTO LINK_CLICKS tgt
                USING (SELECT :linkId AS LINK_ID, :userId AS CLICKER_USER_ID FROM DUAL) src
                ON (tgt.LINK_ID = src.LINK_ID AND tgt.CLICKER_USER_ID = src.CLICKER_USER_ID)
                WHEN MATCHED THEN
                    UPDATE SET tgt.CLICK_COUNT = NVL(tgt.CLICK_COUNT, 1) + 1,
                               tgt.CLICKED_AT  = SYS_EXTRACT_UTC(SYSTIMESTAMP),
                               tgt.REFERRER_PAGE = NVL(:referrer, tgt.REFERRER_PAGE)
                WHEN NOT MATCHED THEN
                    INSERT (LINK_ID, CLICKER_USER_ID, REFERRER_PAGE, CLICK_COUNT)
                    VALUES (:linkId2, :userId2, :referrer2, 1)", conn);
            mergeCmd.BindByName = true;
            mergeCmd.Parameters.Add("linkId",    OracleDbType.Int32).Value    = linkId;
            mergeCmd.Parameters.Add("userId",    OracleDbType.Int32).Value    = currentUserId;
            mergeCmd.Parameters.Add("referrer",  OracleDbType.Varchar2).Value = string.IsNullOrEmpty(referrer) ? (object)DBNull.Value : referrer;
            mergeCmd.Parameters.Add("linkId2",   OracleDbType.Int32).Value    = linkId;
            mergeCmd.Parameters.Add("userId2",   OracleDbType.Int32).Value    = currentUserId;
            mergeCmd.Parameters.Add("referrer2", OracleDbType.Varchar2).Value = string.IsNullOrEmpty(referrer) ? (object)DBNull.Value : referrer;
            mergeCmd.ExecuteNonQuery();

            // Only increment LINKS.CLICKS and send the creator notification on the first ever click
            if (!alreadyClicked)
            {
                var updateCmd = new OracleCommand("UPDATE LINKS SET CLICKS = CLICKS + 1 WHERE LINK_ID = :linkId", conn);
                updateCmd.BindByName = true;
                updateCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
                updateCmd.ExecuteNonQuery();

                var isCreatorCmd = new OracleCommand(@"
                    SELECT COUNT(*) FROM (
                        SELECT 1 FROM COMMUNITIES WHERE CREATED_BY = :clickerId
                        UNION ALL
                        SELECT 1 FROM LINKS WHERE USER_ID = :clickerId AND ROWNUM = 1
                    )", conn);
                isCreatorCmd.BindByName = true;
                isCreatorCmd.Parameters.Add("clickerId", OracleDbType.Int32).Value = currentUserId;
                bool isCreator = Convert.ToInt32(isCreatorCmd.ExecuteScalar()) > 0;

                if (isCreator)
                {
                    var ownerCmd = new OracleCommand(
                        "SELECT USER_ID FROM LINKS WHERE LINK_ID = :linkId", conn);
                    ownerCmd.BindByName = true;
                    ownerCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
                    var ownerIdObj = ownerCmd.ExecuteScalar();

                    if (ownerIdObj != null && ownerIdObj != DBNull.Value)
                    {
                        int ownerId = Convert.ToInt32(ownerIdObj);
                        if (ownerId != currentUserId)
                        {
                            var nameCmd = new OracleCommand(
                                "SELECT USERNAME FROM USERS WHERE USER_ID = :id", conn);
                            nameCmd.BindByName = true;
                            nameCmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
                            var clickerName = nameCmd.ExecuteScalar()?.ToString() ?? "A creator";

                            var titleCmd = new OracleCommand(
                                "SELECT TITLE FROM LINKS WHERE LINK_ID = :linkId2", conn);
                            titleCmd.BindByName = true;
                            titleCmd.Parameters.Add("linkId2", OracleDbType.Int32).Value = linkId;
                            var linkTitle = titleCmd.ExecuteScalar()?.ToString() ?? "your link";

                            const int pointsAwarded = 1;
                            var notifMsg = $"@{clickerName} (creator) clicked your link \"{linkTitle}\"! +{pointsAwarded} point{(pointsAwarded == 1 ? "" : "s")} 🌟";

                            var notifCmd = new OracleCommand(@"
                                INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE)
                                VALUES (:ownerId, :senderId, 'CREATOR_CLICKED_YOUR_LINK', :msg)", conn);
                            notifCmd.BindByName = true;
                            notifCmd.Parameters.Add("ownerId",  OracleDbType.Int32).Value    = ownerId;
                            notifCmd.Parameters.Add("senderId", OracleDbType.Int32).Value    = currentUserId;
                            notifCmd.Parameters.Add("msg",      OracleDbType.Varchar2).Value = notifMsg;
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
            int currentUserId = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            var linkCheck = new OracleCommand("SELECT USER_ID FROM LINKS WHERE LINK_ID = :linkId", conn);
            linkCheck.BindByName = true;
            linkCheck.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            var ownerIdObj = linkCheck.ExecuteScalar();
            if (ownerIdObj == null || ownerIdObj == DBNull.Value)
                return NotFound(new { message = "Link not found." });

            int ownerId = Convert.ToInt32(ownerIdObj);

            var statsCmd = new OracleCommand(@"
                SELECT COUNT(*) as TOTAL_CLICKS,
                       COUNT(DISTINCT lc.CLICKER_USER_ID) as UNIQUE_USERS,
                       SUM(CASE WHEN (SELECT COUNT(*) FROM COMMUNITIES WHERE CREATED_BY = lc.CLICKER_USER_ID) > 0
                                OR (SELECT COUNT(*) FROM LINKS WHERE USER_ID = lc.CLICKER_USER_ID AND ROWNUM = 1) > 0
                                THEN 1 ELSE 0 END) as CREATOR_CLICKS
                FROM LINK_CLICKS lc WHERE lc.LINK_ID = :linkId", conn);
            statsCmd.BindByName = true;
            statsCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;

            int totalClicks = 0, uniqueUsers = 0, creatorClicks = 0;
            using (var statsReader = statsCmd.ExecuteReader())
            {
                if (statsReader.Read())
                {
                    totalClicks = Convert.ToInt32(statsReader["TOTAL_CLICKS"]);
                    uniqueUsers = Convert.ToInt32(statsReader["UNIQUE_USERS"]);
                    creatorClicks = statsReader["CREATOR_CLICKS"] == DBNull.Value ? 0 : Convert.ToInt32(statsReader["CREATOR_CLICKS"]);
                }
            }

            var supporters = new List<object>();
            if (ownerId == currentUserId)
            {
                var clickersCmd = new OracleCommand(@"
                    SELECT u.USER_ID, u.USERNAME, u.AVATAR_URL,
                           lc.CLICKED_AT,
                           lc.REFERRER_PAGE,
                           NVL(lc.CLICK_COUNT, 1) as CLICK_COUNT,
                           CASE WHEN (SELECT COUNT(*) FROM COMMUNITIES WHERE CREATED_BY = u.USER_ID) > 0
                                OR (SELECT COUNT(*) FROM LINKS WHERE USER_ID = u.USER_ID AND ROWNUM = 1) > 0
                                THEN 1 ELSE 0 END as IS_CREATOR,
                           CASE WHEN u.USER_ID = :ownerId THEN 'SELF'
                                ELSE NVL((SELECT STATUS FROM FOLLOWS WHERE FOLLOWER_ID = :ownerId AND FOLLOWING_ID = u.USER_ID), 'NONE')
                           END as FOLLOW_STATUS,
                           CASE WHEN (
                               SELECT COUNT(*) FROM LINK_CLICKS lc2
                               JOIN LINKS l2 ON lc2.LINK_ID = l2.LINK_ID
                               WHERE lc2.CLICKER_USER_ID = :ownerId
                               AND l2.USER_ID = u.USER_ID
                           ) > 0 THEN 1 ELSE 0 END as IS_MUTUAL
                    FROM LINK_CLICKS lc
                    JOIN USERS u ON u.USER_ID = lc.CLICKER_USER_ID
                    WHERE lc.LINK_ID = :linkId
                    ORDER BY IS_CREATOR DESC, NVL(lc.CLICK_COUNT, 1) DESC, lc.CLICKED_AT DESC", conn);
                clickersCmd.BindByName = true;
                clickersCmd.Parameters.Add("ownerId", OracleDbType.Int32).Value = ownerId;
                clickersCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;

                using var reader = clickersCmd.ExecuteReader();
                while (reader.Read())
                {
                    supporters.Add(new
                    {
                        userId       = Convert.ToInt32(reader["USER_ID"]),
                        username     = reader["USERNAME"].ToString(),
                        avatarUrl    = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                        clickedAt    = reader.GetDateTime(reader.GetOrdinal("CLICKED_AT")).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        referrerPage = reader["REFERRER_PAGE"] == DBNull.Value ? null : reader["REFERRER_PAGE"].ToString(),
                        clickCount   = Convert.ToInt32(reader["CLICK_COUNT"]),
                        isCreator    = Convert.ToInt32(reader["IS_CREATOR"]) == 1,
                        isMutual     = Convert.ToInt32(reader["IS_MUTUAL"]) == 1,
                        followStatus = reader["FOLLOW_STATUS"].ToString()
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
            int currentUserId = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            var ownerCheck = new OracleCommand(
                "SELECT COUNT(*) FROM LINKS WHERE LINK_ID = :linkId AND USER_ID = :userId", conn);
            ownerCheck.BindByName = true;
            ownerCheck.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            ownerCheck.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
            if (Convert.ToInt32(ownerCheck.ExecuteScalar()) == 0)
                return StatusCode(403, new { message = "Only the link owner can send shout outs." });

            var nameCmd = new OracleCommand("SELECT USERNAME FROM USERS WHERE USER_ID = :id", conn);
            nameCmd.BindByName = true;
            nameCmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
            var senderName = nameCmd.ExecuteScalar()?.ToString() ?? "A creator";

            var notifCmd = new OracleCommand(@"
                INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE)
                VALUES (:targetId, :senderId, 'SHOUT_OUT', :msg)", conn);
            notifCmd.BindByName = true;
            notifCmd.Parameters.Add("targetId", OracleDbType.Int32).Value = targetUserId;
            notifCmd.Parameters.Add("senderId", OracleDbType.Int32).Value = currentUserId;
            notifCmd.Parameters.Add("msg", OracleDbType.Varchar2).Value =
                $"@{senderName} gave you a shout out for supporting their link!";
            notifCmd.ExecuteNonQuery();

            return Ok(new { message = "Shout out sent!" });
        }

        [HttpPost("{linkId:int}/like")]
        public IActionResult ToggleLike(int linkId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int currentUserId = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            var checkCmd = new OracleCommand(
                "SELECT COUNT(*) FROM LINK_LIKES WHERE LINK_ID = :linkId AND USER_ID = :userId", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            checkCmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
            bool alreadyLiked = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            if (alreadyLiked)
            {
                var deleteCmd = new OracleCommand(
                    "DELETE FROM LINK_LIKES WHERE LINK_ID = :linkId AND USER_ID = :userId", conn);
                deleteCmd.BindByName = true;
                deleteCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
                deleteCmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
                deleteCmd.ExecuteNonQuery();
            }
            else
            {
                var insertCmd = new OracleCommand(
                    "INSERT INTO LINK_LIKES (LINK_ID, USER_ID) VALUES (:linkId, :userId)", conn);
                insertCmd.BindByName = true;
                insertCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
                insertCmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
                insertCmd.ExecuteNonQuery();
            }

            var countCmd = new OracleCommand("SELECT COUNT(*) FROM LINK_LIKES WHERE LINK_ID = :linkId", conn);
            countCmd.BindByName = true;
            countCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            int likeCount = Convert.ToInt32(countCmd.ExecuteScalar());

            return Ok(new { liked = !alreadyLiked, likeCount });
        }

        [HttpGet("{linkId:int}/likes")]
        public IActionResult GetLikes(int linkId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int currentUserId = string.IsNullOrEmpty(userId) ? 0 : Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            var countCmd = new OracleCommand("SELECT COUNT(*) FROM LINK_LIKES WHERE LINK_ID = :linkId", conn);
            countCmd.BindByName = true;
            countCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            int likeCount = Convert.ToInt32(countCmd.ExecuteScalar());

            var checkCmd = new OracleCommand(
                "SELECT COUNT(*) FROM LINK_LIKES WHERE LINK_ID = :linkId AND USER_ID = :userId", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            checkCmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
            bool isLikedByMe = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            return Ok(new { likeCount, isLikedByMe });
        }

        [HttpGet("{linkId:int}/comments")]
        public IActionResult GetComments(int linkId)
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT lc.COMMENT_ID, lc.USER_ID, lc.CONTENT, lc.CREATED_AT, u.USERNAME
                FROM LINK_COMMENTS lc
                JOIN USERS u ON u.USER_ID = lc.USER_ID
                WHERE lc.LINK_ID = :linkId
                ORDER BY lc.CREATED_AT ASC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    commentId = Convert.ToInt32(reader["COMMENT_ID"]),
                    userId = Convert.ToInt32(reader["USER_ID"]),
                    username = reader["USERNAME"].ToString(),
                    content = reader["CONTENT"].ToString(),
                    createdAt = reader.GetDateTime(reader.GetOrdinal("CREATED_AT")).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpPost("{linkId:int}/comments")]
        public IActionResult AddComment(int linkId, [FromBody] AddCommentDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int currentUserId = Convert.ToInt32(userId);

            if (string.IsNullOrWhiteSpace(dto.Content))
                return BadRequest(new { message = "Comment cannot be empty." });
            dto.Content = dto.Content.Trim();
            if (dto.Content.Length > 500)
                return BadRequest(new { message = "Comment must not exceed 500 characters." });

            using var conn = _db.GetConnection();
            conn.Open();

            try
            {
                var cmd = new OracleCommand(
                    "INSERT INTO LINK_COMMENTS (LINK_ID, USER_ID, CONTENT) VALUES (:linkId, :userId, :content)", conn);
                cmd.BindByName = true;
                cmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
                cmd.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
                cmd.Parameters.Add("content", OracleDbType.Varchar2).Value = dto.Content;
                cmd.ExecuteNonQuery();

                return Ok(new { message = "Comment added" });
            }
            catch
            {
                return StatusCode(500, new { message = "An error occurred while adding the comment." });
            }
        }

        [HttpDelete("{linkId:int}/comments/{commentId:int}")]
        public IActionResult DeleteComment(int linkId, int commentId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            int currentUserId = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            var ownerCheck = new OracleCommand(@"
                SELECT COUNT(*) FROM LINK_COMMENTS lc
                JOIN LINKS l ON l.LINK_ID = lc.LINK_ID
                JOIN COMMUNITIES c ON c.COMMUNITY_ID = l.COMMUNITY_ID
                WHERE lc.COMMENT_ID = :commentId
                AND (lc.USER_ID = :userId OR c.CREATED_BY = :userId)", conn);
            ownerCheck.BindByName = true;
            ownerCheck.Parameters.Add("commentId", OracleDbType.Int32).Value = commentId;
            ownerCheck.Parameters.Add("userId", OracleDbType.Int32).Value = currentUserId;
            if (Convert.ToInt32(ownerCheck.ExecuteScalar()) == 0)
                return StatusCode(403, new { message = "Not allowed." });

            var cmd = new OracleCommand("DELETE FROM LINK_COMMENTS WHERE COMMENT_ID = :commentId", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("commentId", OracleDbType.Int32).Value = commentId;
            cmd.ExecuteNonQuery();

            return Ok(new { message = "Comment deleted" });
        }

        // ==================== MY LINKS — includes community name ====================
        [HttpGet("my")]
        public IActionResult MyLinks()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT l.LINK_ID, l.TITLE, l.URL, l.CLICKS, l.CREATED_AT,
                       l.COMMUNITY_ID, c.NAME as COMMUNITY_NAME
                FROM LINKS l
                LEFT JOIN COMMUNITIES c ON c.COMMUNITY_ID = l.COMMUNITY_ID
                WHERE l.USER_ID = :u
                ORDER BY l.CREATED_AT DESC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("u", OracleDbType.Int32).Value = Convert.ToInt32(userId);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    linkId = Convert.ToInt32(reader["LINK_ID"]),
                    title = reader["TITLE"]?.ToString(),
                    url = reader["URL"]?.ToString(),
                    clicks = Convert.ToInt32(reader["CLICKS"]),
                    communityId = reader["COMMUNITY_ID"] == DBNull.Value ? 0 : Convert.ToInt32(reader["COMMUNITY_ID"]),
                    communityName = reader["COMMUNITY_NAME"] == DBNull.Value ? null : reader["COMMUNITY_NAME"].ToString(),
                    createdAt = DateTime.SpecifyKind(
                        reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
                        DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpGet("user/{userId:int}")]
        public IActionResult GetLinksByUser(int userId)
        {
            var currentUserStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(currentUserStr)) return Unauthorized();
            int currentUserId = Convert.ToInt32(currentUserStr);

            using var conn = _db.GetConnection();
            conn.Open();

            // Join LINK_CLICKS to know which links the current user has already supported,
            // so the picker modal can show "Already supported ✓" per individual link.
            var cmd = new OracleCommand(@"
                SELECT l.LINK_ID, l.TITLE, l.URL, l.CLICKS, l.CREATED_AT,
                       CASE WHEN lc.CLICKER_USER_ID IS NOT NULL THEN 1 ELSE 0 END AS IS_CLICKED_BY_ME
                FROM LINKS l
                LEFT JOIN LINK_CLICKS lc
                       ON lc.LINK_ID = l.LINK_ID AND lc.CLICKER_USER_ID = :currentUserId
                WHERE l.USER_ID = :userId
                ORDER BY l.CREATED_AT DESC", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("currentUserId", OracleDbType.Int32).Value = currentUserId;
            cmd.Parameters.Add("userId",        OracleDbType.Int32).Value = userId;

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    linkId         = Convert.ToInt32(reader["LINK_ID"]),
                    title          = reader["TITLE"].ToString(),
                    url            = reader["URL"].ToString(),
                    clicks         = Convert.ToInt32(reader["CLICKS"]),
                    createdAt      = DateTime.SpecifyKind(
                        reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
                        DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    isClickedByMe  = Convert.ToInt32(reader["IS_CLICKED_BY_ME"]) == 1
                });
            }
            return Ok(list); // empty array [] if user has no links — never 404
        }

        [HttpGet("latest/{userId:int}")]
        public IActionResult GetLatestLink(int userId)
        {
            var currentUser = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT l.LINK_ID, l.TITLE, l.URL, l.USER_ID, u.USERNAME
                FROM LINKS l
                JOIN USERS u ON u.USER_ID = l.USER_ID
                WHERE l.USER_ID = :userId
                ORDER BY l.CREATED_AT DESC
                FETCH FIRST 1 ROWS ONLY", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("userId", OracleDbType.Int32).Value = userId;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return NotFound(new { message = "This user has no links yet." });

            return Ok(new
            {
                linkId   = Convert.ToInt32(reader["LINK_ID"]),
                title    = reader["TITLE"].ToString(),
                url      = reader["URL"].ToString(),
                userId   = Convert.ToInt32(reader["USER_ID"]),
                username = reader["USERNAME"].ToString()
            });
        }

        [HttpGet("weekly-supporters")]
        public IActionResult GetWeeklySupporters()
        {
            var currentUser = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT RANK, USER_ID, USERNAME, AVATAR_URL, LINKS_SUPPORTED, TOTAL_CLICKS
                FROM (
                    SELECT
                        ROW_NUMBER() OVER (ORDER BY COUNT(DISTINCT lc.LINK_ID) DESC, COUNT(lc.CLICK_ID) DESC) AS RANK,
                        u.USER_ID,
                        u.USERNAME,
                        u.AVATAR_URL,
                        COUNT(DISTINCT lc.LINK_ID) AS LINKS_SUPPORTED,
                        COUNT(lc.CLICK_ID)         AS TOTAL_CLICKS
                    FROM LINK_CLICKS lc
                    JOIN USERS u ON lc.CLICKER_USER_ID = u.USER_ID
                    WHERE lc.CLICKED_AT >= TRUNC(SYSDATE + (5.5/24), 'IW') - (5.5/24)
                    GROUP BY u.USER_ID, u.USERNAME, u.AVATAR_URL
                )
                WHERE RANK <= 50
                ORDER BY RANK", conn);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    rank           = Convert.ToInt32(reader["RANK"]),
                    userId         = Convert.ToInt32(reader["USER_ID"]),
                    username       = reader["USERNAME"].ToString(),
                    avatarUrl      = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                    linksSupported = Convert.ToInt32(reader["LINKS_SUPPORTED"]),
                    totalClicks    = Convert.ToInt32(reader["TOTAL_CLICKS"])
                });
            }
            return Ok(list);
        }

        [HttpPost("{linkId:int}/support-back-notify/{creatorUserId:int}")]
        public async Task<IActionResult> NotifySupportBack(int linkId, int creatorUserId)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int currentUserId = Convert.ToInt32(userIdStr);

            if (currentUserId == creatorUserId)
                return Ok(new { message = "Cannot notify yourself" });

            using var conn = _db.GetConnection();
            conn.Open();

            // Get supporter username
            var nameCmd = new OracleCommand("SELECT USERNAME FROM USERS WHERE USER_ID = :id", conn);
            nameCmd.BindByName = true;
            nameCmd.Parameters.Add("id", OracleDbType.Int32).Value = currentUserId;
            var supporterName = nameCmd.ExecuteScalar()?.ToString();
            if (supporterName == null) return NotFound(new { message = "Supporter not found." });

            // Get link title
            var titleCmd = new OracleCommand("SELECT TITLE FROM LINKS WHERE LINK_ID = :linkId", conn);
            titleCmd.BindByName = true;
            titleCmd.Parameters.Add("linkId", OracleDbType.Int32).Value = linkId;
            var linkTitle = titleCmd.ExecuteScalar()?.ToString();
            if (linkTitle == null) return NotFound(new { message = "Link not found." });

            // Insert notification
            var message = $"@{supporterName} supported your link \"{linkTitle}\" back! 🔁";
            var notifCmd = new OracleCommand(@"
                INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE)
                VALUES (:userId, :senderId, 'SUPPORT_BACK', :msg)", conn);
            notifCmd.BindByName = true;
            notifCmd.Parameters.Add("userId",   OracleDbType.Int32).Value    = creatorUserId;
            notifCmd.Parameters.Add("senderId", OracleDbType.Int32).Value    = currentUserId;
            notifCmd.Parameters.Add("msg",      OracleDbType.Varchar2).Value = message;
            notifCmd.ExecuteNonQuery();

            // Push real-time notification via SignalR (same group pattern as ChatHub)
            await _hub.Clients.Group($"user_{creatorUserId}")
                .SendAsync("ReceiveNotification", new
                {
                    type      = "SUPPORT_BACK",
                    message   = message,
                    senderId  = currentUserId,
                    username  = supporterName
                });

            return Ok(new { message = "Notification sent" });
        }
    }

    public class AddCommentDto
    {
        public string Content { get; set; } = "";
    }
}
