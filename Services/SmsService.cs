using System.Text.Json;

namespace FullSummpotAPI.Services
{
    public class SmsService
    {
        private readonly string _apiKey;
        private readonly string _route;
        private readonly bool _isConfigured;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;
        private readonly ILogger<SmsService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public SmsService(IConfiguration config, ILogger<SmsService> logger, IHttpClientFactory httpClientFactory, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _env = env;
            _apiKey = config["Fast2SMS:ApiKey"] ?? "";
            _route = config["Fast2SMS:Route"] ?? "otp";
            _isConfigured = !string.IsNullOrWhiteSpace(_apiKey);
        }

        /// <summary>
        /// Sends an OTP via Fast2SMS. Returns the OTP when running in dev fallback mode
        /// (no API key or request fails in development), otherwise null on success.
        /// </summary>
        public async Task<string?> SendOtpSmsAsync(string toPhone, string otp, string purpose)
        {
            string purposeLabel = purpose switch
            {
                "VERIFY_PHONE"   => "Phone Verification",
                "RESET_PASSWORD" => "Password Reset",
                _                => "Verification"
            };

            if (!_isConfigured)
            {
                _logger.LogWarning("=== SMS FALLBACK (Fast2SMS:ApiKey is empty) ===");
                _logger.LogWarning("To: {Phone} | Purpose: {Purpose} | OTP: {Otp}", toPhone, purposeLabel, otp);
                Console.WriteLine($"\n[SMS DEV] {purposeLabel} to {toPhone}: {otp}\n");
                return otp;
            }

            var formattedPhone = PhoneNumberHelper.NormalizeIndianMobile(toPhone);

            var client = _httpClientFactory.CreateClient("Fast2SMS");
            client.DefaultRequestHeaders.Remove("authorization");
            client.DefaultRequestHeaders.Add("authorization", _apiKey);

            // "otp" route is the free/cheap path on Fast2SMS (uses dashboard OTP template).
            // "q" route needs DLT-approved custom text + wallet balance.
            FormUrlEncodedContent content;
            if (_route.Equals("otp", StringComparison.OrdinalIgnoreCase))
            {
                content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("route", "otp"),
                    new KeyValuePair<string, string>("variables_values", otp),
                    new KeyValuePair<string, string>("numbers", formattedPhone),
                });
            }
            else
            {
                var messageBody = $"[FullSumppot] Your {purposeLabel} code is: {otp}. It expires in 15 minutes.";
                content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("route", "q"),
                    new KeyValuePair<string, string>("message", messageBody),
                    new KeyValuePair<string, string>("language", "english"),
                    new KeyValuePair<string, string>("flash", "0"),
                    new KeyValuePair<string, string>("numbers", formattedPhone),
                });
            }

            try
            {
                var response = await client.PostAsync("https://www.fast2sms.com/dev/bulkV2", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Fast2SMS HTTP {Status}: {Body}", response.StatusCode, body);
                    throw new Exception($"Fast2SMS error ({response.StatusCode}): {body}");
                }

                // Fast2SMS often returns HTTP 200 with return:false in JSON
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("return", out var ok) && ok.ValueKind == JsonValueKind.False)
                    {
                        var msg = doc.RootElement.TryGetProperty("message", out var m)
                            ? m.GetString() ?? "Unknown Fast2SMS error"
                            : "Unknown Fast2SMS error";
                        _logger.LogError("Fast2SMS rejected request: {Message} | Body: {Body}", msg, body);
                        throw new Exception($"Fast2SMS: {msg}");
                    }
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Fast2SMS returned non-JSON body: {Body}", body);
                }

                _logger.LogInformation("SMS sent to {Phone} via Fast2SMS ({Purpose})", formattedPhone, purposeLabel);
                return null;
            }
            catch (Exception ex)
            {
                if (_env.EnvironmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Fast2SMS failed in development. Falling back to Dev OTP. Error: {Error}", ex.Message);
                    return otp;
                }
                throw;
            }
        }
    }
}
