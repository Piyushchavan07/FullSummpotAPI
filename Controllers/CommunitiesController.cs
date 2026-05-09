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
    public class CommunitiesController : ControllerBase
    {
        private readonly OracleDbContext _db;

        public CommunitiesController(OracleDbContext db)
        {
            _db = db;
        }

        // ✅ Get all communities with member count
        [HttpGet]
        public IActionResult GetAll()
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"SELECT c.COMMUNITY_ID,
                         c.NAME,
                         c.DESCRIPTION,
                         c.NICHE,
                         (SELECT COUNT(*) 
                          FROM COMMUNITY_MEMBERS m 
                          WHERE m.COMMUNITY_ID = c.COMMUNITY_ID) AS MEMBER_COUNT
                  FROM COMMUNITIES c",
                conn);

            using var reader = cmd.ExecuteReader();

            var list = new List<object>();

            while (reader.Read())
            {
                list.Add(new
                {
                    CommunityId = reader["COMMUNITY_ID"],
                    Name = reader["NAME"]?.ToString(),
                    Description = reader["DESCRIPTION"]?.ToString(),
                    Niche = reader["NICHE"]?.ToString(),
                    MemberCount = Convert.ToInt32(reader["MEMBER_COUNT"])
                });
            }

            return Ok(list);
        }

        // ✅ Get single community with member count
        [HttpGet("{id}")]
        public IActionResult GetById(int id)
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                @"SELECT c.COMMUNITY_ID,
                         c.NAME,
                         c.DESCRIPTION,
                         c.NICHE,
                         (SELECT COUNT(*) 
                          FROM COMMUNITY_MEMBERS m 
                          WHERE m.COMMUNITY_ID = c.COMMUNITY_ID) AS MEMBER_COUNT
                  FROM COMMUNITIES c
                  WHERE c.COMMUNITY_ID = :id",
                conn);

            cmd.Parameters.Add(new OracleParameter("id", id));

            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound();

            return Ok(new
            {
                CommunityId = reader["COMMUNITY_ID"],
                Name = reader["NAME"]?.ToString(),
                Description = reader["DESCRIPTION"]?.ToString(),
                Niche = reader["NICHE"]?.ToString(),
                MemberCount = Convert.ToInt32(reader["MEMBER_COUNT"])
            });
        }

        // ✅ Create community
        [HttpPost]
        public IActionResult Create(CreateCommunityDto dto)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            using var conn = _db.GetConnection();
            conn.Open();

            var cmd = new OracleCommand(
                "INSERT INTO COMMUNITIES (NAME, DESCRIPTION, NICHE, CREATED_BY) VALUES (:n, :d, :ni, :u)",
                conn);

            cmd.Parameters.Add(new OracleParameter("n", dto.Name));
            cmd.Parameters.Add(new OracleParameter("d", dto.Description));
            cmd.Parameters.Add(new OracleParameter("ni", dto.Niche));
            cmd.Parameters.Add(new OracleParameter("u", userId));

            cmd.ExecuteNonQuery();

            return Ok("Community created successfully");
        }

        // ✅ Join community
        [HttpPost("{id}/join")]
        public IActionResult Join(int id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            using var conn = _db.GetConnection();
            conn.Open();

            var checkCmd = new OracleCommand(
                "SELECT COUNT(*) FROM COMMUNITY_MEMBERS WHERE USER_ID = :u AND COMMUNITY_ID = :c",
                conn);

            checkCmd.Parameters.Add(new OracleParameter("u", userId));
            checkCmd.Parameters.Add(new OracleParameter("c", id));

            var exists = Convert.ToInt32(checkCmd.ExecuteScalar());

            if (exists > 0)
                return BadRequest("Already joined");

            var cmd = new OracleCommand(
                "INSERT INTO COMMUNITY_MEMBERS (USER_ID, COMMUNITY_ID) VALUES (:u, :c)",
                conn);

            cmd.Parameters.Add(new OracleParameter("u", userId));
            cmd.Parameters.Add(new OracleParameter("c", id));

            cmd.ExecuteNonQuery();

            // ✅ Update dashboard counter
            var updateCmd = new OracleCommand(
                "UPDATE USERS SET COMMUNITIES_JOINED = COMMUNITIES_JOINED + 1 WHERE USER_ID = :u",
                conn);

            updateCmd.Parameters.Add(new OracleParameter("u", userId));
            updateCmd.ExecuteNonQuery();

            return Ok("Joined successfully");
        }
    }
}