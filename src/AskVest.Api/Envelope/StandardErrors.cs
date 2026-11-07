using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AskVest.Api;

internal static class StandardErrors
{
    private static readonly (int Status, string Code, string Message)[] _entries =
    [
        (400, ErrorCodes.ValidationFailed, "The request is not valid."),
        (400, ErrorCodes.RequestMalformed, "The request body is not valid."),
        (404, ErrorCodes.RouteNotFound, "The route was not found."),
        (405, ErrorCodes.RouteMethodNotAllowed, "The method is not allowed."),
        (409, ErrorCodes.ResourceConflict, "The request conflicts with current state."),
        (422, ErrorCodes.BusinessRuleViolated, "A business rule rejected the request."),
        (429, ErrorCodes.RateLimitExceeded, "Too many requests."),
        (500, ErrorCodes.ServerUnexpected, "An unexpected error occurred."),
        (503, ErrorCodes.ServiceUnavailable, "The service is temporarily unavailable."),
    ];

    public static TBuilder WithStandardErrors<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new StandardErrorsMetadata());
        return builder;
    }

    public static void Apply(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();
        foreach (var (status, code, message) in _entries)
        {
            var statusKey = status.ToString();
            if (operation.Responses.ContainsKey(statusKey))
            {
                continue;
            }

            operation.Responses.Add(statusKey, new OpenApiResponse
            {
                Description = message,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = ErrorEnvelopeSchema(),
                    },
                },
            });
        }
    }

    private static OpenApiSchema ErrorEnvelopeSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["success"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                ["message"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["data"] = new OpenApiSchema { Type = JsonSchemaType.Object },
                ["error"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["details"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Array,
                            Items = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["field"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                    ["message"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                },
                            },
                        },
                    },
                },
                ["correlationId"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };
    }

    private sealed class StandardErrorsMetadata;
}
