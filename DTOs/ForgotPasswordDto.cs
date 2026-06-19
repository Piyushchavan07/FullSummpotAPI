using System.ComponentModel.DataAnnotations;

public class ForgotPasswordDto
{
    /// <summary>
    /// Can be an email address or phone number.
    /// The backend detects the format and sends OTP via the appropriate channel.
    /// </summary>
    [Required]
    public string Contact { get; set; } = "";
}
