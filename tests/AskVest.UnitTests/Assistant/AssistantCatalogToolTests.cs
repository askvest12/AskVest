using System.Text.Json;
using AskVest.Modules.Assistant.Tools;
using AskVest.Modules.Catalog.Contracts;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantCatalogToolTests
{
    private static ToolContext Context() => new(Guid.CreateVersion7(), TimeProvider.System);

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CatalogInstrument Instrument(string id, string symbol, string name, string status = "active")
        => new(id, "stock", name, symbol, null, "NASDAQ", "US", "USD", null, status, "A sample instrument.");

    [Fact]
    public async Task SearchInstruments_Matches_And_Caps_Results()
    {
        var catalog = new StubCatalog();
        catalog.Instruments.Add(Instrument("inst-aapl", "AAPL", "Apple sample"));
        catalog.Instruments.Add(Instrument("inst-msft", "MSFT", "Microsoft sample"));
        catalog.Instruments.Add(Instrument("inst-aapl2", "AAPL2", "Apple twin"));
        catalog.Instruments.Add(Instrument("inst-old", "OLD", "Retired sample", "delisted"));

        var tool = new SearchInstrumentsTool(catalog);
        var result = await tool.ExecuteAsync(
            Args("""{"query":"apple","limit":2}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.ResultId.ShouldBeNull();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
        payload.RootElement.GetProperty("instruments")[0].GetProperty("symbol").GetString().ShouldBe("AAPL");
    }

    [Fact]
    public async Task SearchInstruments_Rejects_Missing_Or_Oversized_Query()
    {
        var tool = new SearchInstrumentsTool(new StubCatalog());

        var missing = await tool.ExecuteAsync(
            Args("""{"limit":3}"""), Context(), TestContext.Current.CancellationToken);
        missing.IsError.ShouldBeTrue();
        missing.ErrorCode.ShouldBe("invalid_arguments");

        var oversized = await tool.ExecuteAsync(
            Args($$"""{"query":"{{new string('x', 101)}}"}"""), Context(), TestContext.Current.CancellationToken);
        oversized.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstrument_Returns_Details_Or_NotFound()
    {
        var catalog = new StubCatalog();
        catalog.Instruments.Add(Instrument("inst-aapl", "AAPL", "Apple sample"));
        var tool = new GetInstrumentTool(catalog);

        var found = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-aapl"}"""), Context(), TestContext.Current.CancellationToken);
        found.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(found.PayloadJson);
        payload.RootElement.GetProperty("instrument").GetProperty("shortDescription")
            .GetString().ShouldBe("A sample instrument.");

        var missing = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-nope"}"""), Context(), TestContext.Current.CancellationToken);
        missing.IsError.ShouldBeTrue();
        missing.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task GetTokenDisclosure_Returns_Reviewed_Disclosure()
    {
        var tokenId = Guid.CreateVersion7();
        var catalog = new StubCatalog
        {
            Disclosure = new CatalogTokenDisclosure(
                tokenId, "inst-aapl", "AAPL", "eip155:1", "listed", "depositary_receipt",
                "Tracks the share price.", "Held by custodian.", "Bank custodian.", "Redeem via issuer.",
                "reinvested", null, true, null, "Transfer limits apply.", ["issuer fee"], false,
                ["market risk"], "https://example.com/disclosure", null, null),
        };
        var tool = new GetTokenDisclosureTool(catalog);

        var result = await tool.ExecuteAsync(
            Args($$"""{"tokenId":"{{tokenId}}"}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("token").GetProperty("claimType").GetString()
            .ShouldBe("depositary_receipt");

        var missing = await tool.ExecuteAsync(
            Args($$"""{"tokenId":"{{Guid.CreateVersion7()}}"}"""), Context(), TestContext.Current.CancellationToken);
        missing.IsError.ShouldBeTrue();
        missing.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task GetTokenDisclosure_Rejects_Non_Uuid()
    {
        var tool = new GetTokenDisclosureTool(new StubCatalog());
        var result = await tool.ExecuteAsync(
            Args("""{"tokenId":"not-a-uuid"}"""), Context(), TestContext.Current.CancellationToken);
        result.IsError.ShouldBeTrue();
        result.ErrorCode.ShouldBe("invalid_arguments");
    }
}
