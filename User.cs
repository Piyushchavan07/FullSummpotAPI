namespace FullSummpotAPI.Models
{
    public class User
    {
        public int User_Id { get; set; }

        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}