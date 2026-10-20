using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Gatekeeper.AspNetCore;

internal sealed class GatekeeperAdmissionRequirement : IAuthorizationRequirement, IAuthorizationRequirementData
{
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [this];
}

internal sealed class GatekeeperAdmissionHandler(IAdmissionValidator store, IOptions<GatekeeperAdmissionOptions> options)
    : AuthorizationHandler<GatekeeperAdmissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        GatekeeperAdmissionRequirement requirement)
    {
        if (context.Resource is not HttpContext http || !context.User.Identities.Any(identity => identity.IsAuthenticated))
        { context.Fail(); return; }
        var userId = options.Value.UserIdResolver(context.User);
        var headers = http.Request.Headers[options.Value.HeaderName];
        if (string.IsNullOrWhiteSpace(userId) || headers.Count != 1 || string.IsNullOrWhiteSpace(headers[0]))
        { context.Fail(); return; }
        // No query/cookie fallback, primary identity replacement or infrastructure-error swallowing.
        var result = await store.ValidateAdmissionAsync(headers[0]!, userId, http.RequestAborted);
        if (result == AdmissionValidationStatus.Valid) context.Succeed(requirement);
        else context.Fail();
    }
}
