using System.ComponentModel.DataAnnotations;

public class VerifyEmailDto
{
    [Required][EmailAddress]
    public string Email { get; set; } = "";

    [Required][StringLength(6, MinimumLength = 6)]
    public string Otp { get; set; } = "";
}
