using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using FullSummpotAPI.Data;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LeaderboardController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        public LeaderboardController(NpgsqlDbContext db) => _db = db;

        [HttpGet]
        public IActionResult GetLeaderboard()
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.content_niche,
                       u.available_points, u.avatar_url,
                       COUNT(l.link_id)           AS links_submitted,
                       COALESCE(SUM(l.clicks), 0) AS total_clicks
                FROM users u
                LEFT JOIN links l ON u.user_id = l.user_id
                GROUP BY u.user_id, u.username, u.content_niche, u.available_points, u.avatar_url
                ORDER BY u.available_points DESC
                LIMIT 100", conn);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            int rank = 1;
            while (reader.Read())
            {
                list.Add(new
                {
                    rank           = rank++,
                    userId         = Convert.ToInt32(reader["user_id"]),
                    username       = reader["username"]?.ToString(),
                    niche          = reader["content_niche"]?.ToString(),
                    points         = Convert.ToInt32(reader["available_points"]),
                    avatarUrl      = reader["avatar_url"] == DBNull.Value ? null : reader["avatar_url"].ToString(),
                    linksSubmitted = Convert.ToInt32(reader["links_submitted"]),
                    clicksReceived = Convert.ToInt32(reader["total_clicks"])
                });
            }
            return Ok(list);
        }
    }
}
