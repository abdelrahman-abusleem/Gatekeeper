using System.ComponentModel.DataAnnotations;

namespace Gatekeeper.Infrastructure.Configuration;

/// <summary>JWT signing and validation policy captured when the token service is constructed.</summary>
public class JwtOptions
{
    /// <summary>The default configuration section name, JwtSettings.</summary>
    public const string SectionName = "JwtSettings";

    /// <summary>Base64 primary HS256 signing key, decoding to at least 32 bytes; default empty is invalid and must be configured. New tokens use only this key.</summary>
    [Required]
    public string SecretKey { get; set; } = string.Empty;
    /// <summary>Validation-only Base64 keys; default empty array, valid 0..4 entries, each at least 32 decoded bytes and distinct from the primary and each other. Equivalent Base64 encodings count as duplicates.</summary>
    public string[] AdditionalSecretKeys { get; set; } = [];

    /// <summary>Required nonblank issuer shared by signers and validators; default empty is invalid.</summary>
    [Required]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Required nonblank audience shared by signers and validators; default empty is invalid.</summary>
    [Required]
    public string Audience { get; set; } = string.Empty;
}