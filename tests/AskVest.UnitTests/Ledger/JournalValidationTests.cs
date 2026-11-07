using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Services;
using AskVest.SharedKernel;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Ledger;

public sealed class JournalValidationTests
{
    private static readonly IReadOnlyDictionary<string, int> Scales = new Dictionary<string, int>
    {
        ["USD"] = 6,
        ["USDC"] = 6,
    };

    private static LedgerLine Line(decimal amount, string asset = "USD", Book book = Book.Practice)
        => new(
            new LedgerAccountRef(book, AccountOwnerType.User, Guid.CreateVersion7(), AccountKind.UserAvailable),
            asset,
            amount);

    [Theory]
    [InlineData(0)]
    [InlineData(10_000_000_000)]
    [InlineData(-10_000_000_000)]
    public void Amount_Zero_Or_Beyond_Ten_Integer_Digits_Is_Invalid(decimal amount)
    {
        JournalValidation.ValidateAmount(amount, 6)!.Code.ShouldBe(ErrorCodes.LedgerAmountInvalid);
    }

    [Theory]
    [InlineData("1.0000001")]
    [InlineData("0.1234567")]
    public void Amount_With_Excess_Scale_Is_Invalid(string text)
    {
        var amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        JournalValidation.ValidateAmount(amount, 6)!.Code.ShouldBe(ErrorCodes.LedgerAmountInvalid);
    }

    [Theory]
    [InlineData("1.000000")]
    [InlineData("0.000001")]
    [InlineData("9999999999.999999")]
    public void Amount_Within_Scale_And_Digits_Is_Valid(string text)
    {
        var amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        JournalValidation.ValidateAmount(amount, 6).ShouldBeNull();
    }

    [Fact]
    public void Unknown_Asset_Is_Rejected()
    {
        var line = new LedgerLine(
            new LedgerAccountRef(Book.Real, AccountOwnerType.System, Guid.Empty, AccountKind.SystemClearing),
            "NOPE",
            1m);
        var error = JournalValidation.ValidateLine(line, Book.Real, Scales);
        error!.Code.ShouldBe(ErrorCodes.LedgerAssetUnknown);
    }

    [Fact]
    public void Line_From_Another_Book_Is_Rejected()
    {
        var error = JournalValidation.ValidateLine(Line(1m, book: Book.Practice), Book.Real, Scales);
        error!.Code.ShouldBe(ErrorCodes.LedgerBookMismatch);
    }

    [Fact]
    public void Issuance_Account_In_Real_Book_Is_Rejected()
    {
        var line = new LedgerLine(
            LedgerAccounts.PracticeIssuance(Book.Real),
            "USD",
            -1m);
        var error = JournalValidation.ValidateLine(line, Book.Real, Scales);
        error!.Code.ShouldBe(ErrorCodes.LedgerBookMismatch);
    }

    [Fact]
    public void Unbalanced_Lines_Are_Rejected()
    {
        var lines = new[] { Line(10m), Line(-4m) };
        JournalValidation.ValidateZeroSum(lines)!.Code.ShouldBe(ErrorCodes.LedgerUnbalanced);
    }

    [Fact]
    public void Balanced_Lines_Pass()
    {
        var lines = new[] { Line(10m), Line(-10m) };
        JournalValidation.ValidateZeroSum(lines).ShouldBeNull();
    }

    [Fact]
    public void Content_Hash_Is_Deterministic_And_Order_Independent()
    {
        var first = Line(5m);
        var second = Line(-5m, "USDC");
        var hashA = JournalValidation.ContentHash(Book.Practice, "deposit", "test", null, null, [first, second]);
        var hashB = JournalValidation.ContentHash(Book.Practice, "deposit", "test", null, null, [second, first]);
        hashA.ShouldBe(hashB);
        hashA.ShouldMatch("^[0-9a-f]{64}$");

        var different = JournalValidation.ContentHash(Book.Practice, "deposit", "test", null, null, [first, second with { Amount = -6m }]);
        different.ShouldNotBe(hashA);
    }

    [Fact]
    public void Content_Hash_Treats_Equal_Amounts_With_Different_Scale_As_Same()
    {
        var credit = Line(1.5m);
        var debit = Line(-1.5m);
        var hashA = JournalValidation.ContentHash(Book.Practice, "deposit", "test", null, null, [credit, debit]);
        var hashB = JournalValidation.ContentHash(
            Book.Practice, "deposit", "test", null, null,
            [credit with { Amount = 1.50m }, debit with { Amount = -1.50m }]);
        hashA.ShouldBe(hashB);
    }
}
