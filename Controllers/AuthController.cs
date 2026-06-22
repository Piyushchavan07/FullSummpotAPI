using FullSummpotAPI.Data;
using FullSummpotAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly NpgsqlDbContext _db;
    private readonly PasswordService _passwordService;
    private readonly JwtService _jwtService;
    private readonly EmailService _emailService;
    private readonly SmsService _smsService;
    private readonly OtpService _otp;
    private readonly AuthEventService _authEvents;
    private readonly IWebHostEnvironment _env;
    private readonly bool _autoVerify;

    private static readonly HashSet<string> AllowedNiches = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gaming","Tech","Education","Music","Comedy","Vlogging",
        "Finance","Fitness","Food","Travel","Other"
    };

    public AuthController(NpgsqlDbContext db, PasswordService passwordService,
                          JwtService jwtService, EmailService emailService,
                          SmsService smsService, OtpService otp,
                          AuthEventService authEvents, IWebHostEnvironment env,
                          IConfiguration config)
    {
        _db              = db;
        _passwordService = passwordService;
        _jwtService      = jwtService;
        _emailService    = emailService;
        _smsService      = smsService;
        _otp             = otp;
        _authEvents      = authEvents;
        _env             = env;
        _autoVerify      = config.GetValue<bool>("DevSettings:AutoVerifyOnRegister");
    }

    // POST /api/Auth/register
    [HttpPost("register")]
    [EnableRateLimiting("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { message = string.Join(" ", ModelState.Values
                .SelectMany(v => v.Errors).Select(e => e.ErrorMessage)) });

        if (!AllowedNiches.Contains(dto.ContentNiche))
            return BadRequest(new { message = "Invalid niche selected." });

        if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Username, @"^[a-zA-Z0-9_]{3,30}$"))
            return BadRequest(new { message = "Username must be 3-30 characters: letters, numbers, underscores only." });

        if (dto.Password.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        using var conn = _db.GetConnection();
        conn.Open();

        var email = dto.Email.Trim().ToLower();

        using (var checkEmail = new NpgsqlCommand(
            "SELECT COUNT(*) FROM users WHERE LOWER(email) = @email", conn))
        {
            checkEmail.Parameters.AddWithValue("email", email);
            if (Convert.ToInt32(checkEmail.ExecuteScalar()) > 0)
                return BadRequest(new { message = "Email already registered." });
        }

        using (var checkUser = new NpgsqlCommand(
            "SELECT COUNT(*) FROM users WHERE LOWER(username) = LOWER(@u)", conn))
        {
            checkUser.Parameters.AddWithValue("u", dto.Username);
            if (Convert.ToInt32(checkUser.ExecuteScalar()) > 0)
                return BadRequest(new { message = "Username already taken." });
        }

        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out normalizedPhone))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

            using var checkPhone = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM users
                WHERE REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@ph, '91' || @ph)", conn);
            checkPhone.Parameters.AddWithValue("ph", normalizedPhone);
            if (Convert.ToInt32(checkPhone.ExecuteScalar()) > 0)
                return BadRequest(new { message = "That phone number is already linked to another account." });
        }

        var hash       = _passwordService.HashPassword(dto.Password);
        var isVerified = _autoVerify;

        var insertSql = normalizedPhone == null
            ? @"INSERT INTO users (username, email, password_hash, content_niche,
                                   is_verified, is_email_verified, is_phone_verified, primary_contact_type)
               VALUES (@u, @e, @p, @c, @v, @v, FALSE, 'EMAIL')"
            : @"INSERT INTO users (username, email, password_hash, content_niche,
                                   is_verified, is_email_verified, is_phone_verified,
                                   primary_contact_type, phone_number)
               VALUES (@u, @e, @p, @c, @v, @v, FALSE, 'EMAIL', @ph)";

        using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("u", dto.Username);
        insertCmd.Parameters.AddWithValue("e", email);
        insertCmd.Parameters.AddWithValue("p", hash);
        insertCmd.Parameters.AddWithValue("c", dto.ContentNiche);
        insertCmd.Parameters.AddWithValue("v", isVerified);
        if (normalizedPhone != null)
            insertCmd.Parameters.AddWithValue("ph", normalizedPhone);
        insertCmd.ExecuteNonQuery();

        try
        {
            using var seedEmail = new NpgsqlCommand(@"
                INSERT INTO user_emails (user_id, email, is_primary, is_verified)
                SELECT user_id, @email, TRUE, @v FROM users WHERE LOWER(email) = @email2
                ON CONFLICT DO NOTHING", conn);
            seedEmail.Parameters.AddWithValue("email", email);
            seedEmail.Parameters.AddWithValue("v", isVerified);
            seedEmail.Parameters.AddWithValue("email2", email);
            seedEmail.ExecuteNonQuery();
        }
        catch { }

        if (_autoVerify)
        {
            using var userCmd = new NpgsqlCommand(
                "SELECT user_id, role FROM users WHERE LOWER(email) = @email", conn);
            userCmd.Parameters.AddWithValue("email", email);
            using var reader = userCmd.ExecuteReader();
            reader.Read();
            var userId = reader["user_id"].ToString()!;
            var role   = reader["role"]?.ToString() ?? "USER";
            var token  = _jwtService.GenerateToken(userId, email, role);
            return Ok(new { message = "Account created and verified (dev mode).", token, username = dto.Username, role });
        }

        try
        {
            var otp = OtpService.Generate();
            await _otp.StoreEmailOtpAsync(conn, email, otp, "VERIFY_EMAIL");
            await _emailService.SendOtpEmailAsync(dto.Email, otp, "VERIFY_EMAIL");
            _authEvents.Log(conn, null, "REGISTER", email);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Failed to send verification email: {ex.Message}" });
        }

        return Ok(new
        {
            message               = "Account created. Check your email for the verification code.",
            maskedContact         = ContactMaskHelper.MaskEmail(email),
            channel               = "email",
            resendCooldownSeconds = OtpService.ResendCooldownSeconds,
            hasPendingPhone       = normalizedPhone != null
        });
    }

    // POST /api/Auth/resend-verification
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationDto dto)
    {
        using var conn = _db.GetConnection();
        conn.Open();

        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            var email = dto.Email.Trim().ToLower();
            using var checkCmd = new NpgsqlCommand(
                "SELECT is_email_verified FROM users WHERE LOWER(email) = @email", conn);
            checkCmd.Parameters.AddWithValue("email", email);
            var res = checkCmd.ExecuteScalar();
            if (res == null || res == DBNull.Value)
                return BadRequest(new { message = "Email is not registered." });
            if (Convert.ToBoolean(res))
                return BadRequest(new { message = "Email is already verified. Please sign in." });

            var cooldown = _otp.GetResendCooldownSeconds(conn, "email", email, "VERIFY_EMAIL");
            if (cooldown > 0)
                return StatusCode(429, new { message = $"Wait {cooldown}s before resending.", resendCooldownSeconds = cooldown });

            var otp = OtpService.Generate();
            await _otp.StoreEmailOtpAsync(conn, email, otp, "VERIFY_EMAIL");
            await _emailService.SendOtpEmailAsync(dto.Email!, otp, "VERIFY_EMAIL");

            var payload = new { message = "Verification code resent.", maskedContact = ContactMaskHelper.MaskEmail(email), resendCooldownSeconds = OtpService.ResendCooldownSeconds };
            if (_env.IsDevelopment()) return Ok(new { payload.message, payload.maskedContact, payload.resendCooldownSeconds, devOtp = otp });
            return Ok(payload);
        }

        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit phone number." });

            using var checkCmd = new NpgsqlCommand(@"
                SELECT is_phone_verified FROM users
                WHERE REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@ph, '91' || @ph)", conn);
            checkCmd.Parameters.AddWithValue("ph", phone);
            var res = checkCmd.ExecuteScalar();
            if (res == null || res == DBNull.Value)
                return BadRequest(new { message = "Phone number is not registered." });
            if (Convert.ToBoolean(res))
                return BadRequest(new { message = "Phone is already verified." });

            var cooldown = _otp.GetResendCooldownSeconds(conn, "phone", phone, "VERIFY_PHONE");
            if (cooldown > 0)
                return StatusCode(429, new { message = $"Wait {cooldown}s before resending.", resendCooldownSeconds = cooldown });

            var otp = OtpService.Generate();
            await _otp.StorePhoneOtpAsync(conn, phone, otp, "VERIFY_PHONE");
            var devOtp = await _smsService.SendOtpSmsAsync(phone, otp, "VERIFY_PHONE");

            var payload = new { message = "SMS code resent.", maskedContact = ContactMaskHelper.MaskPhone(phone), resendCooldownSeconds = OtpService.ResendCooldownSeconds };
            if (_env.IsDevelopment() && devOtp != null)
                return Ok(new { payload.message, payload.maskedContact, payload.resendCooldownSeconds, devOtp });
            return Ok(payload);
        }

        return BadRequest(new { message = "Provide an email or phone number." });
    }

    // POST /api/Auth/verify-email
    [HttpPost("verify-email")]
    public IActionResult VerifyEmail([FromBody] VerifyEmailDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var email = dto.Email.Trim().ToLower();

        using var conn = _db.GetConnection();
        conn.Open();

        if (!_otp.ValidateEmailOtp(conn, email, dto.Otp, "VERIFY_EMAIL"))
            return BadRequest(new { message = "Invalid or expired verification code." });

        using (var updateCmd = new NpgsqlCommand(@"
            UPDATE users SET is_email_verified = TRUE, is_verified = TRUE
            WHERE LOWER(email) = @email", conn))
        {
            updateCmd.Parameters.AddWithValue("email", email);
            updateCmd.ExecuteNonQuery();
        }

        try
        {
            using var ueCmd = new NpgsqlCommand(
                "UPDATE user_emails SET is_verified = TRUE WHERE LOWER(email) = @email AND is_primary = TRUE", conn);
            ueCmd.Parameters.AddWithValue("email", email);
            ueCmd.ExecuteNonQuery();
        }
        catch { }

        using var userCmd = new NpgsqlCommand(
            "SELECT user_id, username, role FROM users WHERE LOWER(email) = @email", conn);
        userCmd.Parameters.AddWithValue("email", email);
        using var reader = userCmd.ExecuteReader();
        if (!reader.Read()) return BadRequest(new { message = "User not found." });

        var userId   = reader["user_id"].ToString()!;
        var username = reader["username"].ToString()!;
        var role     = reader["role"]?.ToString() ?? "USER";
        reader.Close();
        _authEvents.Log(conn, Convert.ToInt32(userId), "EMAIL_VERIFIED", email);

        var token = _jwtService.GenerateToken(userId, email, role);
        return Ok(new { message = "Email verified successfully.", token, username, role });
    }

    // POST /api/Auth/login
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public IActionResult Login([FromBody] LoginDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { message = string.Join(" ", ModelState.Values
                .SelectMany(v => v.Errors).Select(e => e.ErrorMessage)) });

        using var conn = _db.GetConnection();
        conn.Open();

        var contact = dto.Email.Trim();
        bool isEmailLogin = contact.Contains('@');
        string? normalizedPhone = null;
        if (!isEmailLogin && !PhoneNumberHelper.TryNormalizeIndianMobile(contact, out normalizedPhone))
            return BadRequest(new { message = "Enter a valid email or 10-digit phone number." });

        NpgsqlCommand cmd;
        if (isEmailLogin)
        {
            cmd = new NpgsqlCommand(@"
                SELECT u.user_id, u.username, u.password_hash, u.role, u.is_verified,
                       u.is_email_verified, u.email, u.phone_number, u.is_phone_verified
                FROM users u
                WHERE LOWER(u.email) = LOWER(@contact)
                   OR u.user_id IN (
                       SELECT ue.user_id FROM user_emails ue
                       WHERE LOWER(ue.email) = LOWER(@contact2) AND ue.is_verified = TRUE
                   )", conn);
            cmd.Parameters.AddWithValue("contact", contact);
            cmd.Parameters.AddWithValue("contact2", contact.ToLower());
        }
        else
        {
            cmd = new NpgsqlCommand(@"
                SELECT user_id, username, password_hash, role, is_verified,
                       is_email_verified, email, phone_number, is_phone_verified
                FROM users
                WHERE is_phone_verified = TRUE
                  AND REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@ph, '91' || @ph)", conn);
            cmd.Parameters.AddWithValue("ph", normalizedPhone!);
        }

        string? userId = null, username = null, storedHash = null, role = null;
        string? primaryEmail = null, phoneNumber = null;
        bool isVerified = false, emailVerified = false;

        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                userId        = reader["user_id"].ToString();
                username      = reader["username"].ToString();
                storedHash    = reader["password_hash"].ToString();
                role          = reader["role"]?.ToString() ?? "USER";
                isVerified    = Convert.ToBoolean(reader["is_verified"]);
                emailVerified = Convert.ToBoolean(reader["is_email_verified"]);
                primaryEmail  = reader["email"].ToString();
                phoneNumber   = reader["phone_number"] == DBNull.Value ? null : reader["phone_number"]?.ToString();
            }
        }
        cmd.Dispose();

        if (userId == null)
        {
            _authEvents.Log(conn, null, "LOGIN_FAILED_NO_USER", contact);
            return Unauthorized(new { message = "Account does not exist." });
        }

        if (!_passwordService.VerifyPassword(dto.Password, storedHash!))
        {
            _authEvents.Log(conn, Convert.ToInt32(userId), "LOGIN_FAILED_PASSWORD", contact);
            return Unauthorized(new { message = "Incorrect password." });
        }

        if (!isVerified || !emailVerified)
        {
            if (_env.IsDevelopment())
            {
                using var autoVerify = new NpgsqlCommand(
                    "UPDATE users SET is_verified = TRUE, is_email_verified = TRUE WHERE user_id = @id", conn);
                autoVerify.Parameters.AddWithValue("id", Convert.ToInt32(userId));
                autoVerify.ExecuteNonQuery();
            }
            else
            {
                return Unauthorized(new
                {
                    message           = "Please verify your email before logging in.",
                    needsVerification = true,
                    channel           = "email",
                    maskedContact     = ContactMaskHelper.MaskEmail(primaryEmail!),
                    email             = primaryEmail,
                    phoneNumber
                });
            }
        }

        _authEvents.Log(conn, Convert.ToInt32(userId), "LOGIN_SUCCESS", contact);
        var token = _jwtService.GenerateToken(userId!, primaryEmail!, role!);
        return Ok(new { token, username, role });
    }

    // POST /api/Auth/send-phone-otp
    [HttpPost("send-phone-otp")]
    [EnableRateLimiting("register")]
    public async Task<IActionResult> SendPhoneOtp([FromBody] PhoneOtpDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
            return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

        using var conn = _db.GetConnection();
        conn.Open();

        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            using var matchCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM users
                WHERE LOWER(email) = LOWER(@email)
                  AND REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@phone, '91' || @phone)", conn);
            matchCmd.Parameters.AddWithValue("email", dto.Email.Trim());
            matchCmd.Parameters.AddWithValue("phone", phone);
            if (Convert.ToInt32(matchCmd.ExecuteScalar()) == 0)
                return BadRequest(new { message = "Phone number does not match this account." });
        }
        else
        {
            using var existsCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM users
                WHERE REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@phone, '91' || @phone)", conn);
            existsCmd.Parameters.AddWithValue("phone", phone);
            if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0)
                return BadRequest(new { message = "No account found with that phone number." });
        }

        var cooldown = _otp.GetResendCooldownSeconds(conn, "phone", phone, "VERIFY_PHONE");
        if (cooldown > 0)
            return StatusCode(429, new { message = $"Wait {cooldown}s before resending.", resendCooldownSeconds = cooldown });

        try
        {
            var otp    = OtpService.Generate();
            await _otp.StorePhoneOtpAsync(conn, phone, otp, "VERIFY_PHONE");
            var devOtp = await _smsService.SendOtpSmsAsync(phone, otp, "VERIFY_PHONE");

            var payload = new { message = "SMS verification code sent.", maskedContact = ContactMaskHelper.MaskPhone(phone), resendCooldownSeconds = OtpService.ResendCooldownSeconds };
            if (_env.IsDevelopment() && devOtp != null)
                return Ok(new { payload.message, payload.maskedContact, payload.resendCooldownSeconds, devOtp });
            return Ok(payload);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Failed to send SMS code: {ex.Message}" });
        }
    }

    // POST /api/Auth/verify-phone
    [HttpPost("verify-phone")]
    public IActionResult VerifyPhone([FromBody] VerifyPhoneDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
            return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

        using var conn = _db.GetConnection();
        conn.Open();

        if (!_otp.ValidatePhoneOtp(conn, phone, dto.Otp, "VERIFY_PHONE"))
            return BadRequest(new { message = "Invalid or expired phone verification code." });

        using (var updateCmd = new NpgsqlCommand(@"
            UPDATE users SET is_phone_verified = TRUE
            WHERE REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@phone, '91' || @phone)", conn))
        {
            updateCmd.Parameters.AddWithValue("phone", phone);
            updateCmd.ExecuteNonQuery();
        }

        using var userCmd = new NpgsqlCommand(@"
            SELECT user_id, username, email, role, is_email_verified, is_verified
            FROM users
            WHERE REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@phone, '91' || @phone)", conn);
        userCmd.Parameters.AddWithValue("phone", phone);
        using var reader = userCmd.ExecuteReader();
        if (!reader.Read()) return BadRequest(new { message = "No account found with that phone number." });

        var userId   = reader["user_id"].ToString()!;
        var username = reader["username"].ToString()!;
        var email    = reader["email"].ToString()!;
        var role     = reader["role"]?.ToString() ?? "USER";
        bool emailOk = Convert.ToBoolean(reader["is_email_verified"]);
        reader.Close();

        _authEvents.Log(conn, Convert.ToInt32(userId), "PHONE_VERIFIED", phone);

        if (!emailOk)
            return Ok(new { message = "Phone verified. Please verify your email to activate your account." });

        var token = _jwtService.GenerateToken(userId, email, role);
        return Ok(new { message = "Phone verified successfully.", token, username, role });
    }

    // POST /api/Auth/forgot-password
    [HttpPost("forgot-password")]
    [EnableRateLimiting("register")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        using var conn = _db.GetConnection();
        conn.Open();
        bool isEmail = dto.Contact.Contains('@');

        if (isEmail)
        {
            var email = dto.Contact.Trim().ToLower();
            using var checkCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM users WHERE LOWER(email) = @email AND is_email_verified = TRUE", conn);
            checkCmd.Parameters.AddWithValue("email", email);
            bool exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            if (exists)
            {
                try
                {
                    var otp = OtpService.Generate();
                    await _otp.StoreEmailOtpAsync(conn, email, otp, "RESET_PASSWORD");
                    await _emailService.SendOtpEmailAsync(dto.Contact, otp, "RESET_PASSWORD");
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { message = $"Failed to send reset email: {ex.Message}" });
                }
            }
        }
        else
        {
            using var checkCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM users WHERE phone_number = @phone AND is_verified = TRUE", conn);
            checkCmd.Parameters.AddWithValue("phone", dto.Contact.Trim());
            bool exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            if (exists)
            {
                string? devOtp = null;
                try
                {
                    var otp = OtpService.Generate();
                    await _otp.StorePhoneOtpAsync(conn, dto.Contact.Trim(), otp, "RESET_PASSWORD");
                    devOtp = await _smsService.SendOtpSmsAsync(dto.Contact.Trim(), otp, "RESET_PASSWORD");
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { message = $"Failed to send reset SMS: {ex.Message}" });
                }

                if (devOtp != null)
                    return Ok(new { message = "If that contact is registered, a reset code has been sent (dev mode).", devOtp });
            }
        }

        return Ok(new { message = "If that contact is registered, a reset code has been sent." });
    }

    // POST /api/Auth/reset-password
    [HttpPost("reset-password")]
    public IActionResult ResetPassword([FromBody] ResetPasswordDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        using var conn = _db.GetConnection();
        conn.Open();

        bool isEmail  = dto.Contact.Contains('@');
        bool otpValid = isEmail
            ? _otp.ValidateEmailOtp(conn, dto.Contact.ToLower(), dto.Otp, "RESET_PASSWORD")
            : _otp.ValidatePhoneOtp(conn, dto.Contact.Trim(), dto.Otp, "RESET_PASSWORD");

        if (!otpValid)
            return BadRequest(new { message = "Invalid or expired reset code." });

        var newHash = _passwordService.HashPassword(dto.NewPassword);

        NpgsqlCommand updateCmd;
        if (isEmail)
        {
            updateCmd = new NpgsqlCommand(
                "UPDATE users SET password_hash = @hash WHERE LOWER(email) = LOWER(@contact)", conn);
        }
        else
        {
            updateCmd = new NpgsqlCommand(
                "UPDATE users SET password_hash = @hash WHERE phone_number = @contact", conn);
        }
        updateCmd.Parameters.AddWithValue("hash", newHash);
        updateCmd.Parameters.AddWithValue("contact", isEmail ? dto.Contact : dto.Contact.Trim());
        updateCmd.ExecuteNonQuery();
        updateCmd.Dispose();

        return Ok(new { message = "Password reset successfully. You can now log in." });
    }
}
