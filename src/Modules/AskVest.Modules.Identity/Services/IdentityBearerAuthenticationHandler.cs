using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Crypto;
using AskVest.Modules.Identity.Persistence;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

public sealed class IdentityBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AskVest.Bearer";
    public const string ErrorCodeItemKey = "AskVest.AuthErrorCode";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[prefix.Length..].Trim();
        if (token.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var tokenService = Context.RequestServices.GetRequiredService<IAccessTokenService>();
        var validation = await tokenService.ValidateAsync(token);
        if (!validation.IsValid || validation.Principal is null)
        {
            Context.Items[ErrorCodeItemKey] = validation.IsExpired
                ? ErrorCodes.AuthTokenExpired
                : ErrorCodes.AuthUnauthenticated;
            return AuthenticateResult.Fail("The access token is not valid.");
        }

        var principal = validation.Principal;
        var userIdText = principal.FindFirst("sub")?.Value;
        var sessionIdText = principal.FindFirst("sid")?.Value;
        if (!Guid.TryParse(userIdText, out var userId) || !Guid.TryParse(sessionIdText, out var sessionId))
        {
            Context.Items[ErrorCodeItemKey] = ErrorCodes.AuthUnauthenticated;
            return AuthenticateResult.Fail("The access token is not valid.");
        }

        var db = Context.RequestServices.GetRequiredService<IdentityDbContext>();
        var timeProvider = Context.RequestServices.GetRequiredService<TimeProvider>();
        var optionsValue = Context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var now = timeProvider.GetUtcNow();

        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, Context.RequestAborted);
        if (session is null || session.RevokedAt is not null || session.AbsoluteExpiresAt <= now)
        {
            Context.Items[ErrorCodeItemKey] = ErrorCodes.AuthSessionRevoked;
            return AuthenticateResult.Fail("The session is no longer valid.");
        }

        if (now - session.LastUsedAt >= optionsValue.SessionLastUsedInterval)
        {
            session.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", userId.ToString("D")),
                new Claim("sid", sessionId.ToString("D")),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";

        var code = Context.Items.TryGetValue(ErrorCodeItemKey, out var value) && value is string c
            ? c
            : ErrorCodes.AuthUnauthenticated;
        var payload = new
        {
            success = false,
            message = "Authentication is required.",
            data = (object?)null,
            error = new { code, details = (object?)null },
            correlationId = Context.TraceIdentifier,
        };
        var jsonOptions = Context.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        await JsonSerializer.SerializeAsync(Response.Body, payload, jsonOptions, Context.RequestAborted);
    }
}
