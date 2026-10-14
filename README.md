# Gatekeeper

A C# / .NET 10 prototype exploring admission control and waiting-queue domain design.

## Current implementation

- Immutable admission-pass model with validated construction, expiration checks, and remaining-time calculation.
- Queue-position domain model and waiting-queue abstraction.
- Domain unit tests using xUnit and FluentAssertions, including invalid inputs and expiration boundaries.
- Separate domain and infrastructure projects, with Redis dependency and options wiring started.

## Project structure

```text
Gatekeeper.Domain/           Domain models and abstractions
Gatekeeper.Domain.UnitTests/ Domain behavior tests
Gatekeeper.Infrastructure/   Redis queue infrastructure (in progress)
```

## Run the domain tests

Prerequisite: .NET 10 SDK. From the repository root:

```sh
dotnet restore Gatekeeper.Domain.UnitTests/Gatekeeper.Domain.UnitTests.csproj
dotnet test Gatekeeper.Domain.UnitTests/Gatekeeper.Domain.UnitTests.csproj
```

The tests target the domain project independently of the unfinished infrastructure. These commands describe the intended workflow; no test results are claimed here.

## Development status

This repository is a work in progress. `RedisWaitingQueue` currently contains dependency/configuration wiring; queue operations are not implemented yet. The repository does not currently include an HTTP API host or a deployed service.

## Next steps

- Implement waiting-queue operations and add Redis integration tests.
- Add an API host and admission-control endpoints.
- Add a repeatable local environment and CI validation.
