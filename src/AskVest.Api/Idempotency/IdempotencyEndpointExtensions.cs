using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;

namespace AskVest.Api;

internal static class IdempotencyEndpointExtensions
{
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresIdempotencyAttribute());
        return builder;
    }

    public static TBuilder ExemptFromIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new IdempotencyExemptAttribute());
        return builder;
    }
}
