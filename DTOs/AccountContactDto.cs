using System.ComponentModel.DataAnnotations;

public class AddContactEmailDto
{
    [Required][EmailAddress]
    public string Email { get; set; } = "";
}

public class VerifyAddContactEmailDto
{
    [Required][EmailAddress]
    public string Email { get; set; } = "";

    [Required][StringLength(6, MinimumLength = 6)]
    public string Otp { get; set; } = "";
}

public class AddContactPhoneDto
{
    [Required]
    public string PhoneNumber { get; set; } = "";
}

public class VerifyAddContactPhoneDto
{
    [Required]
    public string PhoneNumber { get; set; } = "";

    [Required][StringLength(6, MinimumLength = 6)]
    public string Otp { get; set; } = "";
}
