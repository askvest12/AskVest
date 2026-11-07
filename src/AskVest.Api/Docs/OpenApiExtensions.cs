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
        return Task.CompletedTask;
    }
}
