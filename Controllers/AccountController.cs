using FullSummpotAPI.Data;
using FullSummpotAPI.DTOs;
using FullSummpotAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System.Security.Claims;

namespace FullSummpotAPI.Controllers
{
    /// <summary>
    /// Account Center — verified contacts, add backup email/phone (Instagram-style).
    /// One phone per account. Multiple emails per account via USER_EMAILS.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AccountController : ControllerBase
    {
        private readonly OracleDbContext _db;
        private readonly OtpService _otp;
        private readonly EmailService _email;
        private readonly SmsService _sms;
        private readonly AuthEventService _authEvents;
        private readonly IWebHostEnvironment _env;

        public AccountController(OracleDbContext db, OtpService otp, EmailService email,
            SmsService sms, AuthEventService authEvents, IWebHostEnvironment env)
        {
            _db = db;
            _otp = otp;
            _email = email;
            _sms = sms;
            _authEvents = authEvents;
            _env = env;
        }

        private int UserId => Convert.ToInt32(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        // GET /api/Account/contacts
        [HttpGet("contacts")]
        public IActionResult GetContacts()
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var userCmd = new OracleCommand(@"
                SELECT EMAIL, PHONE_NUMBER, IS_EMAIL_VERIFIED, IS_PHONE_VERIFIED, IS_VERIFIED
                FROM USERS WHERE USER_ID = :id", conn);
            userCmd.BindByName = true;
            userCmd.Parameters.Add("id", OracleDbType.Int32).Value = UserId;
            using var reader = userCmd.ExecuteReader();
            if (!reader.Read()) return NotFound();

            var primaryEmail = reader["EMAIL"]?.ToString() ?? "";
            var phone = reader["PHONE_NUMBER"] == DBNull.Value ? null : reader["PHONE_NUMBER"]?.ToString();
            bool emailVerified = Convert.ToInt32(reader["IS_EMAIL_VERIFIED"]) == 1;
            bool phoneVerified = Convert.ToInt32(reader["IS_PHONE_VERIFIED"]) == 1;

            var emails = new List<object>
            {
                new
                {
                    email = primaryEmail,
                    masked = ContactMaskHelper.MaskEmail(primaryEmail),
                    isPrimary = true,
                    isVerified = emailVerified
                }
            };

            var extraCmd = new OracleCommand(@"
                SELECT EMAIL, IS_VERIFIED FROM USER_EMAILS
                WHERE USER_ID = :id AND IS_PRIMARY = 0
                ORDER BY CREATED_AT", conn);
            extraCmd.BindByName = true;
            extraCmd.Parameters.Add("id", OracleDbType.Int32).Value = UserId;
            using var extraReader = extraCmd.ExecuteReader();
            while (extraReader.Read())
            {
                var e = extraReader["EMAIL"].ToString()!;
                emails.Add(new
                {
                    email = e,
                    masked = ContactMaskHelper.MaskEmail(e),
                    isPrimary = false,
                    isVerified = Convert.ToInt32(extraReader["IS_VERIFIED"]) == 1
                });
            }

            return Ok(new
            {
                emails,
                phone = (object?)null,
                canResetViaEmail = emailVerified,
                canResetViaPhone = false,
                recoveryHint = "You can reset your password via email."
            });
        }

        // POST /api/Account/add-email
        [HttpPost("add-email")]
        public async Task<IActionResult> AddEmail([FromBody] AddContactEmailDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var newEmail = dto.Email.Trim().ToLower();

            using var conn = _db.GetConnection();
            conn.Open();

            var takenCmd = new OracleCommand(
                "SELECT COUNT(*) FROM USERS WHERE LOWER(EMAIL) = :e", conn);
            takenCmd.BindByName = true;
            takenCmd.Parameters.Add("e", newEmail);
            if (Convert.ToInt32(takenCmd.ExecuteScalar()) > 0)
                return BadRequest(new { message = "That email is already in use." });

            try
            {
                var checkExtra = new OracleCommand(
                    "SELECT COUNT(*) FROM USER_EMAILS WHERE LOWER(EMAIL) = :e", conn);
                checkExtra.BindByName = true;
                checkExtra.Parameters.Add("e", newEmail);
                if (Convert.ToInt32(checkExtra.ExecuteScalar()) > 0)
                    return BadRequest(new { message = "That email is already in use." });
            }
            catch { /* USER_EMAILS may not exist until migration */ }

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

        // POST /api/Account/verify-email
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
                var insertCmd = new OracleCommand(@"
                    INSERT INTO USER_EMAILS (USER_ID, EMAIL, IS_PRIMARY, IS_VERIFIED)
                    VALUES (:userId, :email, 0, 1)", conn);
                insertCmd.BindByName = true;
                insertCmd.Parameters.Add("userId", OracleDbType.Int32).Value = UserId;
                insertCmd.Parameters.Add("email", newEmail);
                insertCmd.ExecuteNonQuery();
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                return BadRequest(new { message = "That email is already linked to an account." });
            }

            _authEvents.Log(conn, UserId, "ADD_EMAIL_VERIFIED", newEmail);
            return Ok(new { message = "Email added and verified. You can now sign in with it." });
        }

        // POST /api/Account/add-phone
        [HttpPost("add-phone")]
        public async Task<IActionResult> AddPhone([FromBody] AddContactPhoneDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

            using var conn = _db.GetConnection();
            conn.Open();

            var takenCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM USERS
                WHERE USER_ID != :me
                  AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
            takenCmd.BindByName = true;
            takenCmd.Parameters.Add("me", OracleDbType.Int32).Value = UserId;
            takenCmd.Parameters.Add("ph", phone);
            if (Convert.ToInt32(takenCmd.ExecuteScalar()) > 0)
                return BadRequest(new { message = "This number is already linked to another account." });

            var cooldown = _otp.GetResendCooldownSeconds(conn, "phone", phone, "ADD_PHONE");
            if (cooldown > 0)
                return StatusCode(429, new { message = $"Wait {cooldown}s before requesting another code.", resendCooldownSeconds = cooldown });

            // Save unverified phone on account, verify via OTP next step
            var updateCmd = new OracleCommand(@"
                UPDATE USERS SET PHONE_NUMBER = :ph, IS_PHONE_VERIFIED = 0
                WHERE USER_ID = :id", conn);
            updateCmd.BindByName = true;
            updateCmd.Parameters.Add("ph", phone);
            updateCmd.Parameters.Add("id", OracleDbType.Int32).Value = UserId;
            updateCmd.ExecuteNonQuery();

            var otp = OtpService.Generate();
            await _otp.StorePhoneOtpAsync(conn, phone, otp, "ADD_PHONE");
            var devOtp = await _sms.SendOtpSmsAsync(phone, otp, "VERIFY_PHONE");

            _authEvents.Log(conn, UserId, "ADD_PHONE_REQUESTED", phone);

            var response = new { message = "Verification code sent.", maskedContact = ContactMaskHelper.MaskPhone(phone), resendCooldownSeconds = OtpService.ResendCooldownSeconds };
            if (_env.IsDevelopment() && devOtp != null)
                return Ok(new { response.message, response.maskedContact, response.resendCooldownSeconds, devOtp });
            return Ok(response);
        }

        // POST /api/Account/verify-phone
        [HttpPost("verify-phone")]
        public IActionResult VerifyAddPhone([FromBody] VerifyAddContactPhoneDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!PhoneNumberHelper.TryNormalizeIndianMobile(dto.PhoneNumber, out var phone))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });

            using var conn = _db.GetConnection();
            conn.Open();

            var ownerCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM USERS WHERE USER_ID = :id
                  AND REGEXP_REPLACE(PHONE_NUMBER, '[^0-9]', '') IN (:ph, '91' || :ph)", conn);
            ownerCmd.BindByName = true;
            ownerCmd.Parameters.Add("id", OracleDbType.Int32).Value = UserId;
            ownerCmd.Parameters.Add("ph", phone);
            if (Convert.ToInt32(ownerCmd.ExecuteScalar()) == 0)
                return BadRequest(new { message = "Phone does not match your account. Request a new code first." });

            if (!_otp.ValidatePhoneOtp(conn, phone, dto.Otp, "ADD_PHONE"))
                return BadRequest(new { message = "Invalid or expired code." });

            var verifyCmd = new OracleCommand(
                "UPDATE USERS SET IS_PHONE_VERIFIED = 1, PHONE_NUMBER = :ph WHERE USER_ID = :id", conn);
            verifyCmd.BindByName = true;
            verifyCmd.Parameters.Add("ph", phone);
            verifyCmd.Parameters.Add("id", OracleDbType.Int32).Value = UserId;
            verifyCmd.ExecuteNonQuery();

            _authEvents.Log(conn, UserId, "ADD_PHONE_VERIFIED", phone);
            return Ok(new { message = "Phone verified. You can now use it for login and password recovery." });
        }
    }
}
