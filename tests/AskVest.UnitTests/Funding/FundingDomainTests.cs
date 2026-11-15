using AskVest.Modules.Funding.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Funding;

public sealed class FundingDomainTests
{
    [Fact]
    public void Deposit_Transitions()
    {
        var legal = new (DepositState From, DepositState To)[]
        {
            (DepositState.Pending, DepositState.Credited),
            (DepositState.Pending, DepositState.Quarantined),
            (DepositState.Pending, DepositState.Failed),
            (DepositState.Pending, DepositState.Ignored),
            (DepositState.Credited, DepositState.Reversed),
            (DepositState.Quarantined, DepositState.Credited),
            (DepositState.Quarantined, DepositState.Failed),
        };
        foreach (var (from, to) in legal)
        {
            DepositTransitions.CanTransition(from, to).ShouldBeTrue($"{from} -> {to}");
        }

        var illegal = new (DepositState From, DepositState To)[]
        {
            (DepositState.Credited, DepositState.Credited),
            (DepositState.Reversed, DepositState.Credited),
            (DepositState.Ignored, DepositState.Pending),
            (DepositState.Failed, DepositState.Credited),
            (DepositState.Pending, DepositState.Reversed),
        };
        foreach (var (from, to) in illegal)
        {
            DepositTransitions.CanTransition(from, to).ShouldBeFalse($"{from} -> {to}");
        }
    }

    [Fact]
    public void Conversion_Transitions()
    {
        ConversionTransitions.CanTransition(ConversionState.Quoted, ConversionState.Completed).ShouldBeTrue();
        ConversionTransitions.CanTransition(ConversionState.Quoted, ConversionState.Expired).ShouldBeTrue();
        ConversionTransitions.CanTransition(ConversionState.Completed, ConversionState.Expired).ShouldBeFalse();
        ConversionTransitions.CanTransition(ConversionState.Expired, ConversionState.Completed).ShouldBeFalse();
    }

    [Fact]
    public void Beneficiary_Transitions()
    {
        BeneficiaryTransitions.CanTransition(BeneficiaryState.CoolingOff, BeneficiaryState.Active).ShouldBeTrue();
        BeneficiaryTransitions.CanTransition(BeneficiaryState.CoolingOff, BeneficiaryState.Removed).ShouldBeTrue();
        BeneficiaryTransitions.CanTransition(BeneficiaryState.Active, BeneficiaryState.Removed).ShouldBeTrue();
        BeneficiaryTransitions.CanTransition(BeneficiaryState.Active, BeneficiaryState.CoolingOff).ShouldBeFalse();
        BeneficiaryTransitions.CanTransition(BeneficiaryState.Removed, BeneficiaryState.Active).ShouldBeFalse();
    }

    [Fact]
    public void Withdrawal_Transitions()
    {
        var legal = new (WithdrawalState From, WithdrawalState To)[]
        {
            (WithdrawalState.Previewed, WithdrawalState.Requested),
            (WithdrawalState.Previewed, WithdrawalState.UnderReview),
            (WithdrawalState.Requested, WithdrawalState.Submitting),
            (WithdrawalState.Requested, WithdrawalState.Cancelled),
            (WithdrawalState.UnderReview, WithdrawalState.Requested),
            (WithdrawalState.UnderReview, WithdrawalState.Failed),
            (WithdrawalState.Submitting, WithdrawalState.Submitted),
            (WithdrawalState.Submitting, WithdrawalState.Failed),
            (WithdrawalState.Submitting, WithdrawalState.NeedsReconciliation),
            (WithdrawalState.Submitted, WithdrawalState.Completed),
            (WithdrawalState.Submitted, WithdrawalState.Failed),
            (WithdrawalState.Submitted, WithdrawalState.NeedsReconciliation),
            (WithdrawalState.NeedsReconciliation, WithdrawalState.Completed),
            (WithdrawalState.NeedsReconciliation, WithdrawalState.Failed),
        };
        foreach (var state in Enum.GetValues<WithdrawalState>())
        {
            foreach (var target in Enum.GetValues<WithdrawalState>())
            {
                var expected = legal.Contains((state, target));
                WithdrawalTransitions.CanTransition(state, target)
                    .ShouldBe(expected, $"{state} -> {target}");
            }
        }
    }

    [Fact]
    public void Withdrawal_Rolling_Cap_Counts_Committed_Only()
    {
        var counting = new[]
        {
            WithdrawalState.Requested, WithdrawalState.UnderReview, WithdrawalState.Submitting,
            WithdrawalState.Submitted, WithdrawalState.Completed, WithdrawalState.NeedsReconciliation,
        };
        foreach (var state in Enum.GetValues<WithdrawalState>())
        {
            WithdrawalTransitions.CountsTowardRollingTotal(state)
                .ShouldBe(counting.Contains(state), state.ToString());
        }
    }

