using AskVest.SharedKernel;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AskVest.Api;

internal static class OpenApiExtensions
{
    public static void ConfigureOpenApi(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new Microsoft.OpenApi.OpenApiInfo
            {
                Title = "AskVest API",
                Version = "v1",
                Description = "AskVest backend API. All responses use the standard envelope.",
            };
            document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
            {
                Type = Microsoft.OpenApi.SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "AskVest access token obtained from POST /api/v1/auth/login or /api/v1/auth/refresh.",
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer<StandardErrorsOperationTransformer>();
    }
}

internal sealed class StandardErrorsOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        StandardErrors.Apply(operation);
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<RequiresIdempotencyAttribute>().Any()
            && !metadata.OfType<IdempotencyExemptAttribute>().Any()
            && (operation.Parameters ?? []).All(parameter => parameter.Name != IdempotencyKey.HeaderName))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotencyKey.HeaderName,
                In = ParameterLocation.Header,
                Required = true,
                Description = "Unique key for safe retry. Replays return the stored response.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        return Task.CompletedTask;
    }
}
