using Oracle.ManagedDataAccess.Client;
using System.Security.Cryptography;

namespace FullSummpotAPI.Services
{
    public class OtpService
    {
        public const int ExpiryMinutes = 15;
        public const int ResendCooldownSeconds = 60;
        public const int MaxWrongAttempts = 5;

        public static string Generate() =>
            (BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4), 0) % 1_000_000).ToString("D6");

        public int GetResendCooldownSeconds(OracleConnection conn, string channel, string contact, string purpose)
        {
            var sql = channel == "email"
                ? @"SELECT GREATEST(0, :cooldown - EXTRACT(SECOND FROM (SYS_EXTRACT_UTC(SYSTIMESTAMP) - MAX(CREATED_AT)))
                    - EXTRACT(MINUTE FROM (SYS_EXTRACT_UTC(SYSTIMESTAMP) - MAX(CREATED_AT))) * 60)
                    FROM EMAIL_OTPS
                    WHERE LOWER(EMAIL) = :contact AND PURPOSE = :purpose
                      AND CREATED_AT > SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' HOUR"
                : @"SELECT GREATEST(0, :cooldown - EXTRACT(SECOND FROM (SYS_EXTRACT_UTC(SYSTIMESTAMP) - MAX(CREATED_AT)))
                    - EXTRACT(MINUTE FROM (SYS_EXTRACT_UTC(SYSTIMESTAMP) - MAX(CREATED_AT))) * 60)
                    FROM PHONE_OTPS
                    WHERE PHONE_NUMBER = :contact AND PURPOSE = :purpose
                      AND CREATED_AT > SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' HOUR";

            var cmd = new OracleCommand(sql, conn);
            cmd.BindByName = true;
            cmd.Parameters.Add("cooldown", OracleDbType.Int32).Value = ResendCooldownSeconds;
            cmd.Parameters.Add("contact", OracleDbType.Varchar2).Value =
                channel == "email" ? contact.ToLower() : contact;
            cmd.Parameters.Add("purpose", OracleDbType.Varchar2).Value = purpose;

            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return 0;
            return Math.Max(0, Convert.ToInt32(Math.Floor(Convert.ToDecimal(result))));
        }

        public async Task StoreEmailOtpAsync(OracleConnection conn, string email, string otp, string purpose)
        {
            var expireCmd = new OracleCommand(@"
                UPDATE EMAIL_OTPS SET USED = 1
                WHERE LOWER(EMAIL) = :email AND PURPOSE = :purpose AND USED = 0", conn);
            expireCmd.BindByName = true;
            expireCmd.Parameters.Add("email", email.ToLower());
            expireCmd.Parameters.Add("purpose", purpose);
            await expireCmd.ExecuteNonQueryAsync();

            var insertCmd = new OracleCommand($@"
                INSERT INTO EMAIL_OTPS (EMAIL, OTP_CODE, PURPOSE, EXPIRES_AT, WRONG_ATTEMPTS, CREATED_AT)
                VALUES (:email, :otp, :purpose,
                        SYS_EXTRACT_UTC(SYSTIMESTAMP) + INTERVAL '{ExpiryMinutes}' MINUTE, 0,
                        SYS_EXTRACT_UTC(SYSTIMESTAMP))", conn);
            insertCmd.BindByName = true;
            insertCmd.Parameters.Add("email", email.ToLower());
            insertCmd.Parameters.Add("otp", otp);
            insertCmd.Parameters.Add("purpose", purpose);
            await insertCmd.ExecuteNonQueryAsync();
        }

        public async Task StorePhoneOtpAsync(OracleConnection conn, string phone, string otp, string purpose)
        {
            var expireCmd = new OracleCommand(@"
                UPDATE PHONE_OTPS SET USED = 1
                WHERE PHONE_NUMBER = :phone AND PURPOSE = :purpose AND USED = 0", conn);
            expireCmd.BindByName = true;
            expireCmd.Parameters.Add("phone", phone);
            expireCmd.Parameters.Add("purpose", purpose);
            await expireCmd.ExecuteNonQueryAsync();

            var insertCmd = new OracleCommand($@"
                INSERT INTO PHONE_OTPS (PHONE_NUMBER, OTP_CODE, PURPOSE, EXPIRES_AT, WRONG_ATTEMPTS, CREATED_AT)
                VALUES (:phone, :otp, :purpose,
                        SYS_EXTRACT_UTC(SYSTIMESTAMP) + INTERVAL '{ExpiryMinutes}' MINUTE, 0,
                        SYS_EXTRACT_UTC(SYSTIMESTAMP))", conn);
            insertCmd.BindByName = true;
            insertCmd.Parameters.Add("phone", phone);
            insertCmd.Parameters.Add("otp", otp);
            insertCmd.Parameters.Add("purpose", purpose);
            await insertCmd.ExecuteNonQueryAsync();
        }

        public bool ValidateEmailOtp(OracleConnection conn, string email, string otp, string purpose)
        {
            var findCmd = new OracleCommand(@"
                SELECT OTP_ID, WRONG_ATTEMPTS FROM EMAIL_OTPS
                WHERE LOWER(EMAIL) = :email AND PURPOSE = :purpose AND USED = 0
                  AND EXPIRES_AT > SYS_EXTRACT_UTC(SYSTIMESTAMP)
                ORDER BY CREATED_AT DESC FETCH FIRST 1 ROW ONLY", conn);
            findCmd.BindByName = true;
            findCmd.Parameters.Add("email", email.ToLower());
            findCmd.Parameters.Add("purpose", purpose);

            using var reader = findCmd.ExecuteReader();
            if (!reader.Read()) return false;

            int otpId = Convert.ToInt32(reader["OTP_ID"]);
            int wrong = reader["WRONG_ATTEMPTS"] == DBNull.Value ? 0 : Convert.ToInt32(reader["WRONG_ATTEMPTS"]);
            reader.Close();

            var checkCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM EMAIL_OTPS
                WHERE OTP_ID = :id AND OTP_CODE = :otp", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("id", OracleDbType.Int32).Value = otpId;
            checkCmd.Parameters.Add("otp", otp);

            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
            {
                wrong++;
                var failCmd = new OracleCommand(@"
                    UPDATE EMAIL_OTPS SET WRONG_ATTEMPTS = :wrong,
                           USED = CASE WHEN :wrong >= :max THEN 1 ELSE USED END
                    WHERE OTP_ID = :id", conn);
                failCmd.BindByName = true;
                failCmd.Parameters.Add("wrong", OracleDbType.Int32).Value = wrong;
                failCmd.Parameters.Add("max", OracleDbType.Int32).Value = MaxWrongAttempts;
                failCmd.Parameters.Add("id", OracleDbType.Int32).Value = otpId;
                failCmd.ExecuteNonQuery();
                return false;
            }

            var markCmd = new OracleCommand("UPDATE EMAIL_OTPS SET USED = 1 WHERE OTP_ID = :id", conn);
            markCmd.BindByName = true;
            markCmd.Parameters.Add("id", OracleDbType.Int32).Value = otpId;
            markCmd.ExecuteNonQuery();
            return true;
        }

        public bool ValidatePhoneOtp(OracleConnection conn, string phone, string otp, string purpose)
        {
            var findCmd = new OracleCommand(@"
                SELECT OTP_ID, WRONG_ATTEMPTS FROM PHONE_OTPS
                WHERE PHONE_NUMBER = :phone AND PURPOSE = :purpose AND USED = 0
                  AND EXPIRES_AT > SYS_EXTRACT_UTC(SYSTIMESTAMP)
                ORDER BY CREATED_AT DESC FETCH FIRST 1 ROW ONLY", conn);
            findCmd.BindByName = true;
            findCmd.Parameters.Add("phone", phone);
            findCmd.Parameters.Add("purpose", purpose);

            using var reader = findCmd.ExecuteReader();
            if (!reader.Read()) return false;

            int otpId = Convert.ToInt32(reader["OTP_ID"]);
            int wrong = reader["WRONG_ATTEMPTS"] == DBNull.Value ? 0 : Convert.ToInt32(reader["WRONG_ATTEMPTS"]);
            reader.Close();

            var checkCmd = new OracleCommand(@"
                SELECT COUNT(*) FROM PHONE_OTPS
                WHERE OTP_ID = :id AND OTP_CODE = :otp", conn);
            checkCmd.BindByName = true;
            checkCmd.Parameters.Add("id", OracleDbType.Int32).Value = otpId;
            checkCmd.Parameters.Add("otp", otp);

            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
            {
                wrong++;
                var failCmd = new OracleCommand(@"
                    UPDATE PHONE_OTPS SET WRONG_ATTEMPTS = :wrong,
                           USED = CASE WHEN :wrong >= :max THEN 1 ELSE USED END
                    WHERE OTP_ID = :id", conn);
                failCmd.BindByName = true;
                failCmd.Parameters.Add("wrong", OracleDbType.Int32).Value = wrong;
                failCmd.Parameters.Add("max", OracleDbType.Int32).Value = MaxWrongAttempts;
                failCmd.Parameters.Add("id", OracleDbType.Int32).Value = otpId;
                failCmd.ExecuteNonQuery();
                return false;
            }

            var markCmd = new OracleCommand("UPDATE PHONE_OTPS SET USED = 1 WHERE OTP_ID = :id", conn);
            markCmd.BindByName = true;
            markCmd.Parameters.Add("id", OracleDbType.Int32).Value = otpId;
            markCmd.ExecuteNonQuery();
            return true;
        }
    }
}
