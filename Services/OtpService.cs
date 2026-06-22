using Npgsql;
using System.Security.Cryptography;

namespace FullSummpotAPI.Services
{
    public class OtpService
    {
        public const int ExpiryMinutes          = 15;
        public const int ResendCooldownSeconds  = 60;
        public const int MaxWrongAttempts       = 5;

        public static string Generate() =>
            (BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4), 0) % 1_000_000).ToString("D6");

        public int GetResendCooldownSeconds(NpgsqlConnection conn, string channel, string contact, string purpose)
        {
            var sql = channel == "email"
                ? @"SELECT GREATEST(0, @cooldown - EXTRACT(EPOCH FROM (NOW() AT TIME ZONE 'UTC' - MAX(created_at))))
                    FROM email_otps
                    WHERE LOWER(email) = @contact AND purpose = @purpose
                      AND created_at > NOW() AT TIME ZONE 'UTC' - INTERVAL '1 hour'"
                : @"SELECT GREATEST(0, @cooldown - EXTRACT(EPOCH FROM (NOW() AT TIME ZONE 'UTC' - MAX(created_at))))
                    FROM phone_otps
                    WHERE phone_number = @contact AND purpose = @purpose
                      AND created_at > NOW() AT TIME ZONE 'UTC' - INTERVAL '1 hour'";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("cooldown", ResendCooldownSeconds);
            cmd.Parameters.AddWithValue("contact", channel == "email" ? contact.ToLower() : contact);
            cmd.Parameters.AddWithValue("purpose", purpose);

            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return 0;
            return Math.Max(0, Convert.ToInt32(Math.Floor(Convert.ToDecimal(result))));
        }

        public async Task StoreEmailOtpAsync(NpgsqlConnection conn, string email, string otp, string purpose)
        {
            using var expireCmd = new NpgsqlCommand(@"
                UPDATE email_otps SET used = TRUE
                WHERE LOWER(email) = @email AND purpose = @purpose AND used = FALSE", conn);
            expireCmd.Parameters.AddWithValue("email", email.ToLower());
            expireCmd.Parameters.AddWithValue("purpose", purpose);
            await expireCmd.ExecuteNonQueryAsync();

            using var insertCmd = new NpgsqlCommand($@"
                INSERT INTO email_otps (email, otp_code, purpose, expires_at, wrong_attempts, created_at)
                VALUES (@email, @otp, @purpose,
                        NOW() AT TIME ZONE 'UTC' + INTERVAL '{ExpiryMinutes} minutes',
                        0, NOW() AT TIME ZONE 'UTC')", conn);
            insertCmd.Parameters.AddWithValue("email", email.ToLower());
            insertCmd.Parameters.AddWithValue("otp", otp);
            insertCmd.Parameters.AddWithValue("purpose", purpose);
            await insertCmd.ExecuteNonQueryAsync();
        }

        public async Task StorePhoneOtpAsync(NpgsqlConnection conn, string phone, string otp, string purpose)
        {
            using var expireCmd = new NpgsqlCommand(@"
                UPDATE phone_otps SET used = TRUE
                WHERE phone_number = @phone AND purpose = @purpose AND used = FALSE", conn);
            expireCmd.Parameters.AddWithValue("phone", phone);
            expireCmd.Parameters.AddWithValue("purpose", purpose);
            await expireCmd.ExecuteNonQueryAsync();

            using var insertCmd = new NpgsqlCommand($@"
                INSERT INTO phone_otps (phone_number, otp_code, purpose, expires_at, wrong_attempts, created_at)
                VALUES (@phone, @otp, @purpose,
                        NOW() AT TIME ZONE 'UTC' + INTERVAL '{ExpiryMinutes} minutes',
                        0, NOW() AT TIME ZONE 'UTC')", conn);
            insertCmd.Parameters.AddWithValue("phone", phone);
            insertCmd.Parameters.AddWithValue("otp", otp);
            insertCmd.Parameters.AddWithValue("purpose", purpose);
            await insertCmd.ExecuteNonQueryAsync();
        }

        public bool ValidateEmailOtp(NpgsqlConnection conn, string email, string otp, string purpose)
        {
            using var findCmd = new NpgsqlCommand(@"
                SELECT otp_id, wrong_attempts FROM email_otps
                WHERE LOWER(email) = @email AND purpose = @purpose AND used = FALSE
                  AND expires_at > NOW() AT TIME ZONE 'UTC'
                ORDER BY created_at DESC LIMIT 1", conn);
            findCmd.Parameters.AddWithValue("email", email.ToLower());
            findCmd.Parameters.AddWithValue("purpose", purpose);

            int otpId, wrong;
            using (var reader = findCmd.ExecuteReader())
            {
                if (!reader.Read()) return false;
                otpId = Convert.ToInt32(reader["otp_id"]);
                wrong = reader["wrong_attempts"] == DBNull.Value ? 0 : Convert.ToInt32(reader["wrong_attempts"]);
            }

            using var checkCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM email_otps WHERE otp_id = @id AND otp_code = @otp", conn);
            checkCmd.Parameters.AddWithValue("id", otpId);
            checkCmd.Parameters.AddWithValue("otp", otp);

            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
            {
                wrong++;
                using var failCmd = new NpgsqlCommand(@"
                    UPDATE email_otps SET wrong_attempts = @wrong,
                           used = CASE WHEN @wrong >= @max THEN TRUE ELSE used END
                    WHERE otp_id = @id", conn);
                failCmd.Parameters.AddWithValue("wrong", wrong);
                failCmd.Parameters.AddWithValue("max", MaxWrongAttempts);
                failCmd.Parameters.AddWithValue("id", otpId);
                failCmd.ExecuteNonQuery();
                return false;
            }

            using var markCmd = new NpgsqlCommand(
                "UPDATE email_otps SET used = TRUE WHERE otp_id = @id", conn);
            markCmd.Parameters.AddWithValue("id", otpId);
            markCmd.ExecuteNonQuery();
            return true;
        }

        public bool ValidatePhoneOtp(NpgsqlConnection conn, string phone, string otp, string purpose)
        {
            using var findCmd = new NpgsqlCommand(@"
                SELECT otp_id, wrong_attempts FROM phone_otps
                WHERE phone_number = @phone AND purpose = @purpose AND used = FALSE
                  AND expires_at > NOW() AT TIME ZONE 'UTC'
                ORDER BY created_at DESC LIMIT 1", conn);
            findCmd.Parameters.AddWithValue("phone", phone);
            findCmd.Parameters.AddWithValue("purpose", purpose);

            int otpId, wrong;
            using (var reader = findCmd.ExecuteReader())
            {
                if (!reader.Read()) return false;
                otpId = Convert.ToInt32(reader["otp_id"]);
                wrong = reader["wrong_attempts"] == DBNull.Value ? 0 : Convert.ToInt32(reader["wrong_attempts"]);
            }

            using var checkCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM phone_otps WHERE otp_id = @id AND otp_code = @otp", conn);
            checkCmd.Parameters.AddWithValue("id", otpId);
            checkCmd.Parameters.AddWithValue("otp", otp);

            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
            {
                wrong++;
                using var failCmd = new NpgsqlCommand(@"
                    UPDATE phone_otps SET wrong_attempts = @wrong,
                           used = CASE WHEN @wrong >= @max THEN TRUE ELSE used END
                    WHERE otp_id = @id", conn);
                failCmd.Parameters.AddWithValue("wrong", wrong);
                failCmd.Parameters.AddWithValue("max", MaxWrongAttempts);
                failCmd.Parameters.AddWithValue("id", otpId);
                failCmd.ExecuteNonQuery();
                return false;
            }

            using var markCmd = new NpgsqlCommand(
                "UPDATE phone_otps SET used = TRUE WHERE otp_id = @id", conn);
            markCmd.Parameters.AddWithValue("id", otpId);
            markCmd.ExecuteNonQuery();
            return true;
        }
    }
}
