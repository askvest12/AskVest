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

public sealed class AccessTokenService : IAccessTokenService
{
    private readonly IdentityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly byte[] _currentKeyPkcs8;
    private readonly byte[]? _previousKeyPkcs8;
    private readonly string _currentKeyId;
    private readonly string? _previousKeyId;

    public AccessTokenService(IOptions<IdentityOptions> options, IOptions<IdentitySecretsOptions> secrets, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _currentKeyPkcs8 = Convert.FromBase64String(RequireKey(secrets.Value.AccessTokenSigningKey));
        _previousKeyPkcs8 = string.IsNullOrWhiteSpace(secrets.Value.AccessTokenSigningKeyPrevious)
            ? null
            : Convert.FromBase64String(secrets.Value.AccessTokenSigningKeyPrevious);
        _currentKeyId = KeyId(_currentKeyPkcs8);
        _previousKeyId = _previousKeyPkcs8 is null ? null : KeyId(_previousKeyPkcs8);
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
#pragma warning disable CA2000 // ownership transfers to the security key
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(CreateKey(_currentKeyPkcs8)),
                SecurityAlgorithms.EcdsaSha256),
#pragma warning restore CA2000
        };
        var signed = _handler.CreateToken(descriptor);
        return new IssuedAccessToken(signed, expiresAt);
    }

    public async Task<AccessTokenValidation> ValidateAsync(string token)
    {
        try
        {
            var parameters = new TokenValidationParameters
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
                IssuerSigningKeys = SigningKeys(),
            };
            var result = await _handler.ValidateTokenAsync(token, parameters);
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

    private List<SecurityKey> SigningKeys()
    {
#pragma warning disable CA2000 // ownership transfers to the security keys
        var keys = new List<SecurityKey> { new ECDsaSecurityKey(CreateKey(_currentKeyPkcs8)) { KeyId = _currentKeyId } };
        if (_previousKeyPkcs8 is not null)
        {
            keys.Add(new ECDsaSecurityKey(CreateKey(_previousKeyPkcs8)) { KeyId = _previousKeyId });
        }
#pragma warning restore CA2000

        return keys;
    }

    private static ECDsa CreateKey(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        catch
        {
            key.Dispose();
            throw new InvalidOperationException("Access token signing key is not a valid base64-encoded PKCS8 EC private key.");
        }

        return key;
    }

    private static string KeyId(byte[] pkcs8)
    {
        using var key = CreateKey(pkcs8);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];
    }

    private static string RequireKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException("Access token signing key is not configured.")
            : key;
}
