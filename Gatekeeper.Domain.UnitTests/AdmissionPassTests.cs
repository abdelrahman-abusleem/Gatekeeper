using FluentAssertions;
using Gatekeeper.Domain.Models;
using Xunit;

namespace Gatekeeper.Domain.UnitTests;

public class AdmissionPassTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldInitializeCorrectly()
    {
        var userId = "user_123";
        var duration = TimeSpan.FromMinutes(5);
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var pass = AdmissionPass.Create(userId, duration, now);

        pass.UserId.Should().Be(userId);
        pass.IssuedAtUtc.Should().Be(now);
        pass.ExpiresAtUtc.Should().Be(now.Add(duration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespaceUserId_ShouldThrowArgumentException(string? invalidUserId)
    {
        var duration = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;

        var act = () => AdmissionPass.Create(invalidUserId!, duration, now);

        act.Should().Throw<ArgumentException>()
           .WithParameterName("userId");
    }

    [Fact]
    public void Create_WithZeroOrNegativeDuration_ShouldThrowException()
    {
        var now = DateTime.UtcNow;

        var actZero = () => AdmissionPass.Create("user_1", TimeSpan.Zero, now);
        actZero.Should().Throw<ArgumentException>();

        var actNegative = () => AdmissionPass.Create("user_1", TimeSpan.FromMinutes(-1), now);
        actNegative.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsExpired_WhenCurrentTimeIsBeforeExpiration_ShouldReturnFalse()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var pass = AdmissionPass.Create("user_1", TimeSpan.FromMinutes(5), now);

        var testTime = now.AddMinutes(2); 

        pass.IsExpired(testTime).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_WhenCurrentTimeIsAtOrAfterExpiration_ShouldReturnTrue()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var pass = AdmissionPass.Create("user_1", TimeSpan.FromMinutes(5), now);

        var exactExpiry = now.AddMinutes(5);
        var afterExpiry = now.AddMinutes(6); 

        pass.IsExpired(exactExpiry).Should().BeTrue();
        pass.IsExpired(afterExpiry).Should().BeTrue();
    }

    [Fact]
    public void RemainingTime_WhenValid_ShouldReturnRemainingDuration()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var pass = AdmissionPass.Create("user_1", TimeSpan.FromMinutes(5), now);

        var checkTime = now.AddMinutes(3); 

        var remaining = pass.RemainingTime(checkTime);

        remaining.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void RemainingTime_WhenExpired_ShouldReturnZero()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var pass = AdmissionPass.Create("user_1", TimeSpan.FromMinutes(5), now);

        var lateTime = now.AddMinutes(10); 
        
        var remaining = pass.RemainingTime(lateTime);

        remaining.Should().Be(TimeSpan.Zero);
    }
}