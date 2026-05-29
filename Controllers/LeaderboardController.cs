using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using FullSummpotAPI.Data;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LeaderboardController : ControllerBase
    {
        private readonly OracleDbContext _db;

        public LeaderboardController(OracleDbContext db) => _db = db;

        [HttpGet]
        public IActionResult GetLeaderboard()
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT u.USER_ID, u.USERNAME, u.CONTENT_NICHE,
                       u.AVAILABLE_POINTS, u.AVATAR_URL,
                       COUNT(l.LINK_ID)          AS LINKS_SUBMITTED,
                       NVL(SUM(l.CLICKS), 0)     AS TOTAL_CLICKS
                FROM USERS u
                LEFT JOIN LINKS l ON u.USER_ID = l.USER_ID
                GROUP BY u.USER_ID, u.USERNAME, u.CONTENT_NICHE,
                         u.AVAILABLE_POINTS, u.AVATAR_URL
                ORDER BY u.AVAILABLE_POINTS DESC
                FETCH FIRST 100 ROWS ONLY", conn);

            using var reader = cmd.ExecuteReader();
            var list = new List<object>();
            int rank = 1;
            while (reader.Read())
            {
                list.Add(new
                {
                    rank           = rank++,
                    userId         = Convert.ToInt32(reader["USER_ID"]),
                    username       = reader["USERNAME"]?.ToString(),
                    niche          = reader["CONTENT_NICHE"]?.ToString(),
                    points         = Convert.ToInt32(reader["AVAILABLE_POINTS"]),
                    avatarUrl      = reader["AVATAR_URL"] == DBNull.Value ? null : reader["AVATAR_URL"].ToString(),
                    linksSubmitted = Convert.ToInt32(reader["LINKS_SUBMITTED"]),
                    clicksReceived = Convert.ToInt32(reader["TOTAL_CLICKS"])
                });
            }
            return Ok(list);
        }
    }
}
