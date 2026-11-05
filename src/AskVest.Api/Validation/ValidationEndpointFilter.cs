using AskVest.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Http.Json;

namespace AskVest.Api;

internal sealed class ValidationEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var result = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);
            if (result.IsValid)
            {
                return await next(context);
            }

            var failures = result.Errors
                .Select(error => new ValidationFailure(error.PropertyName, error.ErrorMessage))
                .ToList();
            var domainError = new Error(
                ErrorCodes.ValidationFailed,
                "The request is not valid.",
                ErrorType.Validation,
                failures);
            var status = ApiResults.StatusFor(domainError.Type);
            var apiError = ApiResults.ToApiError(domainError);
            return Results.Json(
                new ApiResponse<object?>(false, "The request is not valid.", null, apiError, CorrelationIdMiddleware.Get(context.HttpContext)),
                statusCode: status);
        }

        return await next(context);
    }
}

internal sealed class ValidationEndpointFilter<TRequest> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return await next(context);
        }

        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();
        if (validator is null)
        {
            return await next(context);
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var failures = result.Errors
            .Select(error => new ValidationFailure(error.PropertyName, error.ErrorMessage))
            .ToList();
        var domainError = new Error(
            ErrorCodes.ValidationFailed,
            "The request is not valid.",
            ErrorType.Validation,
            failures);
        var status = ApiResults.StatusFor(domainError.Type);
        var apiError = ApiResults.ToApiError(domainError);
        return Results.Json(
            new ApiResponse<object?>(false, "The request is not valid.", null, apiError, CorrelationIdMiddleware.Get(context.HttpContext)),
            statusCode: status);
    }
}
