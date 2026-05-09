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

        public LeaderboardController(OracleDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public IActionResult GetLeaderboard()
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"SELECT USERNAME, CONTENT_NICHE, AVAILABLE_POINTS
                  FROM USERS
                  ORDER BY AVAILABLE_POINTS DESC",
                conn);

            using var reader = cmd.ExecuteReader();

            var list = new List<object>();
            int rank = 1;

            while (reader.Read())
            {
                list.Add(new
                {
                    Rank = rank++,
                    Username = reader["USERNAME"],
                    Niche = reader["CONTENT_NICHE"],
                    Points = reader["AVAILABLE_POINTS"]
                });
            }

            return Ok(list);
        }
    }
}