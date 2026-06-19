using System.ComponentModel.DataAnnotations;

public class ResetPasswordDto
{
    /// <summary>
    /// Can be an email address or phone number.
    /// Must match the contact used when requesting the forgot-password OTP.
    /// </summary>
    [Required]
    public string Contact { get; set; } = "";

    [Required][StringLength(6, MinimumLength = 6)]
    public string Otp { get; set; } = "";

    [Required][StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = "";
}
