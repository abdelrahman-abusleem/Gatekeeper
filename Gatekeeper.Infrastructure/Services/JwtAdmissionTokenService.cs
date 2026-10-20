using System.Security.Claims;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Gatekeeper.Domain.ValueObjects;


namespace Gatekeeper.Infrastructure.Services;

public sealed class JwtAdmissionTokenService : IAdmissionTokenService
{
    internal const int NotBeforeToleranceSeconds = 5;
    private const string TypeClaimName = "type";
    private const string AdmissionPassClaimValue = "admission_pass";

    private readonly string _issuer;
    private readonly string _audience;
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly SymmetricSecurityKey _signingKey;
    private readonly SigningCredentials _credentials;
    private readonly TokenValidationParameters _validationParameters;
    private readonly TimeProvider _clock;
    private readonly ILogger<JwtAdmissionTokenService> _logger;

    public JwtAdmissionTokenService(
        IOptions<JwtOptions> options,
        ILogger<JwtAdmissionTokenService> logger)
        : this(options, logger, TimeProvider.System) { }

    internal JwtAdmissionTokenService(IOptions<JwtOptions> options,
        ILogger<JwtAdmissionTokenService> logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        ArgumentNullException.ThrowIfNull(clock);
        // IOptions is a startup policy, not a live rotation mechanism. Snapshot the entire
        // signing/validation policy consistently; never mutate the shared validation parameters.
        var settings = options.Value;
        _issuer = settings.Issuer;
        _audience = settings.Audience;
        _clock = clock;
        _logger = logger;
        _signingKey = new SymmetricSecurityKey(
            Convert.FromBase64String(settings.SecretKey));
        _credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        _validationParameters = BuildValidationParameters();
    }

    public string GenerateToken(AdmissionTokenData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        AdmissionGuards.EnsureAdmissionId(data.AdmissionId, nameof(data));

        if (string.IsNullOrWhiteSpace(data.UserId))
            throw new ArgumentException(
                "UserId cannot be empty.",
                nameof(data));

        AdmissionGuards.EnsureUtc(data.IssuedAtUtc, nameof(data), "Token dates must be UTC.");
        AdmissionGuards.EnsureUtc(data.ExpiresAtUtc, nameof(data), "Token dates must be UTC.");

        if (data.ExpiresAtUtc <= data.IssuedAtUtc)
            throw new ArgumentException(
                "Expiration must be after issuance.",
                nameof(data));

        var claims = new[]
        {
        new Claim(JwtRegisteredClaimNames.Sub, data.UserId),
        new Claim(
            JwtRegisteredClaimNames.Jti,
            data.AdmissionId.ToString()),
        new Claim(TypeClaimName, AdmissionPassClaimValue)
    };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            IssuedAt = data.IssuedAtUtc,
            NotBefore = data.IssuedAtUtc,
            Expires = data.ExpiresAtUtc,
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = _credentials
        };

        var token = _tokenHandler.CreateToken(tokenDescriptor);

        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug(
            "Admission token generated, expires at {ExpiresAtUtc}",
            data.ExpiresAtUtc);

