using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Identity.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AskVest.Modules.Identity.Crypto;

public interface IAccessTokenService
{
    IssuedAccessToken Issue(Guid userId, Guid sessionId);

    Task<AccessTokenValidation> ValidateAsync(string token);
}

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed record AccessTokenValidation(bool IsValid, bool IsExpired, ClaimsPrincipal? Principal)
{
    public static readonly AccessTokenValidation Invalid = new(false, false, null);
    public static readonly AccessTokenValidation Expired = new(false, true, null);
}

public sealed class AccessTokenService : IAccessTokenService, IDisposable
{
    private readonly IdentityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _validationParameters;
    private readonly ECDsa _currentKey;
    private readonly ECDsa? _previousKey;
    private readonly string _currentKeyId;
    private readonly string? _previousKeyId;

    public AccessTokenService(IOptions<IdentityOptions> options, IOptions<IdentitySecretsOptions> secrets, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _currentKey = LoadKey(secrets.Value.AccessTokenSigningKey!);
        _currentKeyId = KeyId(_currentKey);
        _previousKey = string.IsNullOrWhiteSpace(secrets.Value.AccessTokenSigningKeyPrevious)
            ? null
            : LoadKey(secrets.Value.AccessTokenSigningKeyPrevious);
        _previousKeyId = _previousKey is null ? null : KeyId(_previousKey);

        var signingKeys = new List<SecurityKey> { new ECDsaSecurityKey(_currentKey) { KeyId = _currentKeyId } };
        if (_previousKey is not null)
        {
            signingKeys.Add(new ECDsaSecurityKey(_previousKey) { KeyId = _previousKeyId });
        }

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.AccessTokenIssuer,
            ValidateAudience = true,
            ValidAudience = _options.AccessTokenAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            IssuerSigningKeys = signingKeys,
        };
    }

    public IssuedAccessToken Issue(Guid userId, Guid sessionId)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(_options.AccessTokenLifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.AccessTokenIssuer,
            Audience = _options.AccessTokenAudience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
                new Claim("sid", sessionId.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("D")),
            ]),
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(_currentKey) { KeyId = _currentKeyId },
                SecurityAlgorithms.EcdsaSha256),
        };
        var signed = _handler.CreateToken(descriptor);
        return new IssuedAccessToken(signed, expiresAt);
    }

    public async Task<AccessTokenValidation> ValidateAsync(string token)
    {
        try
        {
            var result = await _handler.ValidateTokenAsync(token, _validationParameters);
            if (!result.IsValid || result.ClaimsIdentity is null)
            {
                return AccessTokenValidation.Invalid;
            }

            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            var expClaim = result.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
            if (!long.TryParse(expClaim, System.Globalization.CultureInfo.InvariantCulture, out var expUnix)
                || DateTimeOffset.FromUnixTimeSeconds(expUnix) <= _timeProvider.GetUtcNow())
            {
                return AccessTokenValidation.Expired;
            }

            return new AccessTokenValidation(true, false, principal);
        }
        catch (SecurityTokenExpiredException)
        {
            return AccessTokenValidation.Expired;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException)
        {
            return AccessTokenValidation.Invalid;
        }
    }

    private static ECDsa LoadKey(string base64Pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(base64Pkcs8), out _);
        }
        catch
        {
            key.Dispose();
            throw new InvalidOperationException("Access token signing key is not a valid base64-encoded PKCS8 EC private key.");
        }

        return key;
    }

    private static string KeyId(ECDsa key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];
    }

    public void Dispose()
    {
        _currentKey.Dispose();
        _previousKey?.Dispose();
    }
}
