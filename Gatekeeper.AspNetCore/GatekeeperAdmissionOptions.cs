using System.Security.Claims;

namespace Gatekeeper.AspNetCore;

public sealed class GatekeeperAdmissionOptions
{
    public string HeaderName { get; set; } = "X-Gatekeeper-Admission";
    /// <summary>Map the existing authenticated host principal to its authorized queue identity.</summary>
    public Func<ClaimsPrincipal, string?> UserIdResolver { get; set; } = null!;
}
