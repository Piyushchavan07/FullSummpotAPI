using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using FullSummpotAPI.Data;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        public DashboardController(NpgsqlDbContext db) => _db = db;

        [HttpGet]
        public IActionResult GetDashboard()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();
            int uid = Convert.ToInt32(userId);

            using var conn = _db.GetConnection();
            conn.Open();

            using var cmd = new NpgsqlCommand(@"
                SELECT u.username, u.content_niche, u.available_points, u.avatar_url, u.role,
                       (SELECT COUNT(*) FROM follows WHERE following_id = @id AND status = 'ACCEPTED') AS followers_count,
                       (SELECT COUNT(*) FROM follows WHERE follower_id  = @id AND status = 'ACCEPTED') AS following_count
                FROM users u WHERE u.user_id = @id", conn);
            cmd.Parameters.AddWithValue("id", uid);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound();

            var username        = reader["username"]?.ToString();
            var contentNiche    = reader["content_niche"]?.ToString();
            var availablePoints = Convert.ToInt32(reader["available_points"]);
            var avatarUrl       = reader["avatar_url"]?.ToString();
            var role            = reader["role"]?.ToString() ?? "USER";
            var followersCount  = Convert.ToInt32(reader["followers_count"]);
            var followingCount  = Convert.ToInt32(reader["following_count"]);
            reader.Close();

            var followingCommunities = new List<object>();
            using var commCmd = new NpgsqlCommand(@"
                SELECT c.community_id, c.name, c.niche, c.created_at, c.banner_url,
                       u2.username AS creator_name, u2.avatar_url AS creator_avatar,
                       (SELECT url FROM links WHERE community_id = c.community_id ORDER BY created_at DESC LIMIT 1) AS latest_link_url
                FROM communities c
                JOIN users u2 ON c.created_by = u2.user_id
                JOIN follows f ON f.following_id = u2.user_id
                WHERE f.follower_id = @id AND f.status = 'ACCEPTED'
                ORDER BY c.created_at DESC", conn);
            commCmd.Parameters.AddWithValue("id", uid);

            using var commReader = commCmd.ExecuteReader();
            while (commReader.Read())
            {
                followingCommunities.Add(new
                {
                    communityId   = Convert.ToInt32(commReader["community_id"]),
                    name          = commReader["name"]?.ToString(),
                    niche         = commReader["niche"]?.ToString(),
                    bannerUrl     = commReader["banner_url"] == DBNull.Value ? null : commReader["banner_url"].ToString(),
                    creatorName   = commReader["creator_name"]?.ToString(),
                    creatorAvatar = commReader["creator_avatar"] == DBNull.Value ? null : commReader["creator_avatar"].ToString(),
                    latestLinkUrl = commReader["latest_link_url"] == DBNull.Value ? null : commReader["latest_link_url"].ToString(),
                    createdAt     = DateTime.SpecifyKind(commReader.GetDateTime(commReader.GetOrdinal("created_at")), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }

            return Ok(new
            {
                username,
                contentNiche,
                availablePoints,
                followersCount,
                followingCount,
                avatarUrl,
                role,
                followingCommunities
            });
        }
    }
}
