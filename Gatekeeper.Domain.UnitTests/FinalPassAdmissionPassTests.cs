using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.UnitTests;

public sealed class FinalPassAdmissionPassTests
{
    [Theory]
    [InlineData(false, false, DateTimeKind.Local)]
    [InlineData(false, false, DateTimeKind.Unspecified)]
    [InlineData(false, true, DateTimeKind.Local)]
    [InlineData(false, true, DateTimeKind.Unspecified)]
    [InlineData(true, false, DateTimeKind.Local)]
    [InlineData(true, false, DateTimeKind.Unspecified)]
    [InlineData(true, true, DateTimeKind.Local)]
    [InlineData(true, true, DateTimeKind.Unspecified)]
    public void TerminalTransitions_StillValidateUtcBeforeIdempotentReturn(bool expired, bool revoke, DateTimeKind kind)
    {
        var issued = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var pass = AdmissionPass.Create(AdmissionId.New(), "user", issued, issued.AddMinutes(5));
        var terminal = expired ? pass.Expire(issued.AddMinutes(5)) : pass.Revoke(issued.AddMinutes(1));
        var invalid = DateTime.SpecifyKind(issued.AddMinutes(10), kind);
        var error = Assert.Throws<ArgumentException>(() => revoke ? terminal.Revoke(invalid) : terminal.Expire(invalid));
        Assert.Equal("currentUtc", error.ParamName);
        Assert.Same(terminal, revoke ? terminal.Revoke(issued.AddMinutes(10)) : terminal.Expire(issued.AddMinutes(10)));
    }
}
