# Gatekeeper

Gatekeeper provides Redis-backed virtual waiting rooms and controlled admission for distributed .NET applications.

Applications enqueue trusted user identities; background Workers reserve capacity, issue JWT admission passes, and commit admission state in Redis.

The project is open source under the [MIT License](LICENSE).

## Features and architecture

- FIFO selection from Waiting using Redis sequence scores; Recovery restores original queue position.
- Atomic Redis/Lua Claim, Commit, Recovery and identity-fenced Release.
- Capacity checks covering live Active passes and Processing reservations.
- Configurable queue limits, Worker batches, admission concurrency and retry/backoff.
- Signed JWT passes with stateful validation against the exact current Redis admission.
- Optional ASP.NET Core admission authorization alongside the host's authentication.

| Project | Responsibility |
| --- | --- |
| `Gatekeeper.Domain` | Public interfaces, identifiers, models and result enums |
| `Gatekeeper.Infrastructure` | Redis/Lua protocol, JWT services, notifications and background Workers; references Domain |
| `Gatekeeper.AspNetCore` | Optional endpoint authorization; references Domain and ASP.NET Core |

Redis is authoritative shared state. Consumer hosts own authentication, identity authorization, routes and configuration.

FIFO selection does not imply concurrent signing/Commit completion order, and admission does not guarantee exactly-once business execution.

## Requirements

- .NET 10 SDK
- Redis 7.0 or later
- Docker with Linux containers to run the Redis-backed test suite

Redis should be protected with appropriate network access controls and configured with `noeviction`.

The Lua scripts require `PEXPIRETIME`, available from Redis 7.0.

## Getting started

Clone the repository:

```bash
git clone https://github.com/abdelrahman-abusleem/Gatekeeper.git
cd Gatekeeper
```

Reference the relevant Gatekeeper projects from your .NET application:

```bash
dotnet add YourApp.csproj reference Gatekeeper/Gatekeeper.Infrastructure/Gatekeeper.Infrastructure.csproj
```

For optional ASP.NET Core admission authorization:

```bash
dotnet add YourWebApp.csproj reference Gatekeeper/Gatekeeper.AspNetCore/Gatekeeper.AspNetCore.csproj
```

Adjust the relative paths to match the location of your application and the cloned repository.

## Configuration

Configure Redis, admission limits and JWT settings in the consuming application's configuration.

For example, in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "QueueSettings": {
    "MaxActivePasses": 50,
    "MaxWaitingUsers": 0,
    "MaxConcurrentAdmissions": 1
  },
  "JwtSettings": {
    "Issuer": "your-admission-issuer",
    "Audience": "your-admission-consumers"
  }
}
```

These values are illustrative, and the queue values shown match the existing defaults. They are not deployment sizing recommendations.

Configure them according to your environment:

- **Redis:** Use the actual Redis endpoint. `localhost:6379` is suitable for local development.
- **MaxActivePasses:** Maximum shared capacity for Active admissions and Processing reservations.
- **MaxWaitingUsers:** Maximum waiting queue size; `0` means no configured waiting limit.
- **MaxConcurrentAdmissions:** Admission-processing concurrency within each Worker.
- **Issuer and Audience:** JWT values that must match between the signing and validating applications.

A JWT signing secret is also required. Supply `JwtSettings:SecretKey` through a secure configuration provider, such as the environment variable `JwtSettings__SecretKey`.

The value must be Base64-encoded and represent at least 32 cryptographically random bytes.

Never commit the secret to source control or log it.

Signers and validators must share the intended secret, issuer, audience and Redis namespace.

## Service registration

Register Gatekeeper in your .NET Generic Host or ASP.NET Core application:

```csharp
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddInfrastructure(builder.Configuration);
```

This registers the infrastructure services and the background Worker.

For an API process where Workers run in a separate host:

```csharp
builder.Services.AddInfrastructure(
    builder.Configuration,
    addWorker: false);
```

Choose the registration strategy deliberately.

At least one healthy Worker host is required to process admissions and perform Recovery. API-only hosts still need valid JWT configuration.

Service registration itself does not connect to Redis. Resolving the shared multiplexer connects synchronously, so Worker startup or an initial API request may wait for the configured connection timeout.

Connection and reconnection policies remain controlled by the consuming host through StackExchange.Redis configuration.

## Basic usage

The consuming application must establish the user's identity through its own authentication and authorization process.

Do not treat a user ID supplied directly in a request as proof of ownership.

Inside an already-started application host:

```csharp
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

