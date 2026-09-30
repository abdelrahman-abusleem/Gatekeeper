namespace Gatekeeper.Domain.Models;


public sealed record AdmissionPass
{
    public string UserId { get; }
    public DateTime IssuedAtUtc { get; }
    public DateTime ExpiresAtUtc { get; }

    private AdmissionPass(string userId, DateTime issuedAtUtc, DateTime expiresAtUtc)
    {
        UserId = userId;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

   
    public static AdmissionPass Create(string userId, TimeSpan validDuration, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId cannot be null or empty.", nameof(userId));

        if (validDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(validDuration), "Validity duration must be greater than zero.");

        var expiresAtUtc = utcNow.Add(validDuration);

        return new AdmissionPass(userId, utcNow, expiresAtUtc);
    }


    public bool IsExpired(DateTime currentUtc) => currentUtc >= ExpiresAtUtc;

  
    public TimeSpan RemainingTime(DateTime currentUtc)
    {
        if (IsExpired(currentUtc))
            return TimeSpan.Zero;

        return ExpiresAtUtc - currentUtc;
    }
}