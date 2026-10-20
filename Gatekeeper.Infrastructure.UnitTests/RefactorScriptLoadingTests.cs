using Gatekeeper.Infrastructure.Services;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorScriptLoadingTests
{
    [Theory]
    [InlineData("EnqueueUser.lua")]
    [InlineData("ClaimNextUser.lua")]
    [InlineData("CommitAdmission.lua")]
    [InlineData("RecoverAdmission.lua")]
    [InlineData("GetExpiredProcessing.lua")]
    [InlineData("GetAdmissionSnapshot.lua")]
    [InlineData("GetCommitStatus.lua")]
    [InlineData("ReleaseAdmission.lua")]
    public void EveryRuntimeResource_MatchesItsPublishedContractAsset(string name)
    {
        var source = RedisScriptSource.Load(name);
        Assert.False(source.IsValueCreated);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", name)), source.Value);
        Assert.Same(source.Value, source.Value);
    }

    [Fact]
    public void MissingResource_ReportsScriptAndResource_WithoutTypeInitializationWrapping()
    {
        var lazy = RedisScriptSource.Load("Missing.lua");
        var error = Assert.Throws<InvalidOperationException>(() => lazy.Value);
        Assert.Contains("Missing.lua", error.Message);
        Assert.Contains("Gatekeeper.Infrastructure.Scripts.Missing.lua", error.Message);
        Assert.IsNotType<TypeInitializationException>(error);
    }
}
