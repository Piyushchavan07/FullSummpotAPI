using FullSummpotAPI.Data;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly OracleDbContext _db;
    private readonly PasswordService _passwordService;
    private readonly JwtService _jwtService;

    public AuthController(
        OracleDbContext db,
        PasswordService passwordService,
        JwtService jwtService)
    {
        _db = db;
        _passwordService = passwordService;
        _jwtService = jwtService;
    }

    [HttpPost("register")]
    public IActionResult Register(RegisterDto dto)
    {
        using var conn = _db.GetConnection();
        conn.Open();

        var checkCmd = new OracleCommand(
            "SELECT COUNT(*) FROM USERS WHERE EMAIL = :email",
            conn);
        checkCmd.Parameters.Add(new OracleParameter("email", dto.Email));

        var exists = Convert.ToInt32(checkCmd.ExecuteScalar());

        if (exists > 0)
            return BadRequest("Email already exists");

        var hash = _passwordService.HashPassword(dto.Password);

        var cmd = new OracleCommand(
            "INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE) VALUES (:u, :e, :p, :c)",
            conn);

        cmd.Parameters.Add(new OracleParameter("u", dto.Username));
        cmd.Parameters.Add(new OracleParameter("e", dto.Email));
        cmd.Parameters.Add(new OracleParameter("p", hash));
        cmd.Parameters.Add(new OracleParameter("c", dto.ContentNiche));

        cmd.ExecuteNonQuery();

        return Ok("User registered successfully");
    }

    [HttpPost("login")]
    public IActionResult Login(LoginDto dto)
    {
        using var conn = _db.GetConnection();
        conn.Open();

        var cmd = new OracleCommand(
            "SELECT USER_ID, PASSWORD_HASH FROM USERS WHERE EMAIL = :email",
            conn);

        cmd.Parameters.Add(new OracleParameter("email", dto.Email));

        using var reader = cmd.ExecuteReader();

        if (!reader.Read())
            return Unauthorized("Invalid credentials");

        var userId = reader["USER_ID"].ToString();
        var storedHash = reader["PASSWORD_HASH"].ToString();

        if (!_passwordService.VerifyPassword(dto.Password, storedHash!))
            return Unauthorized("Invalid credentials");

        var token = _jwtService.GenerateToken(userId!, dto.Email);

        return Ok(new { token });
    }
}