using System.ComponentModel.DataAnnotations;

public class VerifyPhoneDto
{
    [Required]
    [Phone]
    public string PhoneNumber { get; set; } = "";

    [Required]
    [StringLength(6, MinimumLength = 6)]
    public string Otp { get; set; } = "";
}
