using System.Globalization;
using System.Text.Json;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;
using static Gatekeeper.Infrastructure.Services.RedisProtocolLimits;

namespace Gatekeeper.Infrastructure.Services;

internal static class AdmissionRecordReader
{
    internal static bool TryReadClaim(
        string userId,
        RedisValue raw,
        double? lease,
        out AdmissionClaim? claim)
    {
        claim = null;

        if (raw.IsNull ||
            lease is null ||
            !double.IsFinite(lease.Value) ||
            lease <= 0 ||
            lease > MaxUnixMilliseconds ||
            lease % 1 != 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw.ToString());

            var record = document.RootElement;

            if (record.ValueKind != JsonValueKind.Object ||
                !record.TryGetProperty(AdmissionRecordFields.UserId, out var user) ||
                user.ValueKind != JsonValueKind.String ||
                user.GetString() != userId ||
                !record.TryGetProperty(AdmissionRecordFields.AdmissionId, out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var guid) ||
                guid == Guid.Empty ||
                !record.TryGetProperty(AdmissionRecordFields.OriginalScore, out var score) ||
                score.ValueKind != JsonValueKind.String ||
                !long.TryParse(
                    score.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var original) ||
                original < 1 ||
                original > MaxExactScore)
            {
                return false;
            }

            claim = new AdmissionClaim(
                new AdmissionId(guid),
                userId,
                original,
                DateTimeOffset
                    .FromUnixTimeMilliseconds((long)lease.Value)
                    .UtcDateTime);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
