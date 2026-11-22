# Gatekeeper

Redis-backed virtual waiting rooms and controlled admission for distributed .NET 10 applications, with JWT admission passes and optional ASP.NET Core endpoint authorization.

## Install

```bash
dotnet add package Gatekeeper.Infrastructure --prerelease
```

For ASP.NET Core endpoint authorization, also install:

```bash
dotnet add package Gatekeeper.AspNetCore --prerelease
```

Both packages reference `Gatekeeper.Domain` at the same package version.

## Quick start

Use a .NET 10 ASP.NET Core host with the packages below. Connect your host's existing login flow to cookie authentication and issue a trusted `ClaimTypes.NameIdentifier` claim for each user.

`appsettings.json`:

```json
{
  "ConnectionStrings": { "Redis": "localhost:6379" },
  "QueueSettings": { "MaxActivePasses": 50, "MaxWaitingUsers": 0, "MaxConcurrentAdmissions": 1 },
  "JwtSettings": { "Issuer": "your-admission-issuer", "Audience": "your-admission-consumers" }
}
```

Supply a Base64 signing key of at least 32 random bytes through configuration. For a temporary development key in PowerShell:

```powershell
$env:JwtSettings__SecretKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

`Program.cs`:

```csharp
using System.Security.Claims;
using Gatekeeper.AspNetCore;
using Gatekeeper.Domain.Abstractions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser().RequireClaim(ClaimTypes.NameIdentifier).Build());
builder.Services.AddGatekeeper(builder.Configuration);
builder.Services.AddGatekeeperAdmission(options =>
    options.UserIdResolver = user => user.FindFirstValue(ClaimTypes.NameIdentifier));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapPost("/queue", (ClaimsPrincipal user, IWaitingQueue queue, CancellationToken cancellationToken) =>
    queue.EnqueueAsync(user.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken))
    .RequireAuthorization();
app.MapGet("/admission", async (ClaimsPrincipal user, IAdmissionReader reader, CancellationToken cancellationToken) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
    return new
    {
        Status = (await reader.GetStatusAsync(userId, cancellationToken)).ToString(),
        Pass = await reader.GetPassAsync(userId, cancellationToken)
    };
}).RequireAuthorization();
app.MapGet("/protected", () => Results.Ok("Admitted")).RequireGatekeeperAdmission();
app.Run();
```

Run the host, authenticate through your login flow, then POST `/queue` and poll GET `/admission` with backoff. Send the returned pass in the `X-Gatekeeper-Admission` header, together with your host authentication, when calling `/protected`. Status and notifications are informational; only `AdmissionValidationStatus.Valid` authorizes admission. Treat pass responses as private and prevent public caching.

## Admission lifecycle

```text
Waiting --Claim--> Processing --Commit--> Active
   ^                   |                   |--Release--> Released
   |--Recovery---------|                   |--expiry---> Expired
      expired lease; original position restored
```

The Worker reserves capacity, signs one JWT and commits its exact bytes in Redis. Recovery restores only a matching expired reservation. Transport failure is uncertain; retries retain the original attempt and token. Release checks the expected identity so it cannot remove a newer admission.

## ASP.NET Core quick start

The complete sample above uses `AddGatekeeperAdmission` and `RequireGatekeeperAdmission()` with the host's cookie authentication. The default admission header is `X-Gatekeeper-Admission`. Keep the trusted identity resolver aligned with the host's login claims and authorization policies. Use HTTPS and private, non-cacheable pass responses.

## Configuration and operations

Configure `QueueSettings`, `JwtSettings` and an explicit `ConnectionStrings:Redis`, or register a host multiplexer first. The code-based `AddGatekeeper` overload takes a connection string and queue/JWT callbacks, which can bind custom section names. API-only hosts can pass `addWorker: false`; at least one healthy Worker is needed for admission and Recovery.

Redis enforces the lowest live registered capacity for new claims. `CapacityKey` defaults to `gatekeeper:{admission}:capacity`; its TTL is `(2 * ReleaseIntervalSeconds + ProcessingLeaseSeconds) * 1000` milliseconds (150 seconds by default). Lowering takes effect immediately without eviction; occupancy drains naturally. Higher callers cannot raise or refresh a lower record.

For signing-key rotation: first add the NEW key to `AdditionalSecretKeys` on ALL API and Worker hosts; then switch `SecretKey` to NEW and move OLD into the additional list, removing NEW from that list; after `PassDuration` since the last old signature, remove OLD everywhere. Apply the new primary configuration to API hosts too. The first additional key's environment name is `JwtSettings__AdditionalSecretKeys__0`. HS256 is pinned; old tokens without `kid` validate against all configured keys. Redis still requires the exact current token.

## Known limitations

- All state for one queue shares one Redis Cluster slot; Cluster does not shard that queue's writes.
- Waiting entries have no TTL or liveness tracking. Abandoned entries may be admitted until removed.
- Lowering capacity does not revoke existing reservations. With no live capacity record, callers use their own values until registration.
- Pub/Sub wakeups are best effort. Poll with backoff and validate every protected operation.
- Admission authorization is not atomic with downstream business work; consumers own business-operation idempotency.
- Redis loss, eviction, arbitrary writes, acknowledged-write loss during failover and significant clock rollback can undermine authority. Use synchronized clocks, sufficient memory and `noeviction`.
- Deploy JWT rotation in the documented sequence across API and Worker hosts; options are captured at service construction.

## Links and license

- [Repository](https://github.com/abdelrahman-abusleem/Gatekeeper)
- [Full README and operating guidance](https://github.com/abdelrahman-abusleem/Gatekeeper/blob/master/README.md)
- [MIT License](https://github.com/abdelrahman-abusleem/Gatekeeper/blob/master/LICENSE)
