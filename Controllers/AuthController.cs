using FullSummpotAPI.Data;
using FullSummpotAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Oracle.ManagedDataAccess.Client;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly OracleDbContext _db;
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

    public AuthController(OracleDbContext db, PasswordService passwordService,
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

        var checkEmail = new OracleCommand(
            "SELECT COUNT(*) FROM USERS WHERE LOWER(EMAIL) = :email", conn);
        checkEmail.BindByName = true;
        checkEmail.Parameters.Add(new OracleParameter("email", email));
        if (Convert.ToInt32(checkEmail.ExecuteScalar()) > 0)
            return BadRequest(new { message = "Email already registered." });

        var checkUser = new OracleCommand(
            "SELECT COUNT(*) FROM USERS WHERE LOWER(USERNAME) = LOWER(:u)", conn);
        checkUser.BindByName = true;
        checkUser.Parameters.Add(new OracleParameter("u", dto.Username));
        if (Convert.ToInt32(checkUser.ExecuteScalar()) > 0)
            return BadRequest(new { message = "Username already taken." });

        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out normalizedPhone))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

            var checkPhone = new OracleCommand(@"
                SELECT COUNT(*) FROM USERS
                WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
            checkPhone.BindByName = true;
            checkPhone.Parameters.Add(new OracleParameter("ph", normalizedPhone));
            if (Convert.ToInt32(checkPhone.ExecuteScalar()) > 0)
                return BadRequest(new { message = "That phone number is already linked to another account." });
        }

        var hash = _passwordService.HashPassword(dto.Password);

        var isVerified = _autoVerify ? 1 : 0;

        var insertSql = normalizedPhone == null
            ? @"INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE,
                                   IS_VERIFIED, IS_EMAIL_VERIFIED, IS_PHONE_VERIFIED, PRIMARY_CONTACT_TYPE)
               VALUES (:u, :e, :p, :c, :v, :v, 0, 'EMAIL')"
            : @"INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE,
                                   IS_VERIFIED, IS_EMAIL_VERIFIED, IS_PHONE_VERIFIED,
                                   PRIMARY_CONTACT_TYPE, PHONE_NUMBER)
               VALUES (:u, :e, :p, :c, :v, :v, 0, 'EMAIL', :ph)";

        var insertCmd = new OracleCommand(insertSql, conn);
        insertCmd.BindByName = true;
        insertCmd.Parameters.Add(new OracleParameter("u", dto.Username));
        insertCmd.Parameters.Add(new OracleParameter("e", email));
        insertCmd.Parameters.Add(new OracleParameter("p", hash));
        insertCmd.Parameters.Add(new OracleParameter("c", dto.ContentNiche));
        insertCmd.Parameters.Add(new OracleParameter("v", isVerified));
        if (normalizedPhone != null)
            insertCmd.Parameters.Add(new OracleParameter("ph", normalizedPhone));
        insertCmd.ExecuteNonQuery();

        try
        {
            var seedEmail = new OracleCommand(@"
                INSERT INTO USER_EMAILS (USER_ID, EMAIL, IS_PRIMARY, IS_VERIFIED)
                SELECT USER_ID, :email, 1, :v FROM USERS WHERE LOWER(EMAIL) = :email2", conn);
            seedEmail.BindByName = true;
            seedEmail.Parameters.Add(new OracleParameter("email", email));
            seedEmail.Parameters.Add(new OracleParameter("v", isVerified));
            seedEmail.Parameters.Add(new OracleParameter("email2", email));
            seedEmail.ExecuteNonQuery();
        }
        catch { /* USER_EMAILS created by migration */ }

        // Auto-verify mode: skip OTP, return JWT immediately
        if (_autoVerify)
        {
            var userCmd = new OracleCommand(
                "SELECT USER_ID, ROLE FROM USERS WHERE LOWER(EMAIL) = :email", conn);
            userCmd.BindByName = true;
            userCmd.Parameters.Add(new OracleParameter("email", email));
            using var reader = userCmd.ExecuteReader();
            reader.Read();
            var userId = reader["USER_ID"].ToString()!;
            var role   = reader["ROLE"]?.ToString() ?? "USER";
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
            Console.WriteLine($"Error sending registration verification email: {ex}");
            return StatusCode(500, new { message = $"Failed to send verification email: {ex.Message}" });
        }

        return Ok(new
        {
            message = "Account created. Check your email for the verification code.",
            maskedContact = ContactMaskHelper.MaskEmail(email),
            channel = "email",
            resendCooldownSeconds = OtpService.ResendCooldownSeconds,
            hasPendingPhone = normalizedPhone != null
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
            var checkCmd = new OracleCommand(
                "SELECT IS_EMAIL_VERIFIED FROM USERS WHERE LOWER(EMAIL) = :email", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add(new OracleParameter("email", email));
            var res = checkCmd.ExecuteScalar();
            if (res == null || res == DBNull.Value)
                return BadRequest(new { message = "Email is not registered." });
            if (Convert.ToInt32(res) == 1)
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

            var checkCmd = new OracleCommand(@"
                SELECT IS_PHONE_VERIFIED FROM USERS
                WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add(new OracleParameter("ph", phone));
            var res = checkCmd.ExecuteScalar();
            if (res == null || res == DBNull.Value)
                return BadRequest(new { message = "Phone number is not registered." });
            if (Convert.ToInt32(res) == 1)
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

        var updateCmd = new OracleCommand(@"
            UPDATE USERS SET IS_EMAIL_VERIFIED = 1, IS_VERIFIED = 1
            WHERE LOWER(EMAIL) = :email", conn);
        updateCmd.BindByName = true;
        updateCmd.Parameters.Add(new OracleParameter("email", email));
        updateCmd.ExecuteNonQuery();

        try
        {
            var ueCmd = new OracleCommand(
                "UPDATE USER_EMAILS SET IS_VERIFIED = 1 WHERE LOWER(EMAIL) = :email AND IS_PRIMARY = 1", conn);
            ueCmd.BindByName = true;
            ueCmd.Parameters.Add(new OracleParameter("email", email));
            ueCmd.ExecuteNonQuery();
        }
        catch { }

        var userCmd = new OracleCommand(
            "SELECT USER_ID, USERNAME, ROLE FROM USERS WHERE LOWER(EMAIL) = :email", conn);
        userCmd.BindByName = true;
        userCmd.Parameters.Add(new OracleParameter("email", email));
        using var reader = userCmd.ExecuteReader();
        if (!reader.Read()) return BadRequest(new { message = "User not found." });

        var userId   = reader["USER_ID"].ToString()!;
        var username = reader["USERNAME"].ToString()!;
        var role     = reader["ROLE"]?.ToString() ?? "USER";
        _authEvents.Log(conn, Convert.ToInt32(userId), "EMAIL_VERIFIED", email);

        var token = _jwtService.GenerateToken(userId, email, role);
        return Ok(new { message = "Email verified successfully.", token, username, role });
    }

    // POST /api/Auth/login — email, secondary email, or verified phone + password
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

        OracleCommand cmd;
        if (isEmailLogin)
        {
            cmd = new OracleCommand(@"
                SELECT u.USER_ID, u.USERNAME, u.PASSWORD_HASH, u.ROLE, u.IS_VERIFIED,
                       u.IS_EMAIL_VERIFIED, u.EMAIL, u.PHONE_NUMBER, u.IS_PHONE_VERIFIED
                FROM USERS u
                WHERE LOWER(u.EMAIL) = LOWER(:contact)
                   OR u.USER_ID IN (
                       SELECT ue.USER_ID FROM USER_EMAILS ue
                       WHERE LOWER(ue.EMAIL) = LOWER(:contact2) AND ue.IS_VERIFIED = 1
                   )", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add(new OracleParameter("contact", contact));
            cmd.Parameters.Add(new OracleParameter("contact2", contact.ToLower()));
        }
        else
        {
            cmd = new OracleCommand(@"
                SELECT USER_ID, USERNAME, PASSWORD_HASH, ROLE, IS_VERIFIED,
                       IS_EMAIL_VERIFIED, EMAIL, PHONE_NUMBER, IS_PHONE_VERIFIED
                FROM USERS
                WHERE IS_PHONE_VERIFIED = 1
                  AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
            cmd.BindByName = true;
            cmd.Parameters.Add(new OracleParameter("ph", normalizedPhone!));
        }

        using var reader = cmd.ExecuteReader();

        string? userId = null, username = null, storedHash = null, role = null;
        string? primaryEmail = null, phoneNumber = null;
        bool isVerified = false, emailVerified = false, phoneVerified = false;

        if (reader.Read())
        {
            userId         = reader["USER_ID"].ToString();
            username       = reader["USERNAME"].ToString();
            storedHash     = reader["PASSWORD_HASH"].ToString();
            role           = reader["ROLE"]?.ToString() ?? "USER";
            isVerified     = Convert.ToInt32(reader["IS_VERIFIED"]) == 1;
            emailVerified  = Convert.ToInt32(reader["IS_EMAIL_VERIFIED"]) == 1;
            phoneVerified  = Convert.ToInt32(reader["IS_PHONE_VERIFIED"]) == 1;
            primaryEmail   = reader["EMAIL"].ToString();
            phoneNumber    = reader["PHONE_NUMBER"] == DBNull.Value ? null : reader["PHONE_NUMBER"]?.ToString();
        }

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
            // In development, auto-verify and let them in
            if (_env.IsDevelopment())
            {
                var autoVerify = new OracleCommand(
                    "UPDATE USERS SET IS_VERIFIED = 1, IS_EMAIL_VERIFIED = 1 WHERE USER_ID = :id", conn);
                autoVerify.BindByName = true;
                autoVerify.Parameters.Add(new OracleParameter("id", Convert.ToInt32(userId)));
                autoVerify.ExecuteNonQuery();
            }
            else
            {
                return Unauthorized(new
                {
                    message = "Please verify your email before logging in.",
                    needsVerification = true,
                    channel = "email",
                    maskedContact = ContactMaskHelper.MaskEmail(primaryEmail!),
                    email = primaryEmail,
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
            var matchCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM USERS
                WHERE LOWER(EMAIL) = LOWER(:email)
                  AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
            matchCmd.BindByName = true;
            matchCmd.Parameters.Add(new OracleParameter("email", dto.Email.Trim()));
            matchCmd.Parameters.Add(new OracleParameter("phone", phone));
            if (Convert.ToInt32(matchCmd.ExecuteScalar()) == 0)
                return BadRequest(new { message = "Phone number does not match this account." });
        }
        else
        {
            var existsCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM USERS
                WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
            existsCmd.BindByName = true;
            existsCmd.Parameters.Add(new OracleParameter("phone", phone));
            if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0)
                return BadRequest(new { message = "No account found with that phone number." });
        }

        var cooldown = _otp.GetResendCooldownSeconds(conn, "phone", phone, "VERIFY_PHONE");
        if (cooldown > 0)
            return StatusCode(429, new { message = $"Wait {cooldown}s before resending.", resendCooldownSeconds = cooldown });

        try
        {
            var otp = OtpService.Generate();
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

    // POST /api/Auth/verify-phone — backup phone verification after email signup
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

        var updateCmd = new OracleCommand(@"
            UPDATE USERS SET IS_PHONE_VERIFIED = 1
            WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
        updateCmd.BindByName = true;
        updateCmd.Parameters.Add(new OracleParameter("phone", phone));
        updateCmd.ExecuteNonQuery();

        var userCmd = new OracleCommand(@"
            SELECT USER_ID, USERNAME, EMAIL, ROLE, IS_EMAIL_VERIFIED, IS_VERIFIED
            FROM USERS
            WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
        userCmd.BindByName = true;
        userCmd.Parameters.Add(new OracleParameter("phone", phone));
        using var reader = userCmd.ExecuteReader();
        if (!reader.Read())
            return BadRequest(new { message = "No account found with that phone number." });

        var userId   = reader["USER_ID"].ToString()!;
        var username = reader["USERNAME"].ToString()!;
        var email    = reader["EMAIL"].ToString()!;
        var role     = reader["ROLE"]?.ToString() ?? "USER";
        bool emailOk = Convert.ToInt32(reader["IS_EMAIL_VERIFIED"]) == 1;

        _authEvents.Log(conn, Convert.ToInt32(userId), "PHONE_VERIFIED", phone);

        if (!emailOk)
            return Ok(new { message = "Phone verified. Please verify your email to activate your account." });

        var token = _jwtService.GenerateToken(userId, email, role);
        return Ok(new { message = "Phone verified successfully.", token, username, role });
    }

    // POST /api/Auth/verify-phone-firebase
    [HttpPost("verify-phone-firebase")]
    public async Task<IActionResult> VerifyPhoneFirebase([FromBody] VerifyPhoneFirebaseDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            var decodedToken = await FirebaseAdmin.Auth.FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(dto.IdToken);
            string firebasePhone = decodedToken.Claims.TryGetValue("phone_number", out var ph) ? ph.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(firebasePhone))
                return BadRequest(new { message = "Firebase token does not contain a phone number." });

            var phone = PhoneNumberHelper.NormalizeIndianMobile(firebasePhone);

            using var conn = _db.GetConnection();
            conn.Open();

            string? userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int? authenticatedUserId = !string.IsNullOrEmpty(userIdStr) ? Convert.ToInt32(userIdStr) : null;

            if (authenticatedUserId != null)
            {
                var takenCmd = new OracleCommand(@"
                    SELECT COUNT(*) FROM USERS
                    WHERE USER_ID != :me
                      AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
                takenCmd.BindByName = true;
                takenCmd.Parameters.Add("me", OracleDbType.Int32).Value = authenticatedUserId.Value;
                takenCmd.Parameters.Add("ph", phone);
                if (Convert.ToInt32(takenCmd.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "This number is already linked to another account." });

                var updateCmd = new OracleCommand(@"
                    UPDATE USERS SET PHONE_NUMBER = :ph, IS_PHONE_VERIFIED = 1
                    WHERE USER_ID = :id", conn);
                updateCmd.BindByName = true;
                updateCmd.Parameters.Add("ph", phone);
                updateCmd.Parameters.Add("id", OracleDbType.Int32).Value = authenticatedUserId.Value;
                updateCmd.ExecuteNonQuery();

                _authEvents.Log(conn, authenticatedUserId.Value, "PHONE_VERIFIED_FIREBASE", phone);

                return Ok(new { message = "Phone verified successfully." });
            }
            else
            {
                string? email = dto.Email?.Trim().ToLower();
                if (!string.IsNullOrEmpty(email))
                {
                    var checkEmailCmd = new OracleCommand("SELECT USER_ID FROM USERS WHERE LOWER(EMAIL) = :email", conn);
                    checkEmailCmd.BindByName = true;
                    checkEmailCmd.Parameters.Add(new OracleParameter("email", email));
                    var userIdObj = checkEmailCmd.ExecuteScalar();
                    if (userIdObj == null || userIdObj == DBNull.Value)
                        return BadRequest(new { message = "No account found with this email." });

                    int targetUserId = Convert.ToInt32(userIdObj);

                    var takenCmd = new OracleCommand(@"
                        SELECT COUNT(*) FROM USERS
                        WHERE USER_ID != :me
                          AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
                    takenCmd.BindByName = true;
                    takenCmd.Parameters.Add("me", OracleDbType.Int32).Value = targetUserId;
                    takenCmd.Parameters.Add("ph", phone);
                    if (Convert.ToInt32(takenCmd.ExecuteScalar()) > 0)
                        return BadRequest(new { message = "This number is already linked to another account." });

                    var updateCmd = new OracleCommand(@"
                        UPDATE USERS SET PHONE_NUMBER = :ph, IS_PHONE_VERIFIED = 1
                        WHERE USER_ID = :id", conn);
                    updateCmd.BindByName = true;
                    updateCmd.Parameters.Add("ph", phone);
                    updateCmd.Parameters.Add("id", OracleDbType.Int32).Value = targetUserId;
                    updateCmd.ExecuteNonQuery();

                    _authEvents.Log(conn, targetUserId, "PHONE_VERIFIED_FIREBASE", phone);

                    var detailsCmd = new OracleCommand(@"
                        SELECT USERNAME, ROLE, IS_EMAIL_VERIFIED FROM USERS WHERE USER_ID = :id", conn);
                    detailsCmd.BindByName = true;
                    detailsCmd.Parameters.Add("id", OracleDbType.Int32).Value = targetUserId;
                    using var reader = detailsCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var username = reader["USERNAME"].ToString()!;
                        var role = reader["ROLE"]?.ToString() ?? "USER";
                        bool emailOk = Convert.ToInt32(reader["IS_EMAIL_VERIFIED"]) == 1;

                        if (!emailOk)
                            return Ok(new { message = "Phone verified. Please verify your email to activate your account." });

                        var tokenStr = _jwtService.GenerateToken(targetUserId.ToString(), email, role);
                        return Ok(new { message = "Phone verified successfully.", token = tokenStr, username, role });
                    }
                }
                else
                {
                    var updateCmd = new OracleCommand(@"
                        UPDATE USERS SET IS_PHONE_VERIFIED = 1
                        WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
                    updateCmd.BindByName = true;
                    updateCmd.Parameters.Add(new OracleParameter("phone", phone));
                    int rows = updateCmd.ExecuteNonQuery();
                    if (rows == 0)
                        return BadRequest(new { message = "No account found with that phone number." });

                    var detailsCmd = new OracleCommand(@"
                        SELECT USER_ID, USERNAME, EMAIL, ROLE, IS_EMAIL_VERIFIED
                        FROM USERS
                        WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
                    detailsCmd.BindByName = true;
                    detailsCmd.Parameters.Add(new OracleParameter("phone", phone));
                    using var reader = detailsCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var userId = reader["USER_ID"].ToString()!;
                        var username = reader["USERNAME"].ToString()!;
                        var userEmail = reader["EMAIL"].ToString()!;
                        var role = reader["ROLE"]?.ToString() ?? "USER";
                        bool emailOk = Convert.ToInt32(reader["IS_EMAIL_VERIFIED"]) == 1;

                        _authEvents.Log(conn, Convert.ToInt32(userId), "PHONE_VERIFIED_FIREBASE", phone);

                        if (!emailOk)
                            return Ok(new { message = "Phone verified. Please verify your email to activate your account." });

                        var tokenStr = _jwtService.GenerateToken(userId, userEmail, role);
                        return Ok(new { message = "Phone verified successfully.", token = tokenStr, username, role });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Firebase token verification failed: {ex.Message}" });
        }

        return BadRequest(new { message = "Failed to verify phone number." });
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
            var checkCmd = new OracleCommand(
                "SELECT COUNT(*) FROM USERS WHERE LOWER(EMAIL) = :email AND IS_EMAIL_VERIFIED = 1", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add(new OracleParameter("email", email));
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
            {
                var cooldown = _otp.GetResendCooldownSeconds(conn, "email", email, "RESET_PASSWORD");
                if (cooldown == 0)
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
        }
        else
        {
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.Contact, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit phone number or use your registered email." });

            var checkCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM USERS
                WHERE IS_PHONE_VERIFIED = 1 AND PHONE_NUMBER IS NOT NULL
                  AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:phone, '91' || :phone)", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add(new OracleParameter("phone", phone));
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
            {
                var cooldown = _otp.GetResendCooldownSeconds(conn, "phone", phone, "RESET_PASSWORD");
                if (cooldown == 0)
                {
                    try
                    {
                        var otp = OtpService.Generate();
                        await _otp.StorePhoneOtpAsync(conn, phone, otp, "RESET_PASSWORD");
                        var devOtp = await _smsService.SendOtpSmsAsync(phone, otp, "RESET_PASSWORD");
                        if (_env.IsDevelopment() && devOtp != null)
                            return Ok(new { message = "If that contact is registered, a reset code has been sent.", devOtp });
                    }
                    catch (Exception ex)
                    {
                        return StatusCode(500, new { message = $"Failed to send reset SMS: {ex.Message}" });
                    }
                }
            }
        }

        return Ok(new { message = "If that contact is registered, a reset code has been sent." });
    }

    // POST /api/Auth/reset-password
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        using var conn = _db.GetConnection();
        conn.Open();

        bool isEmail = dto.Contact.Contains('@');
        bool otpValid;

        if (isEmail)
            otpValid = _otp.ValidateEmailOtp(conn, dto.Contact.ToLower(), dto.Otp, "RESET_PASSWORD");
        else
        {
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.Contact, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit phone number or use your registered email." });

            if (dto.Otp.Length > 20) // Firebase ID Token
            {
                try
                {
                    var decodedToken = await FirebaseAdmin.Auth.FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(dto.Otp);
                    string firebasePhone = decodedToken.Claims.TryGetValue("phone_number", out var ph) ? ph.ToString() ?? "" : "";
                    if (string.IsNullOrEmpty(firebasePhone))
                        return BadRequest(new { message = "Firebase token does not contain a phone number." });

                    var normalizedFirebase = PhoneNumberHelper.NormalizeIndianMobile(firebasePhone);
                    if (normalizedFirebase != phone)
                        return BadRequest(new { message = "Firebase phone number does not match the requested contact." });

                    otpValid = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = $"Firebase token verification failed: {ex.Message}" });
                }
            }
            else
            {
                otpValid = _otp.ValidatePhoneOtp(conn, phone, dto.Otp, "RESET_PASSWORD");
            }
        }

        if (!otpValid)
            return BadRequest(new { message = "Invalid or expired reset code." });

        var newHash = _passwordService.HashPassword(dto.NewPassword);

        OracleCommand updateCmd;
        if (isEmail)
        {
            updateCmd = new OracleCommand(
                "UPDATE USERS SET PASSWORD_HASH = :hash WHERE LOWER(EMAIL) = LOWER(:contact)", conn);
            updateCmd.BindByName = true;
            updateCmd.Parameters.Add(new OracleParameter("hash", newHash));
            updateCmd.Parameters.Add(new OracleParameter("contact", dto.Contact));
        }
        else
        {
            var phone = PhoneNumberHelper.NormalizeIndianMobile(dto.Contact);
            updateCmd = new OracleCommand(@"
                UPDATE USERS SET PASSWORD_HASH = :hash
                WHERE REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:contact, '91' || :contact)", conn);
            updateCmd.BindByName = true;
            updateCmd.Parameters.Add(new OracleParameter("hash", newHash));
            updateCmd.Parameters.Add(new OracleParameter("contact", phone));
        }
        updateCmd.ExecuteNonQuery();

        _authEvents.Log(conn, null, "PASSWORD_RESET", dto.Contact);
        return Ok(new { message = "Password reset successfully. You can now log in." });
    }
}
