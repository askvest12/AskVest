using System.ComponentModel.DataAnnotations;
using AskVest.Modules.Catalog.Configuration;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Catalog;

public sealed class CatalogOptionsTests
{
    [Fact]
    public void Defaults_Are_Valid()
    {
        var options = new CatalogOptions();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            .ShouldBeTrue();
        options.DataPath.ShouldBe("data/catalog");
        options.LoadSampleData.ShouldBeFalse();
        options.ReviewMaxMonths.ShouldBe(12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void ReviewMaxMonths_Out_Of_Range_Fails(int months)
    {
        var options = new CatalogOptions { ReviewMaxMonths = months };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void Empty_DataPath_Fails()
    {
        var options = new CatalogOptions { DataPath = "" };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true)
            .ShouldBeFalse();
    }
}
