using System.ComponentModel.DataAnnotations;

namespace Gatekeeper.Infrastructure.Configuration;

public class JwtOptions
{
    public const string SectionName = "JwtSettings";

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;
}