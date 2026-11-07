using System.Security.Claims;
using AskVest.Modules.Compliance.Services;
using AskVest.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Compliance.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/documents", GetDocumentsAsync)
            .WithName("GetComplianceDocuments")
            .WithSummary("List the current legal documents and this user's acceptance state.")
            .WithTags("Compliance");

        group.MapPost("/consents", AcceptConsentsAsync)
            .WithName("AcceptComplianceConsents")
            .WithSummary("Accept the current versions of legal documents.")
            .WithTags("Compliance")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> GetDocumentsAsync(
        HttpContext httpContext,
        ConsentService consentService,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId(httpContext);
        var documents = await consentService.GetDocumentsAsync(userId, cancellationToken);
        return ComplianceResults.Ok(
            new
            {
                documents = documents.Select(d => new
                {
                    type = d.Type,
                    version = d.Version,
                    url = d.Url,
                    sha256 = d.Sha256,
                    requiredFor = d.RequiredFor,
                    accepted = d.Accepted,
                })
            },
            "Documents retrieved.",
            httpContext);
    }

    private static async Task<IResult> AcceptConsentsAsync(
        AcceptConsentsRequest request,
        HttpContext httpContext,
        ConsentService consentService,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId(httpContext);
        var result = await consentService.AcceptAsync(
            userId,
            request.Documents.Select(d => new ConsentAcceptanceItem(d.Type, d.Version, d.Sha256)).ToList(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return result.IsSuccess
            ? ComplianceResults.Ok<object?>(null, "Consents recorded.", httpContext)
            : ComplianceResults.Error(result.Error!, httpContext);
    }

    internal static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);

    internal sealed record ConsentItemRequest(string Type, string Version, string Sha256);

    internal sealed record AcceptConsentsRequest(IReadOnlyList<ConsentItemRequest> Documents);

    internal sealed class AcceptConsentsValidator : AbstractValidator<AcceptConsentsRequest>
    {
        public AcceptConsentsValidator()
        {
            RuleFor(r => r.Documents).NotEmpty();
            RuleForEach(r => r.Documents).ChildRules(item =>
            {
                item.RuleFor(d => d.Type).NotEmpty().MaximumLength(32);
                item.RuleFor(d => d.Version).NotEmpty().MaximumLength(32);
                item.RuleFor(d => d.Sha256).NotEmpty().Length(64);
            });
        }
    }
}
