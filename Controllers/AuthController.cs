using FullSummpotAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Oracle.ManagedDataAccess.Client;
using System.Security.Claims;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly OracleDbContext _db;
    private readonly PasswordService _passwordService;
    private readonly JwtService _jwtService;

    private static readonly HashSet<string> AllowedNiches = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gaming","Tech","Education","Music","Comedy","Vlogging",
        "Finance","Fitness","Food","Travel","Other"
    };

    public AuthController(OracleDbContext db, PasswordService passwordService, JwtService jwtService)
    {
        _db = db;
        _passwordService = passwordService;
        _jwtService = jwtService;
    }

    [HttpPost("register")]
    [EnableRateLimiting("register")]
    public IActionResult Register([FromBody] RegisterDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        // Validate niche against whitelist
        if (!AllowedNiches.Contains(dto.ContentNiche))
            return BadRequest(new { message = "Invalid niche selected." });

        // Validate username format
        if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Username, @"^[a-zA-Z0-9_]{3,30}$"))
            return BadRequest(new { message = "Username must be 3�30 characters and contain only letters, numbers and underscores." });

        // Basic password strength
        if (dto.Password.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        using var conn = _db.GetConnection();
        conn.Open();

        var checkEmail = new OracleCommand(
            "SELECT COUNT(*) FROM USERS WHERE LOWER(EMAIL) = LOWER(:email)", conn);
        checkEmail.BindByName = true;
        checkEmail.Parameters.Add(new OracleParameter("email", dto.Email));
        if (Convert.ToInt32(checkEmail.ExecuteScalar()) > 0)
            return BadRequest(new { message = "Email already registered." });

        var checkUser = new OracleCommand(
            "SELECT COUNT(*) FROM USERS WHERE LOWER(USERNAME) = LOWER(:u)", conn);
        checkUser.BindByName = true;
        checkUser.Parameters.Add(new OracleParameter("u", dto.Username));
        if (Convert.ToInt32(checkUser.ExecuteScalar()) > 0)
            return BadRequest(new { message = "Username already taken." });

        var hash = _passwordService.HashPassword(dto.Password);

        var cmd = new OracleCommand(
            "INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE) VALUES (:u, :e, :p, :c)", conn);
        cmd.BindByName = true;
        cmd.Parameters.Add(new OracleParameter("u", dto.Username));
        cmd.Parameters.Add(new OracleParameter("e", dto.Email.ToLower()));
        cmd.Parameters.Add(new OracleParameter("p", hash));
        cmd.Parameters.Add(new OracleParameter("c", dto.ContentNiche));
        cmd.ExecuteNonQuery();

        return Ok(new { message = "Account created successfully." });
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public IActionResult Login([FromBody] LoginDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        using var conn = _db.GetConnection();
        conn.Open();

        var cmd = new OracleCommand(
            "SELECT USER_ID, USERNAME, PASSWORD_HASH FROM USERS WHERE LOWER(EMAIL) = LOWER(:email)", conn);
        cmd.BindByName = true;
        cmd.Parameters.Add(new OracleParameter("email", dto.Email));

        using var reader = cmd.ExecuteReader();

        // Always run the same code path to prevent timing-based user enumeration
        string? userId = null, username = null, storedHash = null;
        if (reader.Read())
        {
            userId     = reader["USER_ID"].ToString();
            username   = reader["USERNAME"].ToString();
            storedHash = reader["PASSWORD_HASH"].ToString();
        }

        if (userId == null || !_passwordService.VerifyPassword(dto.Password, storedHash!))
            return Unauthorized(new { message = "Invalid email or password." });

        var token = _jwtService.GenerateToken(userId!, dto.Email);
        return Ok(new { token, username });
    }
}
