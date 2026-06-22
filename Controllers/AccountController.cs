using FullSummpotAPI.Data;
using FullSummpotAPI.DTOs;
using FullSummpotAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AccountController : ControllerBase
    {
        private readonly NpgsqlDbContext _db;
        private readonly OtpService _otp;
        private readonly EmailService _email;
        private readonly SmsService _sms;
        private readonly AuthEventService _authEvents;
        private readonly IWebHostEnvironment _env;

        public AccountController(NpgsqlDbContext db, OtpService otp, EmailService email,
            SmsService sms, AuthEventService authEvents, IWebHostEnvironment env)
        { _db = db; _otp = otp; _email = email; _sms = sms; _authEvents = authEvents; _env = env; }

        private int UserId => Convert.ToInt32(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        [HttpGet("contacts")]
        public IActionResult GetContacts()
        {
            using var conn = _db.GetConnection();
            conn.Open();

            using var userCmd = new NpgsqlCommand(@"
                SELECT email, phone_number, is_email_verified, is_phone_verified, is_verified
                FROM users WHERE user_id = @id", conn);
            userCmd.Parameters.AddWithValue("id", UserId);
            using var reader = userCmd.ExecuteReader();
            if (!reader.Read()) return NotFound();

            var primaryEmail  = reader["email"]?.ToString() ?? "";
            var phone         = reader["phone_number"] == DBNull.Value ? null : reader["phone_number"]?.ToString();
            bool emailVerified = Convert.ToBoolean(reader["is_email_verified"]);
            reader.Close();

            var emails = new List<object>
            {
                new { email = primaryEmail, masked = ContactMaskHelper.MaskEmail(primaryEmail), isPrimary = true, isVerified = emailVerified }
            };

            try
            {
                using var extraCmd = new NpgsqlCommand(@"
                    SELECT email, is_verified FROM user_emails
                    WHERE user_id = @id AND is_primary = FALSE ORDER BY created_at", conn);
                extraCmd.Parameters.AddWithValue("id", UserId);
                using var extraReader = extraCmd.ExecuteReader();
                while (extraReader.Read())
                {
                    var e = extraReader["email"].ToString()!;
                    emails.Add(new { email = e, masked = ContactMaskHelper.MaskEmail(e), isPrimary = false, isVerified = Convert.ToBoolean(extraReader["is_verified"]) });
                }
            }
            catch { }

            return Ok(new { emails, phone = (object?)null, canResetViaEmail = emailVerified, canResetViaPhone = false, recoveryHint = "You can reset your password via email." });
        }

        [HttpPost("add-email")]
        public async Task<IActionResult> AddEmail([FromBody] AddContactEmailDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var newEmail = dto.Email.Trim().ToLower();

            using var conn = _db.GetConnection();
            conn.Open();

            using (var takenCmd = new NpgsqlCommand("SELECT COUNT(*) FROM users WHERE LOWER(email) = @e", conn))
            {
                takenCmd.Parameters.AddWithValue("e", newEmail);
                if (Convert.ToInt32(takenCmd.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "That email is already in use." });
            }

            try
            {
                using var checkExtra = new NpgsqlCommand("SELECT COUNT(*) FROM user_emails WHERE LOWER(email) = @e", conn);
                checkExtra.Parameters.AddWithValue("e", newEmail);
                if (Convert.ToInt32(checkExtra.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "That email is already in use." });
            }
            catch { }

            var cooldown = _otp.GetResendCooldownSeconds(conn, "email", newEmail, "ADD_EMAIL");
            if (cooldown > 0)
                return StatusCode(429, new { message = $"Wait {cooldown}s before requesting another code.", resendCooldownSeconds = cooldown });

            var otp = OtpService.Generate();
            await _otp.StoreEmailOtpAsync(conn, newEmail, otp, "ADD_EMAIL");
            await _email.SendOtpEmailAsync(newEmail, otp, "ADD_EMAIL");
            _authEvents.Log(conn, UserId, "ADD_EMAIL_REQUESTED", newEmail);

            var response = new { message = "Verification code sent.", maskedContact = ContactMaskHelper.MaskEmail(newEmail), resendCooldownSeconds = OtpService.ResendCooldownSeconds };
            if (_env.IsDevelopment())
                return Ok(new { response.message, response.maskedContact, response.resendCooldownSeconds, devOtp = otp });
            return Ok(response);
        }

        [HttpPost("verify-email")]
        public IActionResult VerifyAddEmail([FromBody] VerifyAddContactEmailDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var newEmail = dto.Email.Trim().ToLower();

            using var conn = _db.GetConnection();
            conn.Open();

            if (!_otp.ValidateEmailOtp(conn, newEmail, dto.Otp, "ADD_EMAIL"))
                return BadRequest(new { message = "Invalid or expired code." });

            try
            {
                using var insertCmd = new NpgsqlCommand(@"
                    INSERT INTO user_emails (user_id, email, is_primary, is_verified)
                    VALUES (@uid, @email, FALSE, TRUE)", conn);
                insertCmd.Parameters.AddWithValue("uid",   UserId);
                insertCmd.Parameters.AddWithValue("email", newEmail);
                insertCmd.ExecuteNonQuery();
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return BadRequest(new { message = "That email is already linked to an account." });
            }

            _authEvents.Log(conn, UserId, "ADD_EMAIL_VERIFIED", newEmail);
            return Ok(new { message = "Email added and verified. You can now sign in with it." });
        }

        [HttpPost("add-phone")]
        public async Task<IActionResult> AddPhone([FromBody] AddContactPhoneDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

            using var conn = _db.GetConnection();
            conn.Open();

            using (var takenCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM users
                WHERE user_id != @me
                  AND REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@ph, '91' || @ph)", conn))
            {
                takenCmd.Parameters.AddWithValue("me", UserId);
                takenCmd.Parameters.AddWithValue("ph", phone);
                if (Convert.ToInt32(takenCmd.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "This number is already linked to another account." });
            }

            var cooldown = _otp.GetResendCooldownSeconds(conn, "phone", phone, "ADD_PHONE");
            if (cooldown > 0)
                return StatusCode(429, new { message = $"Wait {cooldown}s before requesting another code.", resendCooldownSeconds = cooldown });

            using (var updateCmd = new NpgsqlCommand(
                "UPDATE users SET phone_number = @ph, is_phone_verified = FALSE WHERE user_id = @id", conn))
            {
                updateCmd.Parameters.AddWithValue("ph", phone);
                updateCmd.Parameters.AddWithValue("id", UserId);
                updateCmd.ExecuteNonQuery();
            }

            var otp    = OtpService.Generate();
            await _otp.StorePhoneOtpAsync(conn, phone, otp, "ADD_PHONE");
            var devOtp = await _sms.SendOtpSmsAsync(phone, otp, "VERIFY_PHONE");
            _authEvents.Log(conn, UserId, "ADD_PHONE_REQUESTED", phone);

            var response = new { message = "Verification code sent.", maskedContact = ContactMaskHelper.MaskPhone(phone), resendCooldownSeconds = OtpService.ResendCooldownSeconds };
            if (_env.IsDevelopment() && devOtp != null)
                return Ok(new { response.message, response.maskedContact, response.resendCooldownSeconds, devOtp });
            return Ok(response);
        }

        [HttpPost("verify-phone")]
        public IActionResult VerifyAddPhone([FromBody] VerifyAddContactPhoneDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

            using var conn = _db.GetConnection();
            conn.Open();

            using (var ownerCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM users WHERE user_id = @id
                  AND REGEXP_REPLACE(phone_number, '[^0-9]', '', 'g') IN (@ph, '91' || @ph)", conn))
            {
                ownerCmd.Parameters.AddWithValue("id", UserId);
                ownerCmd.Parameters.AddWithValue("ph", phone);
                if (Convert.ToInt32(ownerCmd.ExecuteScalar()) == 0)
                    return BadRequest(new { message = "Phone does not match your account. Request a new code first." });
            }

            if (!_otp.ValidatePhoneOtp(conn, phone, dto.Otp, "ADD_PHONE"))
                return BadRequest(new { message = "Invalid or expired code." });

            using var verifyCmd = new NpgsqlCommand(
                "UPDATE users SET is_phone_verified = TRUE, phone_number = @ph WHERE user_id = @id", conn);
            verifyCmd.Parameters.AddWithValue("ph", phone);
            verifyCmd.Parameters.AddWithValue("id", UserId);
            verifyCmd.ExecuteNonQuery();

            _authEvents.Log(conn, UserId, "ADD_PHONE_VERIFIED", phone);
            return Ok(new { message = "Phone verified. You can now use it for login and password recovery." });
        }
    }
}
