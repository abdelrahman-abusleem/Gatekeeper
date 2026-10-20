using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorPassSnapshotTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPass_DoesNotReturnSnapshotReleasedOrReplacedDuringValidation(bool replace)
    {
        var c = new Phase03Context(fixture);
        var (claim, originalToken, _) = await Phase04PollingTests.ActivateAsync(c);
        string? replacementToken = null;
        var tokens = new CallbackTokens(c.Tokens, async () =>
        {
            await c.Store.ReleaseAsync("user", claim.AdmissionId);
            if (replace) (_, replacementToken, _) = await Phase04PollingTests.ActivateAsync(c);
        });
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, tokens);

        Assert.Null(await store.GetPassAsync("user"));
        Assert.Equal(replacementToken, await c.Store.GetPassAsync("user"));
        if (replace) Assert.NotEqual(originalToken, replacementToken);
    }

    private sealed class CallbackTokens(IAdmissionTokenService inner, Func<Task> callback) : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => inner.GenerateToken(data);
        public async Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            var result = await inner.ValidateTokenAsync(token, cancellationToken);
            await callback();
            return result;
        }
    }
}
