using FullSummpotAPI.Data;
using FullSummpotAPI.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class CommunitiesController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        private static readonly HashSet<string> AllowedNiches = new(StringComparer.OrdinalIgnoreCase)
        { "Gaming","Tech","Education","Music","Comedy","Vlogging","Finance","Fitness","Food","Travel","Other" };
        private static readonly string[] AllowedImageTypes = { "image/jpeg", "image/png", "image/webp" };
        private static readonly string[] AllowedImageExts  = { ".jpg", ".jpeg", ".png", ".webp" };

        public CommunitiesController(NpgsqlDbContext db) => _db = db;

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
            using var cmd = new NpgsqlCommand(@"
                SELECT c.community_id, c.name, c.description, c.niche, c.created_by, c.banner_url,
                       u.username AS creator_name, u.avatar_url AS creator_avatar,
                       COUNT(cm.user_id) AS member_count,
                       CASE WHEN SUM(CASE WHEN cm.user_id = @uid THEN 1 ELSE 0 END) > 0 THEN TRUE ELSE FALSE END AS is_member,
                       CASE WHEN c.created_by = @uid THEN TRUE ELSE FALSE END AS is_creator,
                       (SELECT url FROM links WHERE community_id = c.community_id ORDER BY created_at DESC LIMIT 1) AS latest_link_url
                FROM communities c
                LEFT JOIN community_members cm ON cm.community_id = c.community_id
                LEFT JOIN users u ON u.user_id = c.created_by
                GROUP BY c.community_id, c.name, c.description, c.niche, c.created_by,
                         c.banner_url, u.username, u.avatar_url", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            using var reader = cmd.ExecuteReader();
            var communities = new List<object>();
            while (reader.Read())
            {
                communities.Add(new
                {
                    communityId   = reader.GetInt32(reader.GetOrdinal("community_id")),
                    name          = reader["name"]?.ToString(),
                    description   = reader["description"]?.ToString(),
                    niche         = reader["niche"]?.ToString(),
                    bannerUrl     = reader["banner_url"] == DBNull.Value ? null : reader["banner_url"].ToString(),
                    memberCount   = Convert.ToInt32(reader["member_count"]),
                    isMember      = Convert.ToBoolean(reader["is_member"]),
                    isCreator     = Convert.ToBoolean(reader["is_creator"]),
                    creatorId     = reader.GetInt32(reader.GetOrdinal("created_by")),
                    creatorName   = reader["creator_name"]?.ToString(),
                    creatorAvatar = reader["creator_avatar"]?.ToString(),
                    latestLinkUrl = reader["latest_link_url"] == DBNull.Value ? null : reader["latest_link_url"].ToString()
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
            using var cmd = new NpgsqlCommand(@"
                SELECT c.community_id, c.name, c.description, c.niche, c.created_by, c.banner_url,
                       u.username AS creator_name, u.avatar_url AS creator_avatar,
                       COUNT(cm.user_id) AS member_count,
                       CASE WHEN SUM(CASE WHEN cm.user_id = @uid THEN 1 ELSE 0 END) > 0 THEN TRUE ELSE FALSE END AS is_member,
                       CASE WHEN c.created_by = @uid THEN TRUE ELSE FALSE END AS is_creator,
                       (SELECT url FROM links WHERE community_id = c.community_id ORDER BY created_at DESC LIMIT 1) AS latest_link_url
                FROM communities c
                LEFT JOIN community_members cm ON cm.community_id = c.community_id
                LEFT JOIN users u ON u.user_id = c.created_by
                WHERE c.community_id = @cid
                GROUP BY c.community_id, c.name, c.description, c.niche, c.created_by,
                         c.banner_url, u.username, u.avatar_url", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.Parameters.AddWithValue("cid", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return NotFound(new { message = "Community not found." });
            return Ok(new
            {
                communityId   = reader.GetInt32(reader.GetOrdinal("community_id")),
                name          = reader["name"]?.ToString(),
                description   = reader["description"]?.ToString(),
                niche         = reader["niche"]?.ToString(),
                bannerUrl     = reader["banner_url"] == DBNull.Value ? null : reader["banner_url"].ToString(),
                memberCount   = Convert.ToInt32(reader["member_count"]),
                isMember      = Convert.ToBoolean(reader["is_member"]),
                isCreator     = Convert.ToBoolean(reader["is_creator"]),
                creatorId     = reader.GetInt32(reader.GetOrdinal("created_by")),
                creatorName   = reader["creator_name"]?.ToString(),
                creatorAvatar = reader["creator_avatar"]?.ToString(),
                latestLinkUrl = reader["latest_link_url"] == DBNull.Value ? null : reader["latest_link_url"].ToString()
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
            using var tx = conn.BeginTransaction();
            try
            {
                using var insertCmd = new NpgsqlCommand(@"
                    INSERT INTO communities (name, description, niche, created_by)
                    VALUES (@name, @desc, @niche, @createdBy)
                    RETURNING community_id", conn, tx);
                insertCmd.Parameters.AddWithValue("name",      dto.Name);
                insertCmd.Parameters.AddWithValue("desc",      dto.Description);
                insertCmd.Parameters.AddWithValue("niche",     dto.Niche);
                insertCmd.Parameters.AddWithValue("createdBy", userId);
                var newId = Convert.ToInt32(insertCmd.ExecuteScalar());

                using var joinCmd = new NpgsqlCommand(@"
                    INSERT INTO community_members (user_id, community_id) VALUES (@uid, @cid)", conn, tx);
                joinCmd.Parameters.AddWithValue("uid", userId);
                joinCmd.Parameters.AddWithValue("cid", newId);
                joinCmd.ExecuteNonQuery();

                tx.Commit();
                return CreatedAtAction(nameof(GetById), new { id = newId },
                    new { message = "Community created successfully", communityId = newId });
            }
            catch (Exception) { tx.Rollback(); return StatusCode(500, new { error = "Failed to create community." }); }
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

            using (var check = new NpgsqlCommand(
                "SELECT COUNT(*) FROM communities WHERE community_id = @cid AND created_by = @uid", conn))
            {
                check.Parameters.AddWithValue("cid", id);
                check.Parameters.AddWithValue("uid", userId);
                if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Only the creator can edit this community." });
            }

            using var cmd = new NpgsqlCommand(@"
                UPDATE communities SET name = @name, description = @desc, niche = @niche
                WHERE community_id = @cid", conn);
            cmd.Parameters.AddWithValue("name", dto.Name);
            cmd.Parameters.AddWithValue("desc", dto.Description);
            cmd.Parameters.AddWithValue("niche", dto.Niche);
            cmd.Parameters.AddWithValue("cid", id);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Community updated successfully" });
        }

        [HttpPost("{id:int}/join")]
        public IActionResult Join(int id)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO community_members (user_id, community_id)
                SELECT @uid, @cid WHERE EXISTS (SELECT 1 FROM communities WHERE community_id = @cid)
                  AND NOT EXISTS (SELECT 1 FROM community_members WHERE user_id = @uid AND community_id = @cid)", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.Parameters.AddWithValue("cid", id);
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

            using (var checkCmd = new NpgsqlCommand(
                "SELECT created_by FROM communities WHERE community_id = @cid", conn))
            {
                checkCmd.Parameters.AddWithValue("cid", id);
                var createdBy = checkCmd.ExecuteScalar();
                if (createdBy != null && Convert.ToInt32(createdBy) == userId)
                    return BadRequest(new { message = "Community creator cannot leave. Delete the community instead." });
            }

            using var cmd = new NpgsqlCommand(
                "DELETE FROM community_members WHERE user_id = @uid AND community_id = @cid", conn);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.Parameters.AddWithValue("cid", id);
            if (cmd.ExecuteNonQuery() == 0) return NotFound(new { message = "Membership not found." });
            return Ok(new { message = "Left successfully" });
        }

        [HttpPost("{id:int}/banner")]
        public async Task<IActionResult> UploadBanner(int id, IFormFile file)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            if (file == null || file.Length == 0) return BadRequest(new { message = "No file uploaded." });
            if (file.Length > 5 * 1024 * 1024) return BadRequest(new { message = "Banner must be under 5 MB." });
            if (!AllowedImageTypes.Contains(file.ContentType.ToLower())) return BadRequest(new { message = "Only JPEG, PNG and WebP images are allowed." });
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!AllowedImageExts.Contains(ext)) return BadRequest(new { message = "Invalid file extension." });

            using var conn = _db.GetConnection();
            conn.Open();

            using (var check = new NpgsqlCommand("SELECT COUNT(*) FROM communities WHERE community_id = @cid AND created_by = @uid", conn))
            {
                check.Parameters.AddWithValue("cid", id);
                check.Parameters.AddWithValue("uid", userId);
                if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Only the community creator can upload a banner." });
            }

            using (var oldCmd = new NpgsqlCommand("SELECT banner_url FROM communities WHERE community_id = @cid", conn))
            {
                oldCmd.Parameters.AddWithValue("cid", id);
                var oldUrl = oldCmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(oldUrl))
                {
                    var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", oldUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }
            }

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "banners");
            Directory.CreateDirectory(uploadsPath);
            var safeFileName = $"banner_{id}_{DateTime.UtcNow.Ticks}{ext}";
            var filePath = Path.Combine(uploadsPath, safeFileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);
            var publicUrl = $"/uploads/banners/{safeFileName}";

            using var cmd = new NpgsqlCommand("UPDATE communities SET banner_url = @url WHERE community_id = @cid", conn);
            cmd.Parameters.AddWithValue("url", publicUrl);
            cmd.Parameters.AddWithValue("cid", id);
            cmd.ExecuteNonQuery();
            return Ok(new { bannerUrl = publicUrl });
        }

        [HttpDelete("{id:int}")]
        public IActionResult Delete(int id)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();

            using var conn = _db.GetConnection();
            conn.Open();

            // Only the creator can delete their own community
            using (var check = new NpgsqlCommand(
                "SELECT COUNT(*) FROM communities WHERE community_id = @cid AND created_by = @uid", conn))
            {
                check.Parameters.AddWithValue("cid", id);
                check.Parameters.AddWithValue("uid", userId);
                if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                    return StatusCode(403, new { message = "Only the community creator can delete this community." });
            }

            using var tx = conn.BeginTransaction();
            try
            {
                // Delete all links and their dependents first
                var linkIds = new List<int>();
                using (var linkCmd = new NpgsqlCommand("SELECT link_id FROM links WHERE community_id = @cid", conn, tx))
                {
                    linkCmd.Parameters.AddWithValue("cid", id);
                    using var lr = linkCmd.ExecuteReader();
                    while (lr.Read()) linkIds.Add(Convert.ToInt32(lr["link_id"]));
                }
                foreach (var lid in linkIds)
                {
                    void ExecLink(string sql) { using var c = new NpgsqlCommand(sql, conn, tx); c.Parameters.AddWithValue("lid", lid); c.ExecuteNonQuery(); }
                    ExecLink("DELETE FROM link_comments WHERE link_id = @lid");
                    ExecLink("DELETE FROM link_likes    WHERE link_id = @lid");
                    ExecLink("DELETE FROM link_clicks   WHERE link_id = @lid");
                    ExecLink("DELETE FROM links         WHERE link_id = @lid");
                }

                using (var dm = new NpgsqlCommand("DELETE FROM community_members WHERE community_id = @cid", conn, tx))
                { dm.Parameters.AddWithValue("cid", id); dm.ExecuteNonQuery(); }

                using (var dc = new NpgsqlCommand("DELETE FROM communities WHERE community_id = @cid", conn, tx))
                { dc.Parameters.AddWithValue("cid", id); dc.ExecuteNonQuery(); }

                tx.Commit();
                return Ok(new { message = "Community deleted successfully" });
            }
            catch { tx.Rollback(); return StatusCode(500, new { message = "Failed to delete community." }); }
        }

        [HttpDelete("{id:int}/banner")]
        public IActionResult RemoveBanner(int id)        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            using var conn = _db.GetConnection();
            conn.Open();

            using var check = new NpgsqlCommand(
                "SELECT banner_url FROM communities WHERE community_id = @cid AND created_by = @uid", conn);
            check.Parameters.AddWithValue("cid", id);
            check.Parameters.AddWithValue("uid", userId);
            var oldUrl = check.ExecuteScalar()?.ToString();
            if (oldUrl == null) return StatusCode(403, new { message = "Community not found or you are not the creator." });

            if (!string.IsNullOrEmpty(oldUrl))
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", oldUrl.TrimStart('/'));
                if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
            }

            using var cmd = new NpgsqlCommand("UPDATE communities SET banner_url = NULL WHERE community_id = @cid", conn);
            cmd.Parameters.AddWithValue("cid", id);
            cmd.ExecuteNonQuery();
            return Ok(new { message = "Banner removed" });
        }
    }
}
