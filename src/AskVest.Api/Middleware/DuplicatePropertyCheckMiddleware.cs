using System.Text.Json;
using AskVest.SharedKernel;

namespace AskVest.Api;

internal sealed class DuplicatePropertyCheckMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> JsonMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "PATCH",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (JsonMethods.Contains(context.Request.Method)
            && context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true
            && context.Request.ContentLength is > 0)
        {
            context.Request.EnableBuffering();
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var duplicate = FindDuplicate(document.RootElement);
            context.Request.Body.Position = 0;
            if (duplicate is not null)
            {
                await ApiResults.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "The request is not valid.",
                    null,
                    new ApiError(
                        ErrorCodes.ValidationFailed,
                        [new ApiErrorDetail("$", $"Duplicate property '{duplicate}'.")]));
                return;
            }
        }

        await next(context);
    }

    private static string? FindDuplicate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    return property.Name;
                }

                var nested = FindDuplicate(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindDuplicate(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
