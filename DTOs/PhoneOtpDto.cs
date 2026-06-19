using System.ComponentModel.DataAnnotations;

public class PhoneOtpDto
{
    [Required]
    [Phone]
    public string PhoneNumber { get; set; } = "";

    /// <summary>Optional: used to look up the user when sending verification OTP during registration</summary>
    public string? Email { get; set; }
}
