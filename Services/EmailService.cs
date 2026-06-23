using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FullSummpotAPI.Services
{
    public class EmailService
    {
        private readonly string _apiKey;
        private readonly string _fromEmail;
        private readonly string _fromName;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration config, ILogger<EmailService> logger)
        {
            _logger    = logger;
            _apiKey    = config["Brevo:ApiKey"] ?? "";
            _fromEmail = config["Email:FromEmail"] ?? "fullsumppot.noreply@gmail.com";
            _fromName  = config["Email:FromName"]  ?? "FullSumppot";
        }

        public async Task SendOtpEmailAsync(string toEmail, string otp, string purpose)
        {
            bool isReset    = purpose == "RESET_PASSWORD";
            bool isAddEmail = purpose == "ADD_EMAIL";

            var subject = isReset
                ? $"{_fromName} — Password Reset Code"
                : isAddEmail
                    ? $"{_fromName} — Verify New Email"
                    : $"{_fromName} — Verify Your Email";

            var heading  = isReset ? "Reset Your Password"
                : isAddEmail ? "Verify Your New Email"
                : "Verify Your Email Address";

            var bodyLine = isReset
                ? "Use the code below to reset your password. It expires in <strong>15 minutes</strong>."
                : isAddEmail
                    ? "Use the code below to add this email to your account. It expires in <strong>15 minutes</strong>."
                    : "Use the code below to verify your email and activate your account. It expires in <strong>15 minutes</strong>.";

            var html = $@"
<!DOCTYPE html>
<html>
<body style=""font-family:Arial,sans-serif;background:#f4f4f4;margin:0;padding:0"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"">
    <tr><td align=""center"" style=""padding:40px 0"">
      <table width=""480"" cellpadding=""0"" cellspacing=""0""
             style=""background:#fff;border-radius:12px;padding:40px;box-shadow:0 2px 8px rgba(0,0,0,.08)"">
        <tr><td align=""center"" style=""padding-bottom:24px"">
          <h2 style=""color:#e53e3e;margin:0"">{_fromName}</h2>
        </td></tr>
        <tr><td>
          <h3 style=""color:#1f2937;margin-top:0"">{heading}</h3>
          <p style=""color:#6b7280"">{bodyLine}</p>
          <div style=""text-align:center;margin:32px 0"">
            <span style=""font-size:36px;font-weight:bold;letter-spacing:10px;
                          color:#e53e3e;background:#fff5f5;padding:16px 32px;
                          border-radius:8px;display:inline-block"">{otp}</span>
          </div>
          <p style=""color:#9ca3af;font-size:13px"">
            If you didn't request this, you can safely ignore this email.
          </p>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";

            // Use Brevo HTTP API — not blocked by Railway unlike SMTP
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("api-key", _apiKey);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                sender    = new { name = _fromName, email = _fromEmail },
                to        = new[] { new { email = toEmail } },
                subject,
                htmlContent = html
            };

            var content  = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(
                "https://api.brevo.com/v3/smtp/email", content);
            var body     = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("Brevo API response [{Status}]: {Body}",
                (int)response.StatusCode, body);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Failed to send email via Brevo: {body}");
        }
    }
}