var queue = host.Services.GetRequiredService<IWaitingQueue>();
var admissions = host.Services.GetRequiredService<IAdmissionClaimStore>();

var position = await queue.EnqueueAsync(
    trustedUserId,
    cancellationToken);

var status = await admissions.GetStatusAsync(
    trustedUserId,
    cancellationToken);

var pass = await admissions.GetPassAsync(
    trustedUserId,
    cancellationToken);

if (pass is not null &&
    await admissions.ValidateAdmissionAsync(
        pass,
        trustedUserId,
        cancellationToken) == AdmissionValidationStatus.Valid)
{
    // Admission is currently authorized.
    // Apply the application's business rules before proceeding.
}
```

An admission pass may not be available immediately after enqueueing.

Applications should poll with cancellation and appropriate backoff. Queue status and notifications are informational; they do not authorize admission.

A positive waiting limit rejects new entrants with `WaitingQueueFullException`. Redis failures and uncertain outcomes must be handled separately.

`RemoveAsync` removes Waiting entries only. It does not cancel Processing or revoke Active admission.

`MaxActivePasses` controls shared capacity for Active plus Processing admissions, while `MaxConcurrentAdmissions` controls processing lanes inside each Worker.

## ASP.NET Core integration

Gatekeeper provides optional ASP.NET Core admission authorization.

The consuming application remains responsible for authentication and determining the trusted user identity.

Configure a trusted `UserIdResolver` and use `RequireGatekeeperAdmission()` on endpoints that require an active admission.

The admission check complements the application's existing authentication and business authorization; it does not replace them.

## Security and operating assumptions

- Validate admission using `IAdmissionClaimStore.ValidateAdmissionAsync`. Only `AdmissionValidationStatus.Valid` authorizes admission.
- Cryptographic JWT validation alone, Active status, or ready notifications are insufficient.
- Keep application authentication, identity ownership and business authorization separate from admission control.
- Use HTTPS when transporting admission passes.
- Do not log bearer tokens or publicly cache admission-pass responses.
- Apply request-forgery protections appropriate to the application's authentication scheme.
- Protect Redis with network controls, authentication/ACLs and TLS as appropriate.
- Use managed secrets, synchronized clocks, consistent fleet configuration, sufficient memory and `noeviction`.
- Fail closed on Redis outages.
- Account for storage loss, eviction, arbitrary Redis modifications, acknowledged-write loss during failover and significant clock rollback.
- Consumers remain responsible for downstream business-operation idempotency and coordinated JWT key rotation.

Admission validation is not atomic with a downstream business transaction. Releasing an admission cannot cancel a business operation that was already authorized and started.

The Worker derives JWT issue and expiry times from Redis time returned by the atomic Claim, rounded down to whole seconds, and preserves those values across retries.

Validators allow up to five seconds of clock lag for `nbf` only. The exclusive `now < exp` expiry boundary is enforced without tolerance, followed by Redis-backed admission validation.

UUIDv7 initiation also depends on the host clock, so clock synchronization remains necessary.

Lua scripts are embedded in Infrastructure for runtime execution, including executable-only single-file deployments. Copied Lua files are retained for compatibility and inspection; editing those loose files does not modify the embedded protocol.

Dependencies retain their respective license terms, including the test-only FluentAssertions license.

## Build and test

From the repository root:

```bash
dotnet restore Gatekeeper.slnx
dotnet build Gatekeeper.slnx --configuration Release --no-restore -m:1
dotnet test Gatekeeper.slnx --configuration Release --no-build --no-restore -m:1
```

Run the Redis-backed tests with Docker available in Linux-container mode.

The tests use disposable Redis resources and cover Domain behavior, Workers, Redis/Lua operations, Cluster and failure scenarios, and ASP.NET Core integration.

Do not point test infrastructure at production Redis.

Keep generated output, local credentials and environment-specific configuration out of source control.

## License

Gatekeeper is licensed under the [MIT License](LICENSE).