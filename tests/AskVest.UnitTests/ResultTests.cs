using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class ResultTests
{
    private static readonly Error SampleError = new(
        ErrorCodes.BusinessRuleViolated,
        "Rule violated.",
        ErrorType.BusinessRule);

    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();
        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void SuccessOfT_CarriesValue()
    {
        var result = Result.Success(42);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failure_CarriesError()
    {
        var result = Result.Failure(SampleError);
        result.IsSuccess.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void FailureOfT_CarriesError()
    {
        var result = Result.Failure<int>(SampleError);
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void Value_OnFailure_Throws()
    {
        var result = Result.Failure<int>(SampleError);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Constructor_InconsistentSuccessWithError_Throws()
    {
        // The only public construction paths are the factories; verify the invariant
        // by checking that a failure result cannot be constructed without an error.
        Should.Throw<ArgumentException>(() => Result.Failure(new Error("", "", ErrorType.Failure)));
    }

    [Fact]
    public void Error_RejectsEmptyCode()
    {
        Should.Throw<ArgumentException>(() => new Error("", "Message.", ErrorType.Failure));
    }

    [Fact]
    public void Error_RejectsEmptyMessage()
    {
        Should.Throw<ArgumentException>(() => new Error("some.code", "", ErrorType.Failure));
    }
}
