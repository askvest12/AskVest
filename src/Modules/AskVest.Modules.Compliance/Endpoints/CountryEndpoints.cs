using AskVest.Modules.Compliance.Services;
using AskVest.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Compliance.Endpoints;

public static class CountryEndpoints
{
    public static void MapCountryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/country", SetCountryAsync)
            .WithName("SetComplianceCountry")
            .WithSummary("Declare the country of residence.")
            .WithTags("Compliance")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> SetCountryAsync(
        SetCountryRequest request,
        HttpContext httpContext,
        CountryService countryService,
        CancellationToken cancellationToken)
    {
        var result = await countryService.SetCountryAsync(
            DocumentEndpoints.CurrentUserId(httpContext),
            request.Country,
            httpContext.TraceIdentifier,
            cancellationToken);
        return result.IsSuccess
            ? ComplianceResults.Ok(new { country = request.Country.Trim().ToUpperInvariant() }, "Country recorded.", httpContext)
            : ComplianceResults.Error(result.Error!, httpContext);
    }

    internal sealed record SetCountryRequest(string Country);

    internal sealed class SetCountryValidator : AbstractValidator<SetCountryRequest>
    {
        public SetCountryValidator()
        {
            RuleFor(r => r.Country).NotEmpty().Length(2);
        }
    }
}
