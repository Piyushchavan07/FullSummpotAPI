using System.ComponentModel.DataAnnotations;

public class LoginDto
{
    /// <summary>Email address or 10-digit Indian mobile number.</summary>
    [Required]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";
}
