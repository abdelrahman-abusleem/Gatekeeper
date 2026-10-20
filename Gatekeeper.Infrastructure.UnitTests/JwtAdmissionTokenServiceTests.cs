
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gatekeeper.Infrastructure.UnitTests;

public class JwtAdmissionTokenServiceTests
{
    [Fact]
    public async Task InvalidToken_ShouldReturnNull()
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var result = await service.ValidateTokenAsync("not-a-valid-jwt");

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidToken_ShouldPreserveAdmissionIdentity()
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        // JWT stores timestamps with seconds precision.
        var issuedAtUtc = UtcNowWithoutMilliseconds();

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            "Ahmed",
            issuedAtUtc,
            issuedAtUtc.AddMinutes(5));

        var token = service.GenerateToken(data);
        var result = await service.ValidateTokenAsync(token);

        Assert.NotNull(result);

        Assert.Equal(data.AdmissionId, result.AdmissionId);
        Assert.Equal(data.UserId, result.UserId);
        Assert.Equal(data.IssuedAtUtc, result.IssuedAtUtc);
        Assert.Equal(data.ExpiresAtUtc, result.ExpiresAtUtc);
    }

    [Fact]
    public async Task TamperedToken_ShouldReturnNull()
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var now = UtcNowWithoutMilliseconds();

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            "Ahmed",
            now,
            now.AddMinutes(5));

        var originalToken = service.GenerateToken(data);

        Assert.NotNull(
            await service.ValidateTokenAsync(originalToken));

        var parts = originalToken.Split('.');
        Assert.Equal(3, parts.Length);

        var payload = JsonNode.Parse(
            Base64UrlEncoder.Decode(parts[1]))!.AsObject();

        payload["sub"] = "AnotherUser";

        var modifiedPayload = Base64UrlEncoder.Encode(
            payload.ToJsonString());

        // Keep the original signature after changing the payload.
        var tamperedToken =
            $"{parts[0]}.{modifiedPayload}.{parts[2]}";

        var result = await service.ValidateTokenAsync(tamperedToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenSignedWithDifferentKey_ShouldReturnNull()
    {
        var validationKey = RandomNumberGenerator.GetBytes(32);
        var signingKey = RandomNumberGenerator.GetBytes(32);

        var service = CreateTestService(validationKey);

        var token = CreateSignedTestToken(
            signingKey,
            AdmissionId.New().ToString());

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidSignatureButWrongType_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "Ahmed"),
                new Claim("type", "access_token")
            ]),

            Issuer = "TicketingPlatform",
            Audience = "TicketingClients",

            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),

            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256)
        };

        var token = new JsonWebTokenHandler()
            .CreateToken(descriptor);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task TokenWithInvalidAdmissionId_ShouldReturnNull(
        string? jti)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        // null means the jti claim is absent.
        var token = CreateSignedTestToken(key, jti);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenWithoutSubject_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            subject: null);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenWithoutIssuedAt_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            includeIat: false);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenWithoutNotBefore_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            includeNbf: false);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenWithDifferentIssuedAtAndNotBefore_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            issuedAt: now - 60,
            notBefore: now);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenWithWrongIssuer_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            issuer: "AnotherIssuer");

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task TokenWithWrongAudience_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            audience: "AnotherAudience");

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExpiredToken_ShouldReturnNull()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = CreateTestService(key);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var token = CreateSignedTestToken(
            key,
            AdmissionId.New().ToString(),
            issuedAt: now - 600,
            notBefore: now - 600,
            expiresAt: now - 10);

        var result = await service.ValidateTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public void GenerateToken_WithEmptyAdmissionId_ShouldThrow()
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var now = DateTime.UtcNow;

        var data = new AdmissionTokenData(
            default,
            "Ahmed",
            now,
            now.AddMinutes(5));

        Assert.Throws<ArgumentException>(
            () => service.GenerateToken(data));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GenerateToken_WithInvalidUserId_ShouldThrow(
        string userId)
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var now = DateTime.UtcNow;

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            userId,
            now,
            now.AddMinutes(5));

        Assert.Throws<ArgumentException>(
            () => service.GenerateToken(data));
    }

    [Theory]
    [InlineData(DateTimeKind.Local, true)]
    [InlineData(DateTimeKind.Unspecified, true)]
    [InlineData(DateTimeKind.Local, false)]
    [InlineData(DateTimeKind.Unspecified, false)]
    public void GenerateToken_WithNonUtcDates_ShouldThrow(
        DateTimeKind kind,
        bool invalidIssuedAt)
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var now = DateTime.UtcNow;

        var issuedAt = now;
        var expiresAt = now.AddMinutes(5);

        if (invalidIssuedAt)
        {
            issuedAt = DateTime.SpecifyKind(issuedAt, kind);
        }
        else
        {
            expiresAt = DateTime.SpecifyKind(expiresAt, kind);
        }

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            "Ahmed",
            issuedAt,
            expiresAt);

        Assert.Throws<ArgumentException>(
            () => service.GenerateToken(data));
    }

    [Fact]
    public void GenerateToken_WithExpirationBeforeIssuance_ShouldThrow()
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var now = DateTime.UtcNow;

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            "Ahmed",
            now,
            now.AddMinutes(-1));

        Assert.Throws<ArgumentException>(
            () => service.GenerateToken(data));
    }

    [Fact]
    public void GenerateToken_WithEqualIssuanceAndExpiration_ShouldThrow()
    {
        var service = CreateTestService(
            RandomNumberGenerator.GetBytes(32));

        var now = DateTime.UtcNow;

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            "Ahmed",
            now,
            now);

        Assert.Throws<ArgumentException>(
            () => service.GenerateToken(data));
    }

    [Fact]
    public async Task JwtOperations_ShouldNotLogTokensOrSecrets()
    {
        var secret = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        var options = Options.Create(new JwtOptions
        {
            SecretKey = secret,
            Issuer = "TicketingPlatform",
            Audience = "TicketingClients"
        });

        var logger = new RecordingLogger<JwtAdmissionTokenService>();

        var service = new JwtAdmissionTokenService(
            options,
            logger);

        var now = UtcNowWithoutMilliseconds();

        var data = new AdmissionTokenData(
            AdmissionId.New(),
            "Ahmed",
            now,
            now.AddMinutes(5));

        var token = service.GenerateToken(data);

        var validResult = await service.ValidateTokenAsync(token);

        Assert.NotNull(validResult);

        const string invalidToken = "not-a-valid-jwt";

        var invalidResult = await service.ValidateTokenAsync(invalidToken);

        Assert.Null(invalidResult);

        var loggedContent = string.Join(
            Environment.NewLine,
            logger.Entries);

        Assert.DoesNotContain(token, loggedContent);
        Assert.DoesNotContain(secret, loggedContent);
        Assert.DoesNotContain(invalidToken, loggedContent);
    }

    private static JwtAdmissionTokenService CreateTestService(byte[] key)
    {
        var options = Options.Create(new JwtOptions
        {
            SecretKey = Convert.ToBase64String(key),
            Issuer = "TicketingPlatform",
            Audience = "TicketingClients"
        });

        return new JwtAdmissionTokenService(
            options,
            NullLogger<JwtAdmissionTokenService>.Instance);
    }

    private static DateTime UtcNowWithoutMilliseconds()
    {
        var now = DateTime.UtcNow;

        return new DateTime(
            now.Ticks - now.Ticks % TimeSpan.TicksPerSecond,
            DateTimeKind.Utc);
    }

    private static string CreateSignedTestToken(
        byte[] key,
        string? jti,
        bool includeIat = true,
        bool includeNbf = true,
        long? issuedAt = null,
        long? notBefore = null,
        long? expiresAt = null,
        string? subject = "Ahmed",
        string issuer = "TicketingPlatform",
        string audience = "TicketingClients")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var payload = new JsonObject
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["type"] = "admission_pass",
            ["exp"] = expiresAt ?? now + 300
        };

        if (subject is not null)
        {
            payload["sub"] = subject;
        }

        if (jti is not null)
        {
            payload["jti"] = jti;
        }

        if (includeIat)
        {
            payload["iat"] = issuedAt ?? now;
        }

        if (includeNbf)
        {
            payload["nbf"] = notBefore ?? now;
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(key),
            SecurityAlgorithms.HmacSha256);

        return new JsonWebTokenHandler().CreateToken(
            payload.ToJsonString(),
            credentials);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var property in properties)
                {
                    Entries.Add(property.Value?.ToString() ?? string.Empty);
                }
            }

            if (exception is not null)
            {
                Entries.Add(exception.ToString());
            }
        }
    }
}
