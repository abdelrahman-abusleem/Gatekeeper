using System.Diagnostics.Metrics;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gatekeeper.Infrastructure.UnitTests;

[Collection("Phase05 isolated measurements")]
public sealed class FinalPassJwtPolicyTests
{
    private static readonly DateTime Issue = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-5001, false)]
    [InlineData(-5000, true)]
    [InlineData(-4999, true)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(299999, true)]
    [InlineData(300000, false)]
    [InlineData(300001, false)]
    public async Task NotBeforeTolerance_DoesNotExtendExclusiveExpiry(int milliseconds, bool accepted)
    {
        var service = Service(Issue.AddMilliseconds(milliseconds));
        var token = service.GenerateToken(new(AdmissionId.New(), "private-user", Issue, Issue.AddMinutes(5)));
        Assert.Equal(accepted, await service.ValidateTokenAsync(token) is not null);
    }

    [Theory]
    [InlineData("empty", "signature_or_format")]
    [InlineData("format", "signature_or_format")]
    [InlineData("signature", "signature_or_format")]
    [InlineData("expired", "lifetime")]
    [InlineData("claims", "claims")]
    [InlineData("timestamps", "claims")]
    [InlineData("type", "type")]
    public async Task RejectedToken_EmitsExactlyOneBoundedReason(string fault, string expectedReason)
    {
        var samples = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, source) =>
        {
            if (instrument.Meter.Name == "Gatekeeper.Admission" && instrument.Name == "gatekeeper.token.rejected")
                source.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => samples.Add((value, tags.ToArray())));
        listener.Start();
        var service = Service(Issue.AddSeconds(10));
        var settings = RefactorJwtTests.Settings();
        var key = Convert.FromBase64String(settings.SecretKey);
        if (fault == "signature") key[0]++;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "private-user",
            ["jti"] = AdmissionId.New().ToString(),
            ["type"] = fault == "type" ? "login" : "admission_pass"
        };
        if (fault == "claims") claims.Remove("sub");
        var token = fault switch
        {
            "empty" => "",
            "format" => "invalid.jwt",
            _ => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Claims = claims,
                Issuer = settings.Issuer,
                Audience = settings.Audience,
                IssuedAt = Issue,
                NotBefore = fault == "timestamps" ? Issue.AddSeconds(1) : Issue,
                Expires = fault == "expired" ? Issue.AddSeconds(10) : Issue.AddMinutes(5),
                SigningCredentials = new(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
            })
        };
        Assert.Null(await service.ValidateTokenAsync(token));
        var sample = Assert.Single(samples);
        Assert.Equal(1, sample.Count);
        var tag = Assert.Single(sample.Tags);
        Assert.Equal("reason", tag.Key);
        Assert.Equal(expectedReason, tag.Value);
        samples.Clear();
        var valid = service.GenerateToken(new(AdmissionId.New(), "private-user", Issue, Issue.AddMinutes(5)));
        Assert.NotNull(await service.ValidateTokenAsync(valid));
        Assert.Empty(samples);
    }

    private static JwtAdmissionTokenService Service(DateTime now) => new(
        Options.Create(RefactorJwtTests.Settings()), NullLogger<JwtAdmissionTokenService>.Instance, new FixedClock(now));

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
