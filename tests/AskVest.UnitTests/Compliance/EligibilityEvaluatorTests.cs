using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence.Entities;
using AskVest.Modules.Identity.Contracts;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Compliance;

public sealed class EligibilityEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ComplianceOptions Options(
        bool realMoneyEnabled = true,
        IReadOnlyList<string>? realMoneyCountries = null,
        IReadOnlyList<string>? blockedCountries = null)
    {
        var options = new ComplianceOptions
        {
            RealMoneyEnabled = realMoneyEnabled,
        };
        foreach (var country in realMoneyCountries ?? ["NG", "US"])
        {
            options.RealMoneyCountries.Add(country);
        }

        foreach (var country in blockedCountries ?? ["KP"])
        {
            options.BlockedCountries.Add(country);
        }

        options.Documents.Add(Doc("terms", "practice"));
        options.Documents.Add(Doc("privacy", "practice"));
        options.Documents.Add(Doc("risk_disclosure", "real"));
        options.Documents.Add(Doc("token_disclosure", "real"));
        return options;
    }

    private static ComplianceDocumentOptions Doc(string type, string requiredFor)
        => new()
        {
            Type = type,
            Version = "v1",
            Url = new Uri("https://askvest.example/doc"),
            Sha256 = new string('a', 64),
            RequiredFor = requiredFor,
        };

    private static readonly UserAccountSnapshot VerifiedAccount = new(IsActive: true, EmailVerified: true);

    private static HashSet<string> AllConsents()
        => ["terms@v1", "privacy@v1", "risk_disclosure@v1", "token_disclosure@v1"];

    private static ComplianceProfile ApprovedProfile(string country = "NG")
        => new()
        {
            UserId = Guid.CreateVersion7(),
            DeclaredCountry = country,
            KycStatus = KycStatus.Approved,
            VerifiedCountry = country,
            AttemptsUsed = 1,
            DecidedAt = Now.AddDays(-1),
            ExpiresAt = Now.AddMonths(23),
            ScreeningStatus = ScreeningStatus.Clear,
            ScreenedAt = Now.AddDays(-1),
        };

    private static List<string> Codes(IReadOnlyList<EligibilityBlocker> blockers)
        => blockers.Select(b => b.Code).ToList();

    [Fact]
    public void Fully_Eligible_User_Gets_Both_Modes()
    {
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, ApprovedProfile(), AllConsents(), Options(), Now);
        result.PracticeAllowed.ShouldBeTrue();
        result.RealAllowed.ShouldBeTrue();
        result.PracticeBlockers.ShouldBeEmpty();
        result.RealBlockers.ShouldBeEmpty();
    }

    [Fact]
    public void Missing_Email_And_Account_State_Block_Practice()
    {
        var unverified = EligibilityEvaluator.Evaluate(
            new UserAccountSnapshot(true, false), null, AllConsents(), Options(), Now);
        Codes(unverified.PracticeBlockers).ShouldContain(BlockerCodes.EmailNotVerified);

        var inactive = EligibilityEvaluator.Evaluate(
            new UserAccountSnapshot(false, true), null, AllConsents(), Options(), Now);
        Codes(inactive.PracticeBlockers).ShouldContain(BlockerCodes.AccountNotActive);

        var missing = EligibilityEvaluator.Evaluate(
            null, null, AllConsents(), Options(), Now);
        Codes(missing.PracticeBlockers).ShouldContain(BlockerCodes.AccountNotActive);
    }

    [Fact]
    public void No_Country_Reports_Country_Required()
    {
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, new ComplianceProfile { KycStatus = KycStatus.NotStarted }, AllConsents(), Options(), Now);
        Codes(result.PracticeBlockers).ShouldContain(BlockerCodes.CountryRequired);
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.CountryRequired);
    }

    [Fact]
    public void Blocked_Country_Reports_Country_Not_Supported()
    {
        var profile = new ComplianceProfile { DeclaredCountry = "KP", KycStatus = KycStatus.NotStarted };
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(), Now);
        Codes(result.PracticeBlockers).ShouldContain(BlockerCodes.CountryNotSupported);
        result.PracticeAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Verified_Country_Overrides_Declared()
    {
        var profile = new ComplianceProfile
        {
            DeclaredCountry = "US",
            VerifiedCountry = "NG",
            KycStatus = KycStatus.Approved,
            AttemptsUsed = 1,
            ExpiresAt = Now.AddMonths(23),
            ScreeningStatus = ScreeningStatus.Clear,
        };
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(realMoneyCountries: ["NG"]), Now);
        result.RealAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Verified_Country_In_Blocked_List_Denies_Even_When_Declared_Is_Fine()
    {
        var profile = new ComplianceProfile
        {
            DeclaredCountry = "US",
            VerifiedCountry = "KP",
            KycStatus = KycStatus.Approved,
            AttemptsUsed = 1,
            ExpiresAt = Now.AddMonths(23),
            ScreeningStatus = ScreeningStatus.Clear,
        };
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(), Now);
        Codes(result.PracticeBlockers).ShouldContain(BlockerCodes.CountryNotSupported);
    }

    [Fact]
    public void Missing_Practice_Consents_Block_Practice_And_Real()
    {
        var keys = AllConsents().Where(k => !k.StartsWith("terms", StringComparison.Ordinal)).ToHashSet();
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, ApprovedProfile(), keys, Options(), Now);
        var blocker = result.PracticeBlockers.Single(b => b.Code == BlockerCodes.ConsentRequired);
        blocker.Message.ShouldContain("terms");
        result.PracticeAllowed.ShouldBeFalse();
        result.RealAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Practice_Only_User_Cannot_Reach_Real()
    {
        var keys = AllConsents().Where(k => !k.StartsWith("risk", StringComparison.Ordinal)
            && !k.StartsWith("token", StringComparison.Ordinal)).ToHashSet();
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, ApprovedProfile(), keys, Options(), Now);
        result.PracticeAllowed.ShouldBeTrue();
        result.RealAllowed.ShouldBeFalse();
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.ConsentRequired);
    }

    [Fact]
    public void Kill_Switch_Off_Blocks_Real_With_Real_Money_Disabled()
    {
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, ApprovedProfile(), AllConsents(), Options(realMoneyEnabled: false), Now);
        result.PracticeAllowed.ShouldBeTrue();
        result.RealAllowed.ShouldBeFalse();
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.RealMoneyDisabled);
    }

    [Fact]
    public void Country_Outside_Real_Money_List_Blocks_Real()
    {
        var profile = ApprovedProfile("GB");
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(realMoneyCountries: ["NG"]), Now);
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.CountryNotSupported);
        result.PracticeAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(KycStatus.NotStarted, BlockerCodes.KycRequired)]
    [InlineData(KycStatus.InReview, BlockerCodes.KycInReview)]
    [InlineData(KycStatus.Rejected, BlockerCodes.KycRejected)]
    public void Kyc_Statuses_Map_To_Blockers(KycStatus status, string expected)
    {
        var profile = new ComplianceProfile
        {
            DeclaredCountry = "NG",
            KycStatus = status,
            AttemptsUsed = status == KycStatus.Rejected ? 1 : 0,
            RejectionCode = status == KycStatus.Rejected ? "document_unreadable" : null,
        };
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(), Now);
        Codes(result.RealBlockers).ShouldContain(expected);
    }

    [Fact]
    public void Expired_Kyc_Approval_Reports_Kyc_Expired()
    {
        var profile = ApprovedProfile();
        profile.ExpiresAt = Now.AddDays(-1);
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(), Now);
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.KycExpired);
        result.RealAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Exhausted_Attempts_Report_Kyc_Attempts_Exhausted()
    {
        var profile = new ComplianceProfile
        {
            DeclaredCountry = "NG",
            KycStatus = KycStatus.Rejected,
            AttemptsUsed = 3,
            RejectionCode = "failed",
        };
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(), Now);
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.KycAttemptsExhausted);
    }

    [Fact]
    public void Screening_Hit_Reports_Account_Restricted()
    {
        var profile = ApprovedProfile();
        profile.ScreeningStatus = ScreeningStatus.Hit;
        var result = EligibilityEvaluator.Evaluate(
            VerifiedAccount, profile, AllConsents(), Options(), Now);
        Codes(result.RealBlockers).ShouldContain(BlockerCodes.AccountRestricted);
        result.RealAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Blockers_Accumulate_For_Real()
    {
        var result = EligibilityEvaluator.Evaluate(
            new UserAccountSnapshot(true, false), null, new HashSet<string>(), Options(realMoneyEnabled: false), Now);
        var codes = Codes(result.RealBlockers);
        codes.ShouldContain(BlockerCodes.EmailNotVerified);
        codes.ShouldContain(BlockerCodes.CountryRequired);
        codes.ShouldContain(BlockerCodes.ConsentRequired);
        codes.ShouldContain(BlockerCodes.RealMoneyDisabled);
        codes.ShouldContain(BlockerCodes.KycRequired);
    }
}
