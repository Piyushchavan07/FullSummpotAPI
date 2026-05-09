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
    public class DashboardController : ControllerBase
    {
        private readonly OracleDbContext _db;

        public DashboardController(OracleDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public IActionResult GetDashboard()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (userId == null)
                return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"SELECT USERNAME, CONTENT_NICHE,
                         AVAILABLE_POINTS,
                         POINTS_EARNED_TODAY,
                         VIEWS_GIVEN_TODAY,
                         COMMUNITIES_JOINED
                  FROM USERS
                  WHERE USER_ID = :id",
                conn);

            cmd.Parameters.Add(new OracleParameter("id", userId));

            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound();

            return Ok(new
            {
                Username = reader["USERNAME"]?.ToString(),
                ContentNiche = reader["CONTENT_NICHE"]?.ToString(),
                AvailablePoints = Convert.ToInt32(reader["AVAILABLE_POINTS"]),
                PointsEarnedToday = Convert.ToInt32(reader["POINTS_EARNED_TODAY"]),
                ViewsGivenToday = Convert.ToInt32(reader["VIEWS_GIVEN_TODAY"]),
                CommunitiesJoined = Convert.ToInt32(reader["COMMUNITIES_JOINED"])
            });
        }
    }
}