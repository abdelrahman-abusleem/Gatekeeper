using System.ComponentModel.DataAnnotations;

namespace Gatekeeper.Infrastructure.Configuration;

public class JwtOptions
{
    public const string SectionName = "JwtSettings";
    [Obsolete("Unused. Use QueueOptions.ActivePassesKey.")]
    public string ActivePassesKey { get; init; } = "gatekeeper:active_passes";


    [Required]
    public string SecretKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;
}