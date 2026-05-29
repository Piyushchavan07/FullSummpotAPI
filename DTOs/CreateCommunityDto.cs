using System.ComponentModel.DataAnnotations;

namespace FullSummpotAPI.DTOs
{
    public class CreateCommunityDto
    {
        [Required]
        [StringLength(100, MinimumLength = 3)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(500)]
        public string Description { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Niche { get; set; } = string.Empty;
    }
}
