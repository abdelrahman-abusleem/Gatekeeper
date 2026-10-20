using System.Text.RegularExpressions;
using Gatekeeper.Infrastructure.Diagnostics;
using Gatekeeper.Infrastructure.Services;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class FinalPassStatusContractTests
{
    public static TheoryData<string, IReadOnlyList<string>> Scripts => new()
    {
        { "ClaimNextUser.lua", ClaimStatuses.All },
        { "CommitAdmission.lua", CommitStatuses.All },
        { "GetCommitStatus.lua", CommitOutcomeStatuses.All },
        { "ReleaseAdmission.lua", ReleaseStatuses.All },
        { "RecoverAdmission.lua", RecoveryStatuses.All },
        { "EnqueueUser.lua", EnqueueStatuses.All },
        { "GetAdmissionSnapshot.lua", SnapshotStatuses.All }
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void ReturnedStatusLiterals_ExactlyMatchCSharpContract(string script, IReadOnlyList<string> expected)
    {
        var source = RedisScriptSource.Load(script).Value;
        var actual = Regex.Matches(source, @"return\s*\{\s*'(?<status>[A-Z_]+)'")
            .Select(match => match.Groups["status"].Value).ToHashSet(StringComparer.Ordinal);
        if (script == "ClaimNextUser.lua")
        {
            // The one dynamic return replays only these explicitly guarded tombstone outcomes.
            var guardedReplay = Regex.Match(source, @"if (?<guard>attempt\.status[^\r\n]+) then\s*return\s*\{\s*attempt\.status\s*\}");
            Assert.True(guardedReplay.Success, "ClaimNextUser.lua: dynamic status replay guard changed; review its contract.");
            actual.UnionWith(Regex.Matches(guardedReplay.Groups["guard"].Value, @"attempt\.status\s*==\s*'([^']+)'")
                .Select(match => match.Groups[1].Value));
        }
        var missing = actual.Except(expected).Order().ToArray();
        var extra = expected.Except(actual).Order().ToArray();
        Assert.True(missing.Length == 0 && extra.Length == 0,
            $"{script}: missing C# statuses [{string.Join(", ", missing)}]; extra C# statuses [{string.Join(", ", extra)}].");
        Assert.Equal(expected.Count, expected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SnapshotModes_ExactlyMatchLuaModeChecks()
    {
        var source = RedisScriptSource.Load("GetAdmissionSnapshot.lua").Value;
        var modes = Regex.Matches(source, @"mode\s*(?:==|~=)\s*'([^']+)'")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(modes.SetEquals(SnapshotModes.All), "GetAdmissionSnapshot.lua: C#/Lua snapshot modes differ.");
    }

    [Fact]
    public void NonStatusGroups_AreExplicitAndUnique()
    {
        Assert.Equal(new[] { "userId", "admissionId", "originalScore" }, AdmissionRecordFields.All);
        Assert.Equal(new[] { "ready" }, AdmissionNotificationPayloads.All);
        Assert.Equal(new[] { "operation", "category", "reason" }, AdmissionMetricTags.All);
        foreach (var group in new[] { AdmissionMetricCategories.All, AdmissionMetricReasons.All,
                     AdmissionMetricOperations.All, AdmissionMetrics.TokenRejectionReason.All })
            Assert.Equal(group.Count, group.Distinct(StringComparer.Ordinal).Count());
    }
}
