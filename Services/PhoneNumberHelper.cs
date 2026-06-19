namespace FullSummpotAPI.Services
{
    public static class PhoneNumberHelper
    {
        /// <summary>
        /// Normalizes Indian mobiles to 10 digits (e.g. +919876543210 → 9876543210).
        /// </summary>
        public static string NormalizeIndianMobile(string phone)
        {
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length == 12 && digits.StartsWith("91"))
                digits = digits[2..];
            if (digits.Length == 11 && digits.StartsWith('0'))
                digits = digits[1..];
            if (digits.Length != 10)
                throw new ArgumentException("Phone must be a 10-digit Indian mobile number.");
            return digits;
        }

        public static bool TryNormalizeIndianMobile(string phone, out string normalized)
        {
            normalized = "";
            try
            {
                normalized = NormalizeIndianMobile(phone);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
