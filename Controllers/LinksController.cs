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
    public class LinksController : ControllerBase
    {
        private readonly OracleDbContext _db;

        public LinksController(OracleDbContext db)
        {
            _db = db;
        }

        // ✅ Add new link
        [HttpPost]
        public IActionResult Create(CreateLinkDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"INSERT INTO LINKS 
                  (TITLE, URL, COMMUNITY_ID, USER_ID) 
                  VALUES (:titleParam, :urlParam, :communityParam, :userParam)",
                conn);

            cmd.Parameters.Add(new OracleParameter("titleParam", dto.Title));
            cmd.Parameters.Add(new OracleParameter("urlParam", dto.Url));
            cmd.Parameters.Add(new OracleParameter("communityParam", dto.CommunityId));
            cmd.Parameters.Add(new OracleParameter("userParam", userId));

            cmd.ExecuteNonQuery();

            return Ok("Link added successfully");
        }

        // ✅ Get links for a community
        [HttpGet("community/{communityId}")]
        public IActionResult GetByCommunity(int communityId)
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"SELECT l.LINK_ID,
                         l.TITLE,
                         l.URL,
                         l.CLICKS,
                         u.USERNAME
                  FROM LINKS l
                  JOIN USERS u ON u.USER_ID = l.USER_ID
                  WHERE l.COMMUNITY_ID = :communityIdParam",
                conn);

            cmd.Parameters.Add(new OracleParameter("communityIdParam", communityId));

            using var reader = cmd.ExecuteReader();

            var list = new List<object>();

            while (reader.Read())
            {
                list.Add(new
                {
                    LinkId = reader["LINK_ID"],
                    Title = reader["TITLE"]?.ToString(),
                    Url = reader["URL"]?.ToString(),
                    Clicks = Convert.ToInt32(reader["CLICKS"]),
                    Username = reader["USERNAME"]?.ToString()
                });
            }

            return Ok(list);
        }

        // ✅ Register click and give points
        [HttpPost("{linkId}/click")]
        public IActionResult RegisterClick(int linkId)
        {
            using var conn = _db.GetConnection();
            conn.Open();

            // Increase link clicks
            var updateLink = new OracleCommand(
                "UPDATE LINKS SET CLICKS = CLICKS + 1 WHERE LINK_ID = :linkIdParam",
                conn);

            updateLink.Parameters.Add(new OracleParameter("linkIdParam", linkId));
            updateLink.ExecuteNonQuery();

            // Give 1 point to link owner
            var updatePoints = new OracleCommand(
                @"UPDATE USERS 
                  SET AVAILABLE_POINTS = AVAILABLE_POINTS + 1,
                      POINTS_EARNED_TODAY = POINTS_EARNED_TODAY + 1
                  WHERE USER_ID = (
                      SELECT USER_ID FROM LINKS WHERE LINK_ID = :linkIdParam
                  )",
                conn);

            updatePoints.Parameters.Add(new OracleParameter("linkIdParam", linkId));
            updatePoints.ExecuteNonQuery();

            return Ok("Click registered");
        }

        // ✅ My links page
        [HttpGet("my")]
        public IActionResult MyLinks()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"SELECT TITLE, URL, CLICKS
                  FROM LINKS
                  WHERE USER_ID = :userIdParam",
                conn);

            cmd.Parameters.Add(new OracleParameter("userIdParam", userId));

            using var reader = cmd.ExecuteReader();

            var list = new List<object>();

            while (reader.Read())
            {
                list.Add(new
                {
                    Title = reader["TITLE"]?.ToString(),
                    Url = reader["URL"]?.ToString(),
                    Clicks = Convert.ToInt32(reader["CLICKS"])
                });
            }

            return Ok(list);
        }
    }
}