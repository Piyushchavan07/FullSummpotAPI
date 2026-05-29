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
            if (userId == null) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(@"
                SELECT u.USERNAME, u.CONTENT_NICHE, u.AVAILABLE_POINTS,
                       u.POINTS_EARNED_TODAY, u.VIEWS_GIVEN_TODAY,
                       u.COMMUNITIES_JOINED, u.AVATAR_URL,
                       (SELECT COUNT(*) FROM FOLLOWS WHERE FOLLOWING_ID = :id AND STATUS = 'ACCEPTED') as FOLLOWERS_COUNT,
                       (SELECT COUNT(*) FROM FOLLOWS WHERE FOLLOWER_ID = :id AND STATUS = 'ACCEPTED') as FOLLOWING_COUNT
                FROM USERS u
                WHERE u.USER_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add(new OracleParameter("id", userId));

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound();

            var followingCommunities = new List<object>();

            var commCmd = new OracleCommand(@"
                SELECT c.COMMUNITY_ID, c.NAME, c.NICHE, c.CREATED_AT,
                       u.USERNAME as CREATOR_NAME, u.AVATAR_URL as CREATOR_AVATAR
                FROM COMMUNITIES c
                JOIN USERS u ON c.CREATED_BY = u.USER_ID
                JOIN FOLLOWS f ON f.FOLLOWING_ID = u.USER_ID
                WHERE f.FOLLOWER_ID = :id AND f.STATUS = 'ACCEPTED'
                ORDER BY c.CREATED_AT DESC", conn);
            commCmd.BindByName = true;
            commCmd.Parameters.Add(new OracleParameter("id", userId));

            using var commReader = commCmd.ExecuteReader();
            while (commReader.Read())
            {
                followingCommunities.Add(new
                {
                    communityId = Convert.ToInt32(commReader["COMMUNITY_ID"]),
                    name = commReader["NAME"]?.ToString(),
                    niche = commReader["NICHE"]?.ToString(),
                    creatorName = commReader["CREATOR_NAME"]?.ToString(),
                    creatorAvatar = commReader["CREATOR_AVATAR"]?.ToString(),
                    createdAt = DateTime.SpecifyKind(
                        commReader.GetDateTime(commReader.GetOrdinal("CREATED_AT")),
                        DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }

            return Ok(new
            {
                username = reader["USERNAME"]?.ToString(),           // lowercase — matches frontend
                contentNiche = reader["CONTENT_NICHE"]?.ToString(),
                availablePoints = Convert.ToInt32(reader["AVAILABLE_POINTS"]),
                pointsEarnedToday = Convert.ToInt32(reader["POINTS_EARNED_TODAY"]),
                viewsGivenToday = Convert.ToInt32(reader["VIEWS_GIVEN_TODAY"]),
                communitiesJoined = Convert.ToInt32(reader["COMMUNITIES_JOINED"]),
                followersCount = Convert.ToInt32(reader["FOLLOWERS_COUNT"]),
                followingCount = Convert.ToInt32(reader["FOLLOWING_COUNT"]),
                avatarUrl = reader["AVATAR_URL"]?.ToString(),
                followingCommunities
            });
        }
    }
}