        return token;
    }

    /// <remarks>
    /// Cancellation is checked before and after validation. The underlying JsonWebTokenHandler does not accept
    /// a CancellationToken, so cancellation cannot interrupt the validation step itself.
    /// </remarks>
    public async Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return Reject(AdmissionMetrics.TokenRejectionReason.SignatureOrFormat);

        try
        {
            var result = await _tokenHandler.ValidateTokenAsync(token, _validationParameters);
            cancellationToken.ThrowIfCancellationRequested();

            if (!result.IsValid)
            {
                _logger.LogDebug(
                    "Admission token validation failed: {Reason}",
                    result.Exception?.GetType().Name ?? "Unknown");

                return Reject(RejectionReason(result.Exception));
            }

            if (!HasAdmissionPassType(result.ClaimsIdentity))
                return Reject(AdmissionMetrics.TokenRejectionReason.Type);


            if (!TryExtractUserId(result.ClaimsIdentity, out var userId))
                return Reject(AdmissionMetrics.TokenRejectionReason.Claims);

            if (!TryExtractAdmissionId(
            result.ClaimsIdentity,
            out var admissionId))
            {
                return Reject(AdmissionMetrics.TokenRejectionReason.Claims);
            }

            if (!TryValidateTimestamps(
                    result.SecurityToken,
                    out var issuedAt,
                    out var expiresAt))
            {
                return Reject(AdmissionMetrics.TokenRejectionReason.Claims);
            }

            return new AdmissionTokenData(
             admissionId,
             userId,
             issuedAt,
             expiresAt
             );

        }
        catch (SecurityTokenException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug(
                "Admission token failed security validation: {Reason}",
                ex.GetType().Name);

            return Reject(RejectionReason(ex));
        }
    }

    private static AdmissionTokenData? Reject(string reason)
    {
        AdmissionMetrics.TokenRejected.Add(1, new KeyValuePair<string, object?>(AdmissionMetricTags.Reason, reason));
        return null;
    }

    private static string RejectionReason(Exception? error) => error switch
    {
        SecurityTokenExpiredException or SecurityTokenNotYetValidException
            or SecurityTokenInvalidLifetimeException or SecurityTokenNoExpirationException
            => AdmissionMetrics.TokenRejectionReason.Lifetime,
        SecurityTokenInvalidIssuerException or SecurityTokenInvalidAudienceException
            => AdmissionMetrics.TokenRejectionReason.Claims,
        _ => AdmissionMetrics.TokenRejectionReason.SignatureOrFormat
    };

    private TokenValidationParameters BuildValidationParameters()
    {
        return new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,

            ValidAlgorithms = new[]
            {
            SecurityAlgorithms.HmacSha256
        },

            ValidateIssuer = true,
            ValidIssuer = _issuer,

            ValidateAudience = true,
            RequireAudience = true,
            ValidAudience = _audience,

            ValidateLifetime = true,
            RequireExpirationTime = true,

            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = HasValidLifetime
        };
    }

    private bool HasValidLifetime(DateTime? notBefore, DateTime? expires,
        SecurityToken token, TokenValidationParameters parameters)
    {
        // Redis-derived nbf may lead a validator's clock slightly. Tolerate only nbf;
        // exp stays exclusive with no grace, and Redis remains the final authority.
        var now = _clock.GetUtcNow().UtcDateTime;
        return notBefore.HasValue && expires.HasValue
            && notBefore.Value - now <= TimeSpan.FromSeconds(NotBeforeToleranceSeconds)
            && now < expires.Value
            && notBefore.Value < expires.Value;
    }

    private bool HasAdmissionPassType(ClaimsIdentity? identity)
    {
        var typeClaims = identity?
            .FindAll(TypeClaimName)
            .ToArray();

        if (typeClaims is not { Length: 1 } ||
            !string.Equals(
                typeClaims[0].Value,
                AdmissionPassClaimValue,
                StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Admission token rejected: invalid token type");

            return false;
        }

        return true;
    }

    private bool TryExtractUserId(
    ClaimsIdentity? identity,
    out string userId)
    {
        userId = string.Empty;

        var claims = identity?
            .FindAll(JwtRegisteredClaimNames.Sub)
            .ToArray();

        if (claims is not { Length: 1 } ||
            string.IsNullOrWhiteSpace(claims[0].Value))
        {
            _logger.LogDebug(
                "Admission token rejected: invalid subject claim");

            return false;
        }

        userId = claims[0].Value;
        return true;
    }

    private bool TryExtractAdmissionId(
    ClaimsIdentity? identity,
    out AdmissionId admissionId)
    {
        admissionId = default;

        var claims = identity?
            .FindAll(JwtRegisteredClaimNames.Jti)
            .ToArray();

        if (claims is not { Length: 1 } ||
            !Guid.TryParse(claims[0].Value, out var id) ||
            id == Guid.Empty)
        {
            _logger.LogDebug(
                "Admission token rejected: missing or invalid admission ID");

            return false;
        }

        admissionId = new AdmissionId(id);
        return true;
    }

    private bool TryValidateTimestamps(
    SecurityToken securityToken,
    out DateTime issuedAt,
    out DateTime expiresAt)
    {
        issuedAt = default;
        expiresAt = default;

        if (securityToken is not JsonWebToken jwt ||
            jwt.IssuedAt == DateTime.MinValue ||
            jwt.ValidFrom == DateTime.MinValue ||
            jwt.IssuedAt != jwt.ValidFrom ||
            jwt.ValidTo <= jwt.IssuedAt)
        {
            _logger.LogDebug(
                "Admission token rejected: invalid token timestamps");

            return false;
        }

        issuedAt = jwt.IssuedAt;
        expiresAt = jwt.ValidTo;

        return true;
    }
}
