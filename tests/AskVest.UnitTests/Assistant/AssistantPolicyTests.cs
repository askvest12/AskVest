using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Policy;
using AskVest.Modules.Assistant.Tools;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantPolicyTests
{
    [Fact]
    public void Sanitize_Strips_Html_Markdown_Links_Urls_And_Control_Chars()
    {
        var input = "Here is [a link](https://evil.example) and <b>bold</b>\nplus https://evil.example/page\tinline.";
        var result = TextSanitizer.Sanitize(input);
        result.ShouldBe("Here is a link and bold plus inline.");
        result.ShouldNotContain("http");
        result.ShouldNotContain("<");
        result.ShouldNotContain("](");
    }

    [Fact]
    public void Sanitize_Keeps_Plain_Text_And_Citation_Markers()
    {
        TextSanitizer.Sanitize("AAPL trades at 123.45 USD. [r1][r2]")
            .ShouldBe("AAPL trades at 123.45 USD. [r1][r2]");
    }

    [Theory]
    [InlineData("You should buy this stock today.", AnswerPolicy.CategoryRecommendation)]
    [InlineData("I recommend you sell everything else first.", AnswerPolicy.CategoryRecommendation)]
    [InlineData("This is a guaranteed return with no risk.", AnswerPolicy.CategoryPromise)]
    [InlineData("It is a sure thing, you cannot lose.", AnswerPolicy.CategoryPromise)]
    [InlineData("The price will rise 20% next week.", AnswerPolicy.CategoryPrediction)]
    [InlineData("Shares are going to hit a new high soon.", AnswerPolicy.CategoryPrediction)]
    [InlineData("This is definitely the right move.", AnswerPolicy.CategoryCertainty)]
    [InlineData("It will certainly double.", AnswerPolicy.CategoryPrediction)]
    [InlineData("You can be 100% sure of it.", AnswerPolicy.CategoryCertainty)]
    public void Policy_Blocks_Forbidden_Categories(string text, string category)
    {
        AnswerPolicy.Check(text, ["you should buy", "i recommend"]).ShouldBe(category);
    }

    [Theory]
    [InlineData("A recommendation is personal advice; this assistant does not give recommendations.")]
    [InlineData("You should understand that a token is not the same as owning the share.")]
    [InlineData("Some analysts publish price targets; this assistant does not predict prices.")]
    [InlineData("The lesson explains what diversification means.")]
    [InlineData("Prices can fall as well as rise; past performance does not guarantee future results.")]
    public void Policy_Allows_Legitimate_Education(string text)
    {
        AnswerPolicy.Check(text, ["you should buy", "i recommend"]).ShouldBeNull();
    }

    [Fact]
    public void Policy_Uses_Configured_Phrases()
    {
        AnswerPolicy.Check("Our firm says accumulate now.", ["accumulate now"]).ShouldNotBeNull();
        AnswerPolicy.Check("Our firm says accumulate now.", []).ShouldBeNull();
    }

    [Theory]
    [InlineData("AskVest is non-custodial, so your keys stay with you.")]
    [InlineData("This is a self-custody wallet.")]
    [InlineData("It offers self custody for every user.")]
    [InlineData("You control your keys at all times.")]
    public void Default_Phrases_Block_Custody_Claims(string text)
    {
        AnswerPolicy.Check(text, AssistantOptions.DefaultForbiddenPhrases).ShouldNotBeNull();
    }

    [Fact]
    public void Default_Phrases_Allow_Educational_Custody_Text()
    {
        AnswerPolicy.Check(
                "Custodial means a provider holds the assets; the lesson explains the difference.",
                AssistantOptions.DefaultForbiddenPhrases)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("AAPL is 123.45 USD.", "price 123.45 currency USD", true)]
    [InlineData("The price is 123.5 USD.", "price 123.46 currency USD", true)]
    [InlineData("The price is 999.99 USD.", "price 123.45 currency USD", false)]
    [InlineData("It will rise 42%.", "price 123.45 currency USD", false)]
    [InlineData("You asked about 3 lessons.", "", true)]
    [InlineData("You mentioned 500 dollars.", "budget 500", true)]
    [InlineData("Chain eip155:11155111 is used.", "chain eip155:11155111", true)]
    [InlineData("The year 2026 matters.", "", false)]
    public void Grounding_Checks_Figures_Against_Corpus(string answer, string corpus, bool expected)
    {
        NumericGrounding.IsGrounded(answer, corpus).ShouldBe(expected);
    }

    [Fact]
    public void Citations_Keep_Valid_Markers_And_Strip_Unknown()
    {
        var asOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var results = new[]
        {
            ToolResult.Ok("{}", [new ToolSource("price", "inst-a", "AAPL", asOf, "live")])
                with { ResultId = "r1", ToolName = "get_prices", AsOf = asOf },
            ToolResult.Ok("{}", [new ToolSource("instrument", "inst-b", "MSFT", asOf)])
                with { ResultId = "r2", ToolName = "get_instrument", AsOf = asOf },
        };

        var (text, citations, rejected) = CitationValidator.Validate(
            "A is 1. [r1] B is 2. [r2][r99][r1]", results);

        rejected.ShouldBeTrue();
        text.ShouldBe("A is 1. [r1] B is 2. [r2] [r1]");
        citations.Count.ShouldBe(2);
        citations[0].ShouldBe(new AssistantCitation("price", "inst-a", "AAPL", asOf, "live"));
        citations[1].Type.ShouldBe("instrument");
    }

    [Fact]
    public void Citations_Ignore_Error_Results_And_Other_Message_Ids()
    {
        var asOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var results = new[]
        {
            ToolResult.Fail("not_found", "missing")
                with { ResultId = null, ToolName = "get_instrument", AsOf = asOf },
        };

        var (_, citations, rejected) = CitationValidator.Validate("See [r1].", results);
        rejected.ShouldBeTrue();
        citations.ShouldBeEmpty();
    }
}
