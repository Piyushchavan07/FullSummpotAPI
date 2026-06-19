using System.ComponentModel.DataAnnotations;

public class VerifyPhoneFirebaseDto
{
    [Required]
    public string IdToken { get; set; } = "";

    public string? Email { get; set; }
}
