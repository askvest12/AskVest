using System.ComponentModel.DataAnnotations;
using AskVest.Modules.Ledger.Configuration;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Ledger;

public sealed class LedgerOptionsTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(60, 0)]
    public void Invalid_Values_Fail_Validation(int interval, int pageSize)
    {
        var options = new LedgerOptions
        {
            ReconciliationIntervalMinutes = interval,
            ReconciliationPageSize = pageSize,
        };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            .ShouldBeFalse();
        results.ShouldNotBeEmpty();
    }

    [Fact]
    public void Defaults_Are_Valid()
    {
        var options = new LedgerOptions();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            .ShouldBeTrue();
        options.ReconciliationIntervalMinutes.ShouldBe(60);
        options.ReconciliationPageSize.ShouldBe(1000);
    }
}
