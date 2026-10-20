using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Models;

using Gatekeeper.Domain.ValueObjects;
/// <summary>
/// Standalone immutable domain model. The Redis admission runtime does not use this
/// model for persistence, recovery or authorization; Redis remains authoritative.
/// </summary>
public sealed record AdmissionPass
{
    public string UserId { get; }
    public DateTime IssuedAtUtc { get; }
    public DateTime ExpiresAtUtc { get; }
    public AdmissionId AdmissionId { get; }

    public PassStatus Status { get; private init; }

    private AdmissionPass(AdmissionId admissionId, string userId, DateTime issuedAtUtc, DateTime expiresAtUtc)
    {
        AdmissionId = admissionId;
        UserId = userId;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Status = PassStatus.Active;
    }

    public static AdmissionPass Create(
        AdmissionId admissionId,
        string userId,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        if (admissionId.Value == Guid.Empty)
            throw new ArgumentException(
                "AdmissionId cannot be empty.",
                nameof(admissionId));

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (issuedAtUtc.Kind != DateTimeKind.Utc ||
            expiresAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "Admission pass dates must be UTC.");

        if (expiresAtUtc <= issuedAtUtc)
            throw new ArgumentException(
                "Expiration must be after issuance.",
                nameof(expiresAtUtc));

        return new AdmissionPass(
            admissionId,
            userId,
            issuedAtUtc,
            expiresAtUtc);
    }

    public bool IsExpired(DateTime currentUtc)
    {
        EnsureCurrentUtc(currentUtc);
        return currentUtc >= ExpiresAtUtc;
    }

    private static void EnsureCurrentUtc(DateTime currentUtc)
    {
        if (currentUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(
                "Current time must be UTC.",
                nameof(currentUtc));
    }

    public TimeSpan RemainingTime(DateTime currentUtc)
    {
        if (IsExpired(currentUtc))
            return TimeSpan.Zero;

        return ExpiresAtUtc - currentUtc;
    }
    public AdmissionPass Revoke(DateTime currentUtc)
    {
        EnsureCurrentUtc(currentUtc);

        return Status switch
        {
            PassStatus.Revoked or PassStatus.Expired => this,

            PassStatus.Active when currentUtc >= ExpiresAtUtc =>
                this with { Status = PassStatus.Expired },

            PassStatus.Active =>
                this with { Status = PassStatus.Revoked },

            _ => throw new InvalidOperationException(
              $"Cannot revoke a pass with status '{Status}'. " +
             "Only Active, Revoked, and Expired statuses are supported.")
        };
    }

    public AdmissionPass Expire(DateTime currentUtc)
    {
        EnsureCurrentUtc(currentUtc);

        return Status switch
        {
            PassStatus.Expired or PassStatus.Revoked => this,

            PassStatus.Active when currentUtc >= ExpiresAtUtc =>
                this with { Status = PassStatus.Expired },

            PassStatus.Active =>
                throw new InvalidOperationException(
                    "Cannot expire an admission pass before its expiration time."),

            _ => throw new InvalidOperationException(
                $"Cannot expire a pass with status '{Status}'.")
        };
    }
}
