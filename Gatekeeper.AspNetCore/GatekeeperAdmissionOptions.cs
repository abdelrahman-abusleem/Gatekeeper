using System.Security.Claims;

namespace Gatekeeper.AspNetCore;

/// <summary>Host authentication integration and admission HTTP header options, validated at startup.</summary>
public sealed class GatekeeperAdmissionOptions
{
    /// <summary>HTTP token-header name; default X-Gatekeeper-Admission. Must be nonempty and contain only ASCII HTTP field-name token characters.</summary>
    public string HeaderName { get; set; } = "X-Gatekeeper-Admission";
    /// <summary>Required resolver of the host-authenticated principal to its trusted queue identity; default null is invalid. A blank or null result denies admission.</summary>
    public Func<ClaimsPrincipal, string?> UserIdResolver { get; set; } = null!;
}
