using FullSummpotAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly OracleDbContext _db;

        public AdminController(OracleDbContext db)
        {
            _db = db;
        }

        private bool IsAdmin() =>
            User.FindFirst(ClaimTypes.Role)?.Value == "ADMIN";

        // ── GET /api/Admin/stats ──────────────────────────────────────────────
        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT
                    (SELECT COUNT(*) FROM USERS)       AS TOTAL_USERS,
                    (SELECT COUNT(*) FROM COMMUNITIES) AS TOTAL_COMMUNITIES,
                    (SELECT COUNT(*) FROM LINKS)       AS TOTAL_LINKS,
                    (SELECT NVL(SUM(CLICKS), 0) FROM LINKS) AS TOTAL_CLICKS
                FROM DUAL", conn);

            using var reader = cmd.ExecuteReader();
            reader.Read();

            return Ok(new
            {
                totalUsers        = Convert.ToInt32(reader["TOTAL_USERS"]),
                totalCommunities  = Convert.ToInt32(reader["TOTAL_COMMUNITIES"]),
                totalLinks        = Convert.ToInt32(reader["TOTAL_LINKS"]),
                totalClicks       = Convert.ToInt32(reader["TOTAL_CLICKS"])
            });
        }

        // ── GET /api/Admin/users ──────────────────────────────────────────────
        [HttpGet("users")]
        public IActionResult GetUsers()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT
                    u.USER_ID,
                    u.USERNAME,
                    u.EMAIL,
                    u.ROLE,
                    u.AVAILABLE_POINTS,
                    u.CREATED_AT,
                    (SELECT COUNT(*) FROM COMMUNITIES  WHERE CREATED_BY = u.USER_ID) AS COMMUNITIES_CREATED,
                    (SELECT COUNT(*) FROM LINKS        WHERE USER_ID    = u.USER_ID) AS LINKS_SUBMITTED,
                    (SELECT NVL(SUM(l2.CLICKS), 0) FROM LINKS l2 WHERE l2.USER_ID = u.USER_ID) AS TOTAL_CLICKS
                FROM USERS u
                ORDER BY u.CREATED_AT DESC", conn);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    userId             = Convert.ToInt32(reader["USER_ID"]),
                    username           = reader["USERNAME"]?.ToString(),
                    email              = reader["EMAIL"]?.ToString(),
                    role               = reader["ROLE"]?.ToString() ?? "USER",
                    availablePoints    = Convert.ToInt32(reader["AVAILABLE_POINTS"]),
                    communitiesCreated = Convert.ToInt32(reader["COMMUNITIES_CREATED"]),
                    linksSubmitted     = Convert.ToInt32(reader["LINKS_SUBMITTED"]),
                    totalClicks        = Convert.ToInt32(reader["TOTAL_CLICKS"]),
                    createdAt          = reader["CREATED_AT"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(
                            reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
                            DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        // ── POST /api/Admin/users/{id}/make-admin ─────────────────────────────
        [HttpPost("users/{id:int}/make-admin")]
        public IActionResult MakeAdmin(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                "UPDATE USERS SET ROLE = 'ADMIN' WHERE USER_ID = :userIdParam", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("userIdParam", OracleDbType.Int32).Value = id;
            int rows = cmd.ExecuteNonQuery();

            if (rows == 0) return NotFound(new { message = "User not found." });
            return Ok(new { message = "User promoted to admin" });
        }

        // ── DELETE /api/Admin/users/{id} ──────────────────────────────────────
        [HttpDelete("users/{id:int}")]
        public IActionResult DeleteUser(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            // Prevent self-deletion
            var selfId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (selfId != null && Convert.ToInt32(selfId) == id)
                return BadRequest(new { message = "You cannot delete your own account." });

            using var conn = _db.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                void Exec(string sql, int paramValue)
                {
                    var c = new OracleCommand(sql, conn);
                    c.Transaction = tx;
                    c.BindByName = true;
                    c.Parameters.Add("idParam", OracleDbType.Int32).Value = paramValue;
                    c.ExecuteNonQuery();
                }

                // Delete in dependency order
                Exec("DELETE FROM LINK_COMMENTS          WHERE USER_ID    = :idParam", id);
                Exec("DELETE FROM LINK_LIKES             WHERE USER_ID    = :idParam", id);
                Exec("DELETE FROM LINK_CLICKS            WHERE CLICKER_USER_ID = :idParam", id);
                Exec("DELETE FROM NOTIFICATIONS          WHERE USER_ID    = :idParam OR SENDER_ID = :idParam", id);
                Exec("DELETE FROM MESSAGE_REQUESTS       WHERE SENDER_ID  = :idParam OR RECIPIENT_ID = :idParam", id);

                // Remove user from conversations, then clean up empty conversations
                Exec("DELETE FROM CONVERSATION_PARTICIPANTS WHERE USER_ID = :idParam", id);

                // Delete links owned by user (and their dependent rows)
                var linkCmd = new OracleCommand(
                    "SELECT LINK_ID FROM LINKS WHERE USER_ID = :idParam", conn);
                linkCmd.Transaction = tx;
                linkCmd.BindByName = true;
                linkCmd.Parameters.Add("idParam", OracleDbType.Int32).Value = id;
                var linkIds = new List<int>();
                using (var lr = linkCmd.ExecuteReader())
                    while (lr.Read()) linkIds.Add(Convert.ToInt32(lr["LINK_ID"]));

                foreach (var lid in linkIds)
                {
                    void ExecLink(string sql)
                    {
                        var c = new OracleCommand(sql, conn);
                        c.Transaction = tx;
                        c.BindByName = true;
                        c.Parameters.Add("linkIdParam", OracleDbType.Int32).Value = lid;
                        c.ExecuteNonQuery();
                    }
                    ExecLink("DELETE FROM LINK_COMMENTS WHERE LINK_ID = :linkIdParam");
                    ExecLink("DELETE FROM LINK_LIKES    WHERE LINK_ID = :linkIdParam");
                    ExecLink("DELETE FROM LINK_CLICKS   WHERE LINK_ID = :linkIdParam");
                    ExecLink("DELETE FROM LINKS         WHERE LINK_ID = :linkIdParam");
                }

                // Delete communities created by user
                var commCmd = new OracleCommand(
                    "SELECT COMMUNITY_ID FROM COMMUNITIES WHERE CREATED_BY = :idParam", conn);
                commCmd.Transaction = tx;
                commCmd.BindByName = true;
                commCmd.Parameters.Add("idParam", OracleDbType.Int32).Value = id;
                var commIds = new List<int>();
                using (var cr = commCmd.ExecuteReader())
                    while (cr.Read()) commIds.Add(Convert.ToInt32(cr["COMMUNITY_ID"]));

                foreach (var cid in commIds)
                {
                    void ExecComm(string sql)
                    {
                        var c = new OracleCommand(sql, conn);
                        c.Transaction = tx;
                        c.BindByName = true;
                        c.Parameters.Add("commIdParam", OracleDbType.Int32).Value = cid;
                        c.ExecuteNonQuery();
                    }
                    // Delete all links in this community first
                    var clCmd = new OracleCommand(
                        "SELECT LINK_ID FROM LINKS WHERE COMMUNITY_ID = :commIdParam", conn);
                    clCmd.Transaction = tx;
                    clCmd.BindByName = true;
                    clCmd.Parameters.Add("commIdParam", OracleDbType.Int32).Value = cid;
                    var clIds = new List<int>();
                    using (var clr = clCmd.ExecuteReader())
                        while (clr.Read()) clIds.Add(Convert.ToInt32(clr["LINK_ID"]));

                    foreach (var lid in clIds)
                    {
                        void ExecCL(string sql)
                        {
                            var c = new OracleCommand(sql, conn);
                            c.Transaction = tx;
                            c.BindByName = true;
                            c.Parameters.Add("linkIdParam", OracleDbType.Int32).Value = lid;
                            c.ExecuteNonQuery();
                        }
                        ExecCL("DELETE FROM LINK_COMMENTS WHERE LINK_ID = :linkIdParam");
                        ExecCL("DELETE FROM LINK_LIKES    WHERE LINK_ID = :linkIdParam");
                        ExecCL("DELETE FROM LINK_CLICKS   WHERE LINK_ID = :linkIdParam");
                        ExecCL("DELETE FROM LINKS         WHERE LINK_ID = :linkIdParam");
                    }

                    ExecComm("DELETE FROM COMMUNITY_MEMBERS WHERE COMMUNITY_ID = :commIdParam");
                    ExecComm("DELETE FROM COMMUNITIES       WHERE COMMUNITY_ID = :commIdParam");
                }

                Exec("DELETE FROM COMMUNITY_MEMBERS WHERE USER_ID   = :idParam", id);
                Exec("DELETE FROM FOLLOWS           WHERE FOLLOWER_ID = :idParam OR FOLLOWING_ID = :idParam", id);
                Exec("DELETE FROM USERS             WHERE USER_ID    = :idParam", id);

                tx.Commit();
                return Ok(new { message = "User deleted" });
            }
            catch
            {
                tx.Rollback();
                return StatusCode(500, new { message = "Failed to delete user." });
            }
        }

        // ── GET /api/Admin/communities ────────────────────────────────────────
        [HttpGet("communities")]
        public IActionResult GetCommunities()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT
                    c.COMMUNITY_ID,
                    c.NAME,
                    c.NICHE,
                    c.CREATED_AT,
                    u.USERNAME AS CREATOR_NAME,
                    (SELECT COUNT(*) FROM COMMUNITY_MEMBERS cm WHERE cm.COMMUNITY_ID = c.COMMUNITY_ID) AS MEMBER_COUNT,
                    (SELECT COUNT(*) FROM LINKS             l  WHERE l.COMMUNITY_ID  = c.COMMUNITY_ID) AS LINK_COUNT
                FROM COMMUNITIES c
                JOIN USERS u ON u.USER_ID = c.CREATED_BY
                ORDER BY c.CREATED_AT DESC", conn);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    communityId  = Convert.ToInt32(reader["COMMUNITY_ID"]),
                    name         = reader["NAME"]?.ToString(),
                    niche        = reader["NICHE"]?.ToString(),
                    creatorName  = reader["CREATOR_NAME"]?.ToString(),
                    memberCount  = Convert.ToInt32(reader["MEMBER_COUNT"]),
                    linkCount    = Convert.ToInt32(reader["LINK_COUNT"]),
                    createdAt    = reader["CREATED_AT"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(
                            reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
                            DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        // ── DELETE /api/Admin/communities/{id} ────────────────────────────────
        [HttpDelete("communities/{id:int}")]
        public IActionResult DeleteCommunity(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Delete all links in the community and their dependents
                var linkCmd = new OracleCommand(
                    "SELECT LINK_ID FROM LINKS WHERE COMMUNITY_ID = :commIdParam", conn);
                linkCmd.Transaction = tx;
                linkCmd.BindByName = true;
                linkCmd.Parameters.Add("commIdParam", OracleDbType.Int32).Value = id;
                var linkIds = new List<int>();
                using (var lr = linkCmd.ExecuteReader())
                    while (lr.Read()) linkIds.Add(Convert.ToInt32(lr["LINK_ID"]));

                foreach (var lid in linkIds)
                {
                    void ExecLink(string sql)
                    {
                        var c = new OracleCommand(sql, conn);
                        c.Transaction = tx;
                        c.BindByName = true;
                        c.Parameters.Add("linkIdParam", OracleDbType.Int32).Value = lid;
                        c.ExecuteNonQuery();
                    }
                    ExecLink("DELETE FROM LINK_COMMENTS WHERE LINK_ID = :linkIdParam");
                    ExecLink("DELETE FROM LINK_LIKES    WHERE LINK_ID = :linkIdParam");
                    ExecLink("DELETE FROM LINK_CLICKS   WHERE LINK_ID = :linkIdParam");
                    ExecLink("DELETE FROM LINKS         WHERE LINK_ID = :linkIdParam");
                }

                var delMembers = new OracleCommand(
                    "DELETE FROM COMMUNITY_MEMBERS WHERE COMMUNITY_ID = :commIdParam", conn);
                delMembers.Transaction = tx;
                delMembers.BindByName = true;
                delMembers.Parameters.Add("commIdParam", OracleDbType.Int32).Value = id;
                delMembers.ExecuteNonQuery();

                var delComm = new OracleCommand(
                    "DELETE FROM COMMUNITIES WHERE COMMUNITY_ID = :commIdParam", conn);
                delComm.Transaction = tx;
                delComm.BindByName = true;
                delComm.Parameters.Add("commIdParam", OracleDbType.Int32).Value = id;
                int rows = delComm.ExecuteNonQuery();

                tx.Commit();
                if (rows == 0) return NotFound(new { message = "Community not found." });
                return Ok(new { message = "Community deleted" });
            }
            catch
            {
                tx.Rollback();
                return StatusCode(500, new { message = "Failed to delete community." });
            }
        }

        // ── GET /api/Admin/links ──────────────────────────────────────────────
        [HttpGet("links")]
        public IActionResult GetLinks()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT
                    l.LINK_ID,
                    l.TITLE,
                    l.URL,
                    l.CLICKS,
                    l.CREATED_AT,
                    u.USERNAME,
                    c.NAME AS COMMUNITY_NAME
                FROM LINKS l
                JOIN USERS       u ON u.USER_ID      = l.USER_ID
                JOIN COMMUNITIES c ON c.COMMUNITY_ID = l.COMMUNITY_ID
                ORDER BY l.CREATED_AT DESC", conn);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    linkId        = Convert.ToInt32(reader["LINK_ID"]),
                    title         = reader["TITLE"]?.ToString(),
                    url           = reader["URL"]?.ToString(),
                    username      = reader["USERNAME"]?.ToString(),
                    communityName = reader["COMMUNITY_NAME"]?.ToString(),
                    clicks        = Convert.ToInt32(reader["CLICKS"]),
                    createdAt     = reader["CREATED_AT"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(
                            reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
                            DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        // ── DELETE /api/Admin/links/{id} ──────────────────────────────────────
        [HttpDelete("links/{id:int}")]
        public IActionResult DeleteLink(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });

            using var conn = _db.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                void Exec(string sql)
                {
                    var c = new OracleCommand(sql, conn);
                    c.Transaction = tx;
                    c.BindByName = true;
                    c.Parameters.Add("linkIdParam", OracleDbType.Int32).Value = id;
                    c.ExecuteNonQuery();
                }

                Exec("DELETE FROM LINK_COMMENTS WHERE LINK_ID = :linkIdParam");
                Exec("DELETE FROM LINK_LIKES    WHERE LINK_ID = :linkIdParam");
                Exec("DELETE FROM LINK_CLICKS   WHERE LINK_ID = :linkIdParam");
                Exec("DELETE FROM LINKS         WHERE LINK_ID = :linkIdParam");

                tx.Commit();
                return Ok(new { message = "Link deleted" });
            }
            catch
            {
                tx.Rollback();
                return StatusCode(500, new { message = "Failed to delete link." });
            }
        }
    }
}
