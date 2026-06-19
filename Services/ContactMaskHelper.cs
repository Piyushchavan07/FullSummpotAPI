namespace FullSummpotAPI.Services
{
    public static class ContactMaskHelper
    {
        public static string MaskEmail(string email)
        {
            var at = email.IndexOf('@');
            if (at <= 1) return "***@***";
            var local = email[..at];
            var domain = email[(at + 1)..];
            var maskedLocal = local[0] + new string('*', Math.Min(local.Length - 1, 4));
            var dot = domain.LastIndexOf('.');
            if (dot <= 0) return $"{maskedLocal}@***";
            var domainName = domain[..dot];
            var tld = domain[dot..];
            var maskedDomain = domainName.Length <= 1
                ? "*"
                : domainName[0] + new string('*', Math.Min(domainName.Length - 1, 3));
            return $"{maskedLocal}@{maskedDomain}{tld}";
        }

        public static string MaskPhone(string phone)
        {
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length < 4) return "******";
            return new string('*', digits.Length - 4) + digits[^4..];
        }
    }
}
