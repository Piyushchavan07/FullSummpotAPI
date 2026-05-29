using FullSummpotAPI.Data;
using FullSummpotAPI.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class CommunitiesController : ControllerBase
    {
        private readonly OracleDbContext _db;

        private static readonly HashSet<string> AllowedNiches = new(StringComparer.OrdinalIgnoreCase)
        {
            "Gaming","Tech","Education","Music","Comedy","Vlogging",
            "Finance","Fitness","Food","Travel","Other"
        };

        private static readonly string[] AllowedImageTypes = { "image/jpeg", "image/png", "image/webp" };
        private static readonly string[] AllowedImageExts  = { ".jpg", ".jpeg", ".png", ".webp" };

        public CommunitiesController(OracleDbContext db) => _db = db;

        private bool TryGetUserId(out int userId)
        {
            userId = 0;
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return !string.IsNullOrWhiteSpace(claim) && int.TryParse(claim, out userId);
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = @"
                SELECT c.COMMUNITY_ID, c.NAME, c.DESCRIPTION, c.NICHE, c.CREATED_BY,
                       c.BANNER_URL,
                       u.USERNAME as CREATOR_NAME, u.AVATAR_URL as CREATOR_AVATAR,
                       COUNT(cm.USER_ID) AS MEMBER_COUNT,
                       CASE WHEN SUM(CASE WHEN cm.USER_ID = :userIdParam THEN 1 ELSE 0 END) > 0 THEN 1 ELSE 0 END AS IS_MEMBER,
                       CASE WHEN c.CREATED_BY = :userIdParam THEN 1 ELSE 0 END AS IS_CREATOR
                FROM COMMUNITIES c
                LEFT JOIN COMMUNITY_MEMBERS cm ON cm.COMMUNITY_ID = c.COMMUNITY_ID
                LEFT JOIN USERS u ON u.USER_ID = c.CREATED_BY
                GROUP BY c.COMMUNITY_ID, c.NAME, c.DESCRIPTION, c.NICHE, c.CREATED_BY,
                         c.BANNER_URL, u.USERNAME, u.AVATAR_URL";
            cmd.Parameters.Add("userIdParam", OracleDbType.Int32).Value = userId;

            using var reader = cmd.ExecuteReader();
            var communities = new List<object>();
            while (reader.Read())
            {
                communities.Add(new
                {
                    communityId  = reader.GetInt32(reader.GetOrdinal("COMMUNITY_ID")),
                    name         = reader["NAME"]?.ToString(),
                    description  = reader["DESCRIPTION"]?.ToString(),
                    niche        = reader["NICHE"]?.ToString(),
                    bannerUrl    = reader["BANNER_URL"] == DBNull.Value ? null : reader["BANNER_URL"].ToString(),
                    memberCount  = reader.GetInt32(reader.GetOrdinal("MEMBER_COUNT")),
                    isMember     = reader.GetInt32(reader.GetOrdinal("IS_MEMBER")) == 1,
                    isCreator    = reader.GetInt32(reader.GetOrdinal("IS_CREATOR")) == 1,
                    creatorId    = reader.GetInt32(reader.GetOrdinal("CREATED_BY")),
                    creatorName  = reader["CREATOR_NAME"]?.ToString(),
                    creatorAvatar= reader["CREATOR_AVATAR"]?.ToString()
                });
            }
            return Ok(communities);
        }

        [HttpGet("{id:int}")]
        public IActionResult GetById(int id)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = @"
                SELECT c.COMMUNITY_ID, c.NAME, c.DESCRIPTION, c.NICHE, c.CREATED_BY,
                       c.BANNER_URL,
                       u.USERNAME as CREATOR_NAME, u.AVATAR_URL as CREATOR_AVATAR,
                       COUNT(cm.USER_ID) AS MEMBER_COUNT,
                       CASE WHEN SUM(CASE WHEN cm.USER_ID = :userIdParam THEN 1 ELSE 0 END) > 0 THEN 1 ELSE 0 END AS IS_MEMBER,
                       CASE WHEN c.CREATED_BY = :userIdParam THEN 1 ELSE 0 END AS IS_CREATOR
                FROM COMMUNITIES c
                LEFT JOIN COMMUNITY_MEMBERS cm ON cm.COMMUNITY_ID = c.COMMUNITY_ID
                LEFT JOIN USERS u ON u.USER_ID = c.CREATED_BY
                WHERE c.COMMUNITY_ID = :communityIdParam
                GROUP BY c.COMMUNITY_ID, c.NAME, c.DESCRIPTION, c.NICHE, c.CREATED_BY,
                         c.BANNER_URL, u.USERNAME, u.AVATAR_URL";
            cmd.Parameters.Add("userIdParam", OracleDbType.Int32).Value = userId;
            cmd.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = id;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound(new { message = "Community not found." });

            return Ok(new
            {
                communityId  = reader.GetInt32(reader.GetOrdinal("COMMUNITY_ID")),
                name         = reader["NAME"]?.ToString(),
                description  = reader["DESCRIPTION"]?.ToString(),
                niche        = reader["NICHE"]?.ToString(),
                bannerUrl    = reader["BANNER_URL"] == DBNull.Value ? null : reader["BANNER_URL"].ToString(),
                memberCount  = reader.GetInt32(reader.GetOrdinal("MEMBER_COUNT")),
                isMember     = reader.GetInt32(reader.GetOrdinal("IS_MEMBER")) == 1,
                isCreator    = reader.GetInt32(reader.GetOrdinal("IS_CREATOR")) == 1,
                creatorId    = reader.GetInt32(reader.GetOrdinal("CREATED_BY")),
                creatorName  = reader["CREATOR_NAME"]?.ToString(),
                creatorAvatar= reader["CREATOR_AVATAR"]?.ToString()
            });
        }

        [HttpPost]
        public IActionResult Create([FromBody] CreateCommunityDto dto)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            if (!ModelState.IsValid) return BadRequest(ModelState);

            if (!AllowedNiches.Contains(dto.Niche))
                return BadRequest(new { message = "Invalid niche selected." });

            using var conn = _db.GetConnection();
            conn.Open();
            using var transaction = conn.BeginTransaction();
            try
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.BindByName = true;
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO COMMUNITIES (NAME, DESCRIPTION, NICHE, CREATED_BY)
                    VALUES (:nameParam, :descParam, :nicheParam, :createdByParam)";
                insertCmd.Parameters.Add("nameParam",      OracleDbType.Varchar2).Value = dto.Name;
                insertCmd.Parameters.Add("descParam",      OracleDbType.Varchar2).Value = dto.Description;
                insertCmd.Parameters.Add("nicheParam",     OracleDbType.Varchar2).Value = dto.Niche;
                insertCmd.Parameters.Add("createdByParam", OracleDbType.Int32).Value    = userId;
                insertCmd.ExecuteNonQuery();

                using var getIdCmd = conn.CreateCommand();
                getIdCmd.Transaction = transaction;
                getIdCmd.CommandText = "SELECT MAX(COMMUNITY_ID) FROM COMMUNITIES";
                var newCommunityId = Convert.ToInt32(getIdCmd.ExecuteScalar());

                using var joinCmd = conn.CreateCommand();
                joinCmd.BindByName = true;
                joinCmd.Transaction = transaction;
                joinCmd.CommandText = @"
                    INSERT INTO COMMUNITY_MEMBERS (USER_ID, COMMUNITY_ID)
                    VALUES (:userIdParam, :communityIdParam)";
                joinCmd.Parameters.Add("userIdParam",      OracleDbType.Int32).Value = userId;
                joinCmd.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = newCommunityId;
                joinCmd.ExecuteNonQuery();

                transaction.Commit();
                return CreatedAtAction(nameof(GetById),
                    new { id = newCommunityId },
                    new { message = "Community created successfully", communityId = newCommunityId });
            }
            catch (Exception)
            {
                transaction.Rollback();
                return StatusCode(500, new { error = "Failed to create community." });
            }
        }

        [HttpPut("{id:int}")]
        public IActionResult Update(int id, [FromBody] CreateCommunityDto dto)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            if (!ModelState.IsValid) return BadRequest(ModelState);

            if (!AllowedNiches.Contains(dto.Niche))
                return BadRequest(new { message = "Invalid niche selected." });

            using var conn = _db.GetConnection();
            conn.Open();

            var check = new OracleCommand(
                "SELECT COUNT(*) FROM COMMUNITIES WHERE COMMUNITY_ID = :id AND CREATED_BY = :userId", conn);
            check.BindByName = true;
            check.Parameters.Add("id",     OracleDbType.Int32).Value = id;
            check.Parameters.Add("userId", OracleDbType.Int32).Value = userId;
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                return StatusCode(403, new { message = "Only the creator can edit this community." });

            var cmd = new OracleCommand(@"
                UPDATE COMMUNITIES SET NAME = :name, DESCRIPTION = :desc, NICHE = :niche
                WHERE COMMUNITY_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("name",  OracleDbType.Varchar2).Value = dto.Name;
            cmd.Parameters.Add("desc",  OracleDbType.Varchar2).Value = dto.Description;
            cmd.Parameters.Add("niche", OracleDbType.Varchar2).Value = dto.Niche;
            cmd.Parameters.Add("id",    OracleDbType.Int32).Value    = id;
            cmd.ExecuteNonQuery();

            return Ok(new { message = "Community updated successfully" });
        }

        [HttpPost("{id:int}/join")]
        public IActionResult Join(int id)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = @"
                INSERT INTO COMMUNITY_MEMBERS (USER_ID, COMMUNITY_ID)
                SELECT :userIdParam, :communityIdParam FROM DUAL
                WHERE EXISTS (SELECT 1 FROM COMMUNITIES WHERE COMMUNITY_ID = :communityIdParam)
                  AND NOT EXISTS (
                    SELECT 1 FROM COMMUNITY_MEMBERS
                    WHERE USER_ID = :userIdParam AND COMMUNITY_ID = :communityIdParam
                  )";
            cmd.Parameters.Add("userIdParam",      OracleDbType.Int32).Value = userId;
            cmd.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = id;

            if (cmd.ExecuteNonQuery() == 0)
                return BadRequest(new { message = "Community not found or already joined." });

            return Ok(new { message = "Joined successfully" });
        }

        [HttpPost("{id:int}/leave")]
        public IActionResult Leave(int id)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            var checkCmd = conn.CreateCommand();
            checkCmd.BindByName = true;
            checkCmd.CommandText = "SELECT CREATED_BY FROM COMMUNITIES WHERE COMMUNITY_ID = :communityIdParam";
            checkCmd.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = id;
            var createdBy = checkCmd.ExecuteScalar();

            if (createdBy != null && Convert.ToInt32(createdBy) == userId)
                return BadRequest(new { message = "Community creator cannot leave. Delete the community instead." });

            var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = @"
                DELETE FROM COMMUNITY_MEMBERS
                WHERE USER_ID = :userIdParam AND COMMUNITY_ID = :communityIdParam";
            cmd.Parameters.Add("userIdParam",      OracleDbType.Int32).Value = userId;
            cmd.Parameters.Add("communityIdParam", OracleDbType.Int32).Value = id;

            if (cmd.ExecuteNonQuery() == 0)
                return NotFound(new { message = "Membership not found." });

            return Ok(new { message = "Left successfully" });
        }

        [HttpPost("{id:int}/banner")]
        public async Task<IActionResult> UploadBanner(int id, IFormFile file)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();

            if (file == null || file.Length == 0)
                return BadRequest(new { message = "No file uploaded." });

            if (file.Length > 5 * 1024 * 1024)
                return BadRequest(new { message = "Banner must be under 5 MB." });

            if (!AllowedImageTypes.Contains(file.ContentType.ToLower()))
                return BadRequest(new { message = "Only JPEG, PNG and WebP images are allowed." });

            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!AllowedImageExts.Contains(ext))
                return BadRequest(new { message = "Invalid file extension." });

            using var conn = _db.GetConnection();
            conn.Open();

            var check = new OracleCommand(
                "SELECT COUNT(*) FROM COMMUNITIES WHERE COMMUNITY_ID = :id AND CREATED_BY = :userId", conn);
            check.BindByName = true;
            check.Parameters.Add("id",     OracleDbType.Int32).Value = id;
            check.Parameters.Add("userId", OracleDbType.Int32).Value = userId;
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                return StatusCode(403, new { message = "Only the community creator can upload a banner." });

            // Delete old banner
            var oldCmd = new OracleCommand("SELECT BANNER_URL FROM COMMUNITIES WHERE COMMUNITY_ID = :id", conn);
            oldCmd.BindByName = true;
            oldCmd.Parameters.Add("id", OracleDbType.Int32).Value = id;
            var oldUrl = oldCmd.ExecuteScalar()?.ToString();
            if (!string.IsNullOrEmpty(oldUrl))
            {
                var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", oldUrl.TrimStart('/'));
                if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
            }

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "banners");
            Directory.CreateDirectory(uploadsPath);

            var safeFileName = $"banner_{id}_{DateTime.UtcNow.Ticks}{ext}";
            var filePath = Path.Combine(uploadsPath, safeFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            var publicUrl = $"/uploads/banners/{safeFileName}";

            var cmd = new OracleCommand(
                "UPDATE COMMUNITIES SET BANNER_URL = :url WHERE COMMUNITY_ID = :id", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("url", OracleDbType.Varchar2).Value = publicUrl;
            cmd.Parameters.Add("id",  OracleDbType.Int32).Value    = id;
            cmd.ExecuteNonQuery();

            return Ok(new { bannerUrl = publicUrl });
        }
    }
}
