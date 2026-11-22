using System.ComponentModel.DataAnnotations;

namespace Gatekeeper.Infrastructure.Configuration;

public class JwtOptions
{
    public const string SectionName = "JwtSettings";

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Up to four additional Base64 keys of at least 32 bytes, accepted only for validation; empty by default.</summary>
    public string[] AdditionalSecretKeys { get; set; } = [];

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;
}