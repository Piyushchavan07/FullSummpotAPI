using FullSummpotAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        public AdminController(NpgsqlDbContext db) => _db = db;
        private bool IsAdmin() => User.FindFirst(ClaimTypes.Role)?.Value == "ADMIN";

        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT
                    (SELECT COUNT(*) FROM users)       AS total_users,
                    (SELECT COUNT(*) FROM communities) AS total_communities,
                    (SELECT COUNT(*) FROM links)       AS total_links,
                    (SELECT COALESCE(SUM(clicks), 0) FROM links) AS total_clicks", conn);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return Ok(new
            {
                totalUsers       = Convert.ToInt32(reader["total_users"]),
                totalCommunities = Convert.ToInt32(reader["total_communities"]),
                totalLinks       = Convert.ToInt32(reader["total_links"]),
                totalClicks      = Convert.ToInt32(reader["total_clicks"])
            });
        }

        [HttpGet("users")]
        public IActionResult GetUsers()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.email, u.role, u.available_points, u.created_at,
                    (SELECT COUNT(*) FROM communities WHERE created_by = u.user_id) AS communities_created,
                    (SELECT COUNT(*) FROM links WHERE user_id = u.user_id) AS links_submitted,
                    (SELECT COALESCE(SUM(l2.clicks),0) FROM links l2 WHERE l2.user_id = u.user_id) AS total_clicks
                FROM users u ORDER BY u.created_at DESC", conn);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    userId             = Convert.ToInt32(reader["user_id"]),
                    username           = reader["username"]?.ToString(),
                    email              = reader["email"]?.ToString(),
                    role               = reader["role"]?.ToString() ?? "USER",
                    availablePoints    = Convert.ToInt32(reader["available_points"]),
                    communitiesCreated = Convert.ToInt32(reader["communities_created"]),
                    linksSubmitted     = Convert.ToInt32(reader["links_submitted"]),
                    totalClicks        = Convert.ToInt32(reader["total_clicks"]),
                    createdAt          = reader["created_at"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("created_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpPost("users/{id:int}/make-admin")]
        public IActionResult MakeAdmin(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand("UPDATE users SET role = 'ADMIN' WHERE user_id = @id", conn);
            cmd.Parameters.AddWithValue("id", id);
            int rows = cmd.ExecuteNonQuery();
            if (rows == 0) return NotFound(new { message = "User not found." });
            return Ok(new { message = "User promoted to admin" });
        }

        [HttpDelete("users/{id:int}")]
        public IActionResult DeleteUser(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            var selfId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (selfId != null && Convert.ToInt32(selfId) == id)
                return BadRequest(new { message = "You cannot delete your own account." });

            using var conn = _db.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                void Exec(string sql)
                {
                    using var c = new NpgsqlCommand(sql, conn, tx);
                    c.Parameters.AddWithValue("id", id);
                    c.ExecuteNonQuery();
                }
                Exec("DELETE FROM link_comments          WHERE user_id = @id");
                Exec("DELETE FROM link_likes             WHERE user_id = @id");
                Exec("DELETE FROM link_clicks            WHERE clicker_user_id = @id");
                Exec("DELETE FROM notifications          WHERE user_id = @id OR sender_id = @id");
                Exec("DELETE FROM message_requests       WHERE sender_id = @id OR recipient_id = @id");
                Exec("DELETE FROM conversation_participants WHERE user_id = @id");

                using var linkCmd = new NpgsqlCommand("SELECT link_id FROM links WHERE user_id = @id", conn, tx);
                linkCmd.Parameters.AddWithValue("id", id);
                var linkIds = new List<int>();
                using (var lr = linkCmd.ExecuteReader())
                    while (lr.Read()) linkIds.Add(Convert.ToInt32(lr["link_id"]));

                foreach (var lid in linkIds)
                {
                    void ExecLink(string sql) { using var c = new NpgsqlCommand(sql, conn, tx); c.Parameters.AddWithValue("lid", lid); c.ExecuteNonQuery(); }
                    ExecLink("DELETE FROM link_comments WHERE link_id = @lid");
                    ExecLink("DELETE FROM link_likes    WHERE link_id = @lid");
                    ExecLink("DELETE FROM link_clicks   WHERE link_id = @lid");
                    ExecLink("DELETE FROM links         WHERE link_id = @lid");
                }

                using var commCmd = new NpgsqlCommand("SELECT community_id FROM communities WHERE created_by = @id", conn, tx);
                commCmd.Parameters.AddWithValue("id", id);
                var commIds = new List<int>();
                using (var cr = commCmd.ExecuteReader())
                    while (cr.Read()) commIds.Add(Convert.ToInt32(cr["community_id"]));

                foreach (var cid in commIds)
                {
                    using var clCmd = new NpgsqlCommand("SELECT link_id FROM links WHERE community_id = @cid", conn, tx);
                    clCmd.Parameters.AddWithValue("cid", cid);
                    var clIds = new List<int>();
                    using (var clr = clCmd.ExecuteReader())
                        while (clr.Read()) clIds.Add(Convert.ToInt32(clr["link_id"]));
                    foreach (var lid in clIds)
                    {
                        void ExecCL(string sql) { using var c = new NpgsqlCommand(sql, conn, tx); c.Parameters.AddWithValue("lid", lid); c.ExecuteNonQuery(); }
                        ExecCL("DELETE FROM link_comments WHERE link_id = @lid");
                        ExecCL("DELETE FROM link_likes    WHERE link_id = @lid");
                        ExecCL("DELETE FROM link_clicks   WHERE link_id = @lid");
                        ExecCL("DELETE FROM links         WHERE link_id = @lid");
                    }
                    using var dm = new NpgsqlCommand("DELETE FROM community_members WHERE community_id = @cid", conn, tx);
                    dm.Parameters.AddWithValue("cid", cid); dm.ExecuteNonQuery();
                    using var dc = new NpgsqlCommand("DELETE FROM communities WHERE community_id = @cid", conn, tx);
                    dc.Parameters.AddWithValue("cid", cid); dc.ExecuteNonQuery();
                }

                Exec("DELETE FROM community_members WHERE user_id = @id");
                Exec("DELETE FROM follows WHERE follower_id = @id OR following_id = @id");
                Exec("DELETE FROM users WHERE user_id = @id");
                tx.Commit();
                return Ok(new { message = "User deleted" });
            }
            catch { tx.Rollback(); return StatusCode(500, new { message = "Failed to delete user." }); }
        }

        [HttpGet("communities")]
        public IActionResult GetCommunities()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT c.community_id, c.name, c.niche, c.created_at, u.username AS creator_name,
                    (SELECT COUNT(*) FROM community_members cm WHERE cm.community_id = c.community_id) AS member_count,
                    (SELECT COUNT(*) FROM links l WHERE l.community_id = c.community_id) AS link_count
                FROM communities c JOIN users u ON u.user_id = c.created_by
                ORDER BY c.created_at DESC", conn);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    communityId = Convert.ToInt32(reader["community_id"]),
                    name        = reader["name"]?.ToString(),
                    niche       = reader["niche"]?.ToString(),
                    creatorName = reader["creator_name"]?.ToString(),
                    memberCount = Convert.ToInt32(reader["member_count"]),
                    linkCount   = Convert.ToInt32(reader["link_count"]),
                    createdAt   = reader["created_at"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("created_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpDelete("communities/{id:int}")]
        public IActionResult DeleteCommunity(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                using var linkCmd = new NpgsqlCommand("SELECT link_id FROM links WHERE community_id = @id", conn, tx);
                linkCmd.Parameters.AddWithValue("id", id);
                var linkIds = new List<int>();
                using (var lr = linkCmd.ExecuteReader())
                    while (lr.Read()) linkIds.Add(Convert.ToInt32(lr["link_id"]));
                foreach (var lid in linkIds)
                {
                    void ExecLink(string sql) { using var c = new NpgsqlCommand(sql, conn, tx); c.Parameters.AddWithValue("lid", lid); c.ExecuteNonQuery(); }
                    ExecLink("DELETE FROM link_comments WHERE link_id = @lid");
                    ExecLink("DELETE FROM link_likes    WHERE link_id = @lid");
                    ExecLink("DELETE FROM link_clicks   WHERE link_id = @lid");
                    ExecLink("DELETE FROM links         WHERE link_id = @lid");
                }
                using var dm = new NpgsqlCommand("DELETE FROM community_members WHERE community_id = @id", conn, tx);
                dm.Parameters.AddWithValue("id", id); dm.ExecuteNonQuery();
                using var dc = new NpgsqlCommand("DELETE FROM communities WHERE community_id = @id", conn, tx);
                dc.Parameters.AddWithValue("id", id);
                int rows = dc.ExecuteNonQuery();
                tx.Commit();
                if (rows == 0) return NotFound(new { message = "Community not found." });
                return Ok(new { message = "Community deleted" });
            }
            catch { tx.Rollback(); return StatusCode(500, new { message = "Failed to delete community." }); }
        }

        [HttpGet("links")]
        public IActionResult GetLinks()
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT l.link_id, l.title, l.url, l.clicks, l.created_at,
                       u.username, c.name AS community_name
                FROM links l
                JOIN users u ON u.user_id = l.user_id
                JOIN communities c ON c.community_id = l.community_id
                ORDER BY l.created_at DESC", conn);
            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    linkId        = Convert.ToInt32(reader["link_id"]),
                    title         = reader["title"]?.ToString(),
                    url           = reader["url"]?.ToString(),
                    username      = reader["username"]?.ToString(),
                    communityName = reader["community_name"]?.ToString(),
                    clicks        = Convert.ToInt32(reader["clicks"]),
                    createdAt     = reader["created_at"] == DBNull.Value ? null :
                        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("created_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }
            return Ok(list);
        }

        [HttpDelete("links/{id:int}")]
        public IActionResult DeleteLink(int id)
        {
            if (!IsAdmin()) return StatusCode(403, new { message = "Admin access required." });
            using var conn = _db.GetConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                void Exec(string sql) { using var c = new NpgsqlCommand(sql, conn, tx); c.Parameters.AddWithValue("id", id); c.ExecuteNonQuery(); }
                Exec("DELETE FROM link_comments WHERE link_id = @id");
                Exec("DELETE FROM link_likes    WHERE link_id = @id");
                Exec("DELETE FROM link_clicks   WHERE link_id = @id");
                using var dc = new NpgsqlCommand("DELETE FROM links WHERE link_id = @id", conn, tx);
                dc.Parameters.AddWithValue("id", id);
                int rows = dc.ExecuteNonQuery();
                tx.Commit();
                if (rows == 0) return NotFound(new { message = "Link not found." });
                return Ok(new { message = "Link deleted" });
            }
            catch { tx.Rollback(); return StatusCode(500, new { message = "Failed to delete link." }); }
        }
    }
}