    [Fact]
    public void Conversion_Math_Exact()
    {
        // 100 USD at mid 1500, 50 bps spread, 100 bps fee, target scale 2.
        var quote = FundingMath.ComputeQuote(
            "USD:NGN", "USD", "NGN", 100m, 1500m, 2, 2, 50, 100);

        quote.EffectiveRate.ShouldBe(1492.50m);
        quote.ReceiveAmount.ShouldBe(149250.00m);
        quote.FeeAmount.ShouldBe(1492.50m);
        quote.NetAmount.ShouldBe(147757.50m);
        quote.TermsHash.ShouldBe(FundingMath.ComputeQuote(
            "USD:NGN", "USD", "NGN", 100m, 1500m, 2, 2, 50, 100).TermsHash);
    }

    [Fact]
    public void Conversion_Math_Rounds_Down_Receive_And_Half_Up_Fee()
    {
        // Receive must round down; fee rounds half up at the target scale.
        var quote = FundingMath.ComputeQuote(
            "USD:NGN", "USD", "NGN", 1m, 3m, 2, 2, 0, 333);

        // 1 * 3 = 3.00 receive; fee = 3.00 * 0.0333 = 0.0999 -> 0.10 half up.
        quote.ReceiveAmount.ShouldBe(3.00m);
        quote.FeeAmount.ShouldBe(0.10m);
        quote.NetAmount.ShouldBe(2.90m);
    }

    [Fact]
    public void Conversion_Terms_Hash_Changes_With_Any_Term()
    {
        var baseline = FundingMath.ComputeQuote("USD:NGN", "USD", "NGN", 10m, 1500m, 2, 2, 50, 100);
        var differentAmount = FundingMath.ComputeQuote("USD:NGN", "USD", "NGN", 11m, 1500m, 2, 2, 50, 100);
        var differentSpread = FundingMath.ComputeQuote("USD:NGN", "USD", "NGN", 10m, 1500m, 2, 2, 51, 100);
        var differentFee = FundingMath.ComputeQuote("USD:NGN", "USD", "NGN", 10m, 1500m, 2, 2, 50, 101);

        differentAmount.TermsHash.ShouldNotBe(baseline.TermsHash);
        differentSpread.TermsHash.ShouldNotBe(baseline.TermsHash);
        differentFee.TermsHash.ShouldNotBe(baseline.TermsHash);
    }

    [Fact]
    public void Withdrawal_Terms_Hash_Changes_With_Any_Term()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var beneficiary = Guid.CreateVersion7();
        var baseline = FundingMath.WithdrawalTermsHash(beneficiary, "USD", 100m, 1m, 101m, expires);
        var otherAmount = FundingMath.WithdrawalTermsHash(beneficiary, "USD", 100.01m, 1m, 101.01m, expires);
        var otherCurrency = FundingMath.WithdrawalTermsHash(beneficiary, "NGN", 100m, 1m, 101m, expires);
        var otherExpiry = FundingMath.WithdrawalTermsHash(beneficiary, "USD", 100m, 1m, 101m, expires.AddMinutes(1));

        otherAmount.ShouldNotBe(baseline);
        otherCurrency.ShouldNotBe(baseline);
        otherExpiry.ShouldNotBe(baseline);
    }

    [Fact]
    public void Fresh_Deposit_Lock()
    {
        FundingMath.WithdrawableAfterFreshDepositLock(100m, 0m).ShouldBe(100m);
        FundingMath.WithdrawableAfterFreshDepositLock(100m, 40m).ShouldBe(60m);
        FundingMath.WithdrawableAfterFreshDepositLock(100m, 150m).ShouldBe(0m);
        FundingMath.WithdrawableAfterFreshDepositLock(0m, 10m).ShouldBe(0m);
    }

    [Fact]
    public void Reserves_Shortfall_Only_When_Owed_Exceeds_Held()
    {
        FundingMath.ReservesShortfall(100m, 100m).ShouldBe(0m);
        FundingMath.ReservesShortfall(100m, 150m).ShouldBe(0m);
        FundingMath.ReservesShortfall(150m, 100m).ShouldBe(50m);
        FundingMath.ReservesShortfall(0m, 0m).ShouldBe(0m);
    }

    [Fact]
    public void FundingText_Roundtrips_All_States()
    {
        foreach (var state in Enum.GetValues<DepositState>())
        {
            FundingText.DepositStateFromDb(FundingText.ToDb(state)).ShouldBe(state);
        }

        foreach (var state in Enum.GetValues<WithdrawalState>())
        {
            FundingText.WithdrawalStateFromDb(FundingText.ToDb(state)).ShouldBe(state);
        }

        foreach (var state in Enum.GetValues<ConversionState>())
        {
            FundingText.ConversionStateFromDb(FundingText.ToDb(state)).ShouldBe(state);
        }

        foreach (var state in Enum.GetValues<BeneficiaryState>())
        {
            FundingText.BeneficiaryStateFromDb(FundingText.ToDb(state)).ShouldBe(state);
        }
    }
}
