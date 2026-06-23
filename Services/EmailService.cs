using System.Net;
using System.Net.Mail;

namespace FullSummpotAPI.Services
{
    public class EmailService
    {
        private readonly string _smtpHost;
        private readonly int    _smtpPort;
        private readonly string _fromEmail;
        private readonly string _fromName;
        private readonly string _appPassword;

        public EmailService(IConfiguration config)
        {
            _smtpHost    = config["Email:SmtpHost"]    ?? "smtp.gmail.com";
            _smtpPort    = int.Parse(config["Email:SmtpPort"] ?? "587");
            _fromEmail   = config["Email:FromEmail"]   ?? "";
            _fromName    = config["Email:FromName"]    ?? "FullSumppot";
            _appPassword = config["Email:AppPassword"] ?? "";
        }

        public async Task SendOtpEmailAsync(string toEmail, string otp, string purpose)
        {
            bool isReset = purpose == "RESET_PASSWORD";
            bool isAddEmail = purpose == "ADD_EMAIL";

            var subject = isReset
                ? $"{_fromName} — Password Reset Code"
                : isAddEmail
                    ? $"{_fromName} — Verify New Email"
                    : $"{_fromName} — Verify Your Email";

            var heading = isReset ? "Reset Your Password"
                : isAddEmail ? "Verify Your New Email"
                : "Verify Your Email Address";

            var bodyLine = isReset
                ? "Use the code below to reset your password. It expires in <strong>15 minutes</strong>."
                : isAddEmail
                    ? "Use the code below to add this email to your FullSumppot account. It expires in <strong>15 minutes</strong>."
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
          <h2 style=""color:#7c3aed;margin:0"">{_fromName}</h2>
        </td></tr>
        <tr><td>
          <h3 style=""color:#1f2937;margin-top:0"">{heading}</h3>
          <p style=""color:#6b7280"">{bodyLine}</p>
          <div style=""text-align:center;margin:32px 0"">
            <span style=""font-size:36px;font-weight:bold;letter-spacing:10px;
                          color:#7c3aed;background:#f3f0ff;padding:16px 32px;
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

            using var client = new SmtpClient(_smtpHost, _smtpPort)
            {
                EnableSsl   = true,
                Credentials = new NetworkCredential(_fromEmail, _appPassword),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 30000
            };

            using var mail = new MailMessage
            {
                From       = new MailAddress(_fromEmail, _fromName),
                Subject    = subject,
                Body       = html,
                IsBodyHtml = true
            };
            mail.To.Add(toEmail);

            await client.SendMailAsync(mail);
        }
    }
}
