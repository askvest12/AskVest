using AskVest.Api;
using AskVest.SharedKernel;
using Error = AskVest.SharedKernel.Error;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class ApiEnvelopeTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthenticated, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.RateLimited, 429)]
    [InlineData(ErrorType.BusinessRule, 422)]
    [InlineData(ErrorType.Unavailable, 503)]
    [InlineData(ErrorType.Failure, 500)]
    public void StatusFor_MapsEachType(ErrorType type, int expected)
        => ApiResults.StatusFor(type).ShouldBe(expected);

    [Fact]
    public void ApiError_FromResult_NeverIncludesMessage()
    {
        var result = Result.Failure<int>(new Error(ErrorCodes.ResourceNotFound, "User 42 does not exist.", ErrorType.NotFound));
        var apiError = ApiResults.ToApiError(result.Error!);
        apiError.Code.ShouldBe(ErrorCodes.ResourceNotFound);
        apiError.Details.ShouldBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(apiError);
        json.ShouldNotContain("does not exist");
    }

    [Fact]
    public void ApiError_FromResult_IncludesDetailsPaths()
    {
        var result = Result.Failure<int>(new Error(
            ErrorCodes.ValidationFailed,
            "bad",
            ErrorType.Validation,
            [new ValidationFailure("Amount", "required")]));
        var apiError = ApiResults.ToApiError(result.Error!);
        apiError.Details.ShouldNotBeNull();
        apiError.Details![0].Field.ShouldBe("Amount");
    }

    [Fact]
    public void ApiErrorMapper_MapsEveryCatalogueCode()
    {
        foreach (var code in ErrorCatalogue.AllCodes)
        {
            ErrorCatalogue.TryGet(code, out var status, out var type, out var message).ShouldBeTrue(code);
            status.ShouldBeGreaterThanOrEqualTo(400);
            message.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
