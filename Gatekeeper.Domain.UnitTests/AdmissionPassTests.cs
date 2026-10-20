
using FluentAssertions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Xunit;

namespace Gatekeeper.Domain.UnitTests;

public class AdmissionPassTests
{
    private static readonly DateTime FixedUtcNow =
        new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AdmissionPass CreatePass()
    {
        return AdmissionPass.Create(
            AdmissionId.New(),
            "user_1",
            FixedUtcNow,
            FixedUtcNow.AddMinutes(5));
    }



    [Fact]
    public void Create_WithValidParameters_ShouldInitializeCorrectly()
    {
        var admissionId = AdmissionId.New();
        var userId = "user_123";
        var issuedAtUtc = FixedUtcNow;
        var expiresAtUtc = FixedUtcNow.AddMinutes(5);

        var pass = AdmissionPass.Create(
            admissionId,
            userId,
            issuedAtUtc,
            expiresAtUtc);

        pass.AdmissionId.Should().Be(admissionId);
        pass.UserId.Should().Be(userId);
        pass.IssuedAtUtc.Should().Be(issuedAtUtc);
        pass.ExpiresAtUtc.Should().Be(expiresAtUtc);
        pass.Status.Should().Be(PassStatus.Active);
    }

    [Fact]
    public void Create_WithEmptyAdmissionId_ShouldThrow()
    {
        var act = () => AdmissionPass.Create(
            default(AdmissionId),
            "user_1",
            FixedUtcNow,
            FixedUtcNow.AddMinutes(5));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("admissionId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespaceUserId_ShouldThrowArgumentException(
        string? invalidUserId)
    {
        var act = () => AdmissionPass.Create(
            AdmissionId.New(),
            invalidUserId!,
            FixedUtcNow,
            FixedUtcNow.AddMinutes(5));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("userId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public void Create_WithInvalidExpiration_ShouldThrow(
        int minutes)
    {
        var issuedAtUtc = FixedUtcNow;
        var expiresAtUtc = issuedAtUtc.AddMinutes(minutes);

        var act = () => AdmissionPass.Create(
            AdmissionId.New(),
            "user_1",
            issuedAtUtc,
            expiresAtUtc);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("expiresAtUtc");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_WithNonUtcIssuedAt_ShouldThrow(
        DateTimeKind kind)
    {
        var invalidIssuedAt = DateTime.SpecifyKind(
            FixedUtcNow,
            kind);

        var act = () => AdmissionPass.Create(
            AdmissionId.New(),
            "user_1",
            invalidIssuedAt,
            FixedUtcNow.AddMinutes(5));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_WithNonUtcExpiresAt_ShouldThrow(
        DateTimeKind kind)
    {
        var invalidExpiresAt = DateTime.SpecifyKind(
            FixedUtcNow.AddMinutes(5),
            kind);

        var act = () => AdmissionPass.Create(
            AdmissionId.New(),
            "user_1",
            FixedUtcNow,
            invalidExpiresAt);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithDifferentAdmissionIds_ShouldPreserveEachIdentity()
    {
        var firstId = AdmissionId.New();
        var secondId = AdmissionId.New();

        var firstPass = AdmissionPass.Create(
            firstId,
            "user_1",
            FixedUtcNow,
            FixedUtcNow.AddMinutes(5));

        var secondPass = AdmissionPass.Create(
            secondId,
            "user_1",
            FixedUtcNow,
            FixedUtcNow.AddMinutes(5));

        firstPass.AdmissionId.Should().Be(firstId);
        secondPass.AdmissionId.Should().Be(secondId);

        firstPass.AdmissionId.Should().NotBe(secondPass.AdmissionId);
    }



    [Fact]
    public void IsExpired_WhenCurrentTimeIsBeforeExpiration_ShouldReturnFalse()
    {
        var pass = CreatePass();

        pass.IsExpired(FixedUtcNow.AddMinutes(2))
            .Should().BeFalse();
    }

    [Fact]
    public void IsExpired_WhenCurrentTimeIsAtOrAfterExpiration_ShouldReturnTrue()
    {
        var pass = CreatePass();

        pass.IsExpired(FixedUtcNow.AddMinutes(5))
            .Should().BeTrue();

        pass.IsExpired(FixedUtcNow.AddMinutes(6))
            .Should().BeTrue();
    }

    [Fact]
    public void RemainingTime_WhenValid_ShouldReturnRemainingDuration()
    {
        var pass = CreatePass();

        var remaining = pass.RemainingTime(
            FixedUtcNow.AddMinutes(3));

        remaining.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void RemainingTime_WhenExpired_ShouldReturnZero()
    {
        var pass = CreatePass();

        var remaining = pass.RemainingTime(
            FixedUtcNow.AddMinutes(10));

        remaining.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void IsExpired_WithNonUtcTime_ShouldThrow(
        DateTimeKind kind)
    {
        var pass = CreatePass();

        var invalidTime = DateTime.SpecifyKind(
            FixedUtcNow.AddMinutes(2),
            kind);

        var act = () => pass.IsExpired(invalidTime);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("currentUtc");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void RemainingTime_WithNonUtcTime_ShouldThrow(
        DateTimeKind kind)
    {
        var pass = CreatePass();

        var invalidTime = DateTime.SpecifyKind(
            FixedUtcNow.AddMinutes(2),
            kind);

        var act = () => pass.RemainingTime(invalidTime);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("currentUtc");
    }

    [Fact]
    public void IsExpired_WhenStatusIsStillActiveButTimePassed_ShouldReturnTrue()
    {
        var pass = CreatePass();

        pass.Status.Should().Be(PassStatus.Active);

        pass.IsExpired(FixedUtcNow.AddMinutes(10))
            .Should().BeTrue();

        // Expiration is based on time, even if the
        // local status has not been updated.
        pass.Status.Should().Be(PassStatus.Active);
    }


    [Fact]
    public void Revoke_ActivePassBeforeExpiry_ShouldBecomeRevoked()
    {
        var pass = CreatePass();

        var revoked = pass.Revoke(
            FixedUtcNow.AddMinutes(1));

        revoked.Status.Should().Be(PassStatus.Revoked);
        pass.Status.Should().Be(PassStatus.Active);

        revoked.AdmissionId.Should().Be(pass.AdmissionId);
        revoked.UserId.Should().Be(pass.UserId);
        revoked.IssuedAtUtc.Should().Be(pass.IssuedAtUtc);
        revoked.ExpiresAtUtc.Should().Be(pass.ExpiresAtUtc);
    }

    [Fact]
    public void Revoke_WhenCalledTwice_ShouldRemainRevoked()
    {
        var pass = CreatePass();

        var revoked = pass.Revoke(
            FixedUtcNow.AddMinutes(1));

        var revokedAgain = revoked.Revoke(
            FixedUtcNow.AddMinutes(2));

        revokedAgain.Status.Should().Be(PassStatus.Revoked);

        Assert.Same(revoked, revokedAgain);
    }

    [Fact]
    public void Revoke_WhenPassAlreadyExpiredByTime_ShouldBecomeExpired()
    {
        var pass = CreatePass();

        var result = pass.Revoke(
            FixedUtcNow.AddMinutes(5));

        result.Status.Should().Be(PassStatus.Expired);
    }

    [Fact]
    public void Revoke_WhenPassIsAlreadyExpired_ShouldRemainExpired()
    {
        var pass = CreatePass();

        var expired = pass.Expire(
            FixedUtcNow.AddMinutes(5));

        var result = expired.Revoke(
            FixedUtcNow.AddMinutes(6));

        result.Status.Should().Be(PassStatus.Expired);

        Assert.Same(expired, result);
    }

    [Fact]
    public void Revoke_WhenAlreadyRevokedAndTimeLaterExpires_ShouldRemainRevoked()
    {
        var pass = CreatePass();

        var revoked = pass.Revoke(
            FixedUtcNow.AddMinutes(1));

        var result = revoked.Revoke(
            FixedUtcNow.AddMinutes(10));

        result.Status.Should().Be(PassStatus.Revoked);

        Assert.Same(revoked, result);
    }



    [Fact]
    public void Expire_WhenExpirationReached_ShouldBecomeExpired()
    {
        var pass = CreatePass();

        var expired = pass.Expire(
            FixedUtcNow.AddMinutes(5));

        expired.Status.Should().Be(PassStatus.Expired);
        pass.Status.Should().Be(PassStatus.Active);

        expired.AdmissionId.Should().Be(pass.AdmissionId);
        expired.UserId.Should().Be(pass.UserId);
        expired.IssuedAtUtc.Should().Be(pass.IssuedAtUtc);
        expired.ExpiresAtUtc.Should().Be(pass.ExpiresAtUtc);
    }

    [Fact]
    public void Expire_WhenCalledAfterExpiration_ShouldBecomeExpired()
    {
        var pass = CreatePass();

        var expired = pass.Expire(
            FixedUtcNow.AddMinutes(10));

        expired.Status.Should().Be(PassStatus.Expired);
    }

    [Fact]
    public void Expire_BeforeExpiration_ShouldThrow()
    {
        var pass = CreatePass();

        var act = () => pass.Expire(
            FixedUtcNow.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();

        pass.Status.Should().Be(PassStatus.Active);
    }

    [Fact]
    public void Expire_WhenCalledTwice_ShouldRemainExpired()
    {
        var pass = CreatePass();

        var expired = pass.Expire(
            FixedUtcNow.AddMinutes(5));

        var expiredAgain = expired.Expire(
            FixedUtcNow.AddMinutes(10));

        expiredAgain.Status.Should().Be(PassStatus.Expired);

        Assert.Same(expired, expiredAgain);
    }

    [Fact]
    public void Expire_WhenAlreadyRevoked_ShouldRemainRevoked()
    {
        var pass = CreatePass();

        var revoked = pass.Revoke(
            FixedUtcNow.AddMinutes(1));

        var result = revoked.Expire(
            FixedUtcNow.AddMinutes(10));

        result.Status.Should().Be(PassStatus.Revoked);

        Assert.Same(revoked, result);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Expire_WithNonUtcTime_ShouldThrow(
        DateTimeKind kind)
    {
        var pass = CreatePass();

        var invalidTime = DateTime.SpecifyKind(
            FixedUtcNow.AddMinutes(5),
            kind);

        var act = () => pass.Expire(invalidTime);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("currentUtc");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Revoke_WithNonUtcTime_ShouldThrow(
        DateTimeKind kind)
    {
        var pass = CreatePass();

        var invalidTime = DateTime.SpecifyKind(
            FixedUtcNow.AddMinutes(1),
            kind);

        var act = () => pass.Revoke(invalidTime);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("currentUtc");
    }



    [Fact]
    public void RevokedPass_CannotReturnToActive()
    {
        var pass = CreatePass();

        var revoked = pass.Revoke(
            FixedUtcNow.AddMinutes(1));

        var result = revoked.Expire(
            FixedUtcNow.AddMinutes(10));

        result.Status.Should().Be(PassStatus.Revoked);
        revoked.Status.Should().NotBe(PassStatus.Active);
    }

    [Fact]
    public void ExpiredPass_CannotReturnToActive()
    {
        var pass = CreatePass();

        var expired = pass.Expire(
            FixedUtcNow.AddMinutes(5));

        var result = expired.Revoke(
            FixedUtcNow.AddMinutes(10));

        result.Status.Should().Be(PassStatus.Expired);
        expired.Status.Should().NotBe(PassStatus.Active);
    }
}
