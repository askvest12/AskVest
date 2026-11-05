using Microsoft.OpenApi;
using Microsoft.AspNetCore.OpenApi;

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
