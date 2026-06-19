using System.ComponentModel.DataAnnotations;

public class ResendVerificationDto
{
    [EmailAddress]
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }
}
