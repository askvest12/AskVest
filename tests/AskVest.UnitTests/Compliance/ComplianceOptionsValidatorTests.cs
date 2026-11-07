using AskVest.Modules.Compliance.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Compliance;

public sealed class ComplianceOptionsValidatorTests
{
    private static ComplianceOptions ValidOptions()
    {
        var options = new ComplianceOptions();
        options.Documents.Add(new ComplianceDocumentOptions
        {
            Type = "terms",
            Version = "1",
            Url = new Uri("https://askvest.example/terms"),
            Sha256 = new string('a', 64),
            RequiredFor = "practice",
        });
        options.Kyc.WebhookSecret = "0123456789abcdef0123456789abcdef";
        return options;
    }

    private static ValidateOptionsResult Validate(ComplianceOptions options)
        => ComplianceOptions.Validator.Validate(null, options);

    [Fact]
    public void Valid_Configuration_Passes()
    {
        Validate(ValidOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Empty_Configuration_With_Secret_Passes()
    {
        var options = new ComplianceOptions();
        options.Kyc.WebhookSecret = "0123456789abcdef0123456789abcdef";
        Validate(options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Missing_Webhook_Secret_Fails()
    {
        var options = ValidOptions();
        options.Kyc.WebhookSecret = null;
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Short_Webhook_Secret_Fails()
    {
        var options = ValidOptions();
        options.Kyc.WebhookSecret = "too-short";
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Duplicate_Document_Type_Fails()
    {
        var options = ValidOptions();
        options.Documents.Add(new ComplianceDocumentOptions
        {
            Type = "terms",
            Version = "2",
            Url = new Uri("https://askvest.example/terms"),
            Sha256 = new string('b', 64),
            RequiredFor = "real",
        });
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Bad_Content_Hash_Fails()
    {
        var options = ValidOptions();
        options.Documents[0].Sha256 = "not-a-hash";
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_RequiredFor_Fails()
    {
        var options = ValidOptions();
        options.Documents[0].RequiredFor = "everything";
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Missing_Document_Version_Fails()
    {
        var options = ValidOptions();
        options.Documents[0].Version = " ";
        Validate(options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("USA")]
    [InlineData("1A")]
    public void Invalid_Country_Code_Fails(string country)
    {
        var options = ValidOptions();
        options.BlockedCountries.Add(country);
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Country_In_Blocked_And_RealMoney_Fails()
    {
        var options = ValidOptions();
        options.BlockedCountries.Add("NG");
        options.RealMoneyCountries.Add("NG");
        Validate(options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositive_Kyc_Limits_Fail(int value)
    {
        var options = ValidOptions();
        options.Kyc.ApprovalValidityMonths = value;
        options.Kyc.MaxAttempts = value;
        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void NonPositive_Webhook_Tolerance_Fails()
    {
        var options = ValidOptions();
        options.WebhookTolerance = TimeSpan.Zero;
        Validate(options).Failed.ShouldBeTrue();
    }
}
