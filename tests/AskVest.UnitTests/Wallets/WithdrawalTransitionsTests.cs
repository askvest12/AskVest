using AskVest.Modules.Wallets.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class WithdrawalTransitionsTests
{
    [Theory]
    [InlineData(WithdrawalState.Previewed, WithdrawalState.Requested, true)]
    [InlineData(WithdrawalState.Requested, WithdrawalState.Submitting, true)]
    [InlineData(WithdrawalState.Requested, WithdrawalState.Cancelled, true)]
    [InlineData(WithdrawalState.Submitting, WithdrawalState.Submitted, true)]
    [InlineData(WithdrawalState.Submitting, WithdrawalState.Failed, true)]
    [InlineData(WithdrawalState.Submitting, WithdrawalState.NeedsReconciliation, true)]
    [InlineData(WithdrawalState.Submitted, WithdrawalState.Completed, true)]
    [InlineData(WithdrawalState.Submitted, WithdrawalState.Failed, true)]
    [InlineData(WithdrawalState.Submitted, WithdrawalState.NeedsReconciliation, true)]
    [InlineData(WithdrawalState.NeedsReconciliation, WithdrawalState.Completed, true)]
    [InlineData(WithdrawalState.NeedsReconciliation, WithdrawalState.Failed, true)]
    public void Legal_Transitions_Are_Allowed(
        WithdrawalState from, WithdrawalState to, bool expected)
        => WithdrawalTransitions.CanTransition(from, to).ShouldBe(expected);

    [Theory]
    [InlineData(WithdrawalState.Previewed, WithdrawalState.Completed)]
    [InlineData(WithdrawalState.Previewed, WithdrawalState.Failed)]
    [InlineData(WithdrawalState.Previewed, WithdrawalState.Cancelled)]
    [InlineData(WithdrawalState.Requested, WithdrawalState.Completed)]
    [InlineData(WithdrawalState.Requested, WithdrawalState.Failed)]
    [InlineData(WithdrawalState.Requested, WithdrawalState.Submitted)]
    [InlineData(WithdrawalState.Submitted, WithdrawalState.Requested)]
    [InlineData(WithdrawalState.Completed, WithdrawalState.Failed)]
    [InlineData(WithdrawalState.Failed, WithdrawalState.Completed)]
    [InlineData(WithdrawalState.Cancelled, WithdrawalState.Requested)]
    [InlineData(WithdrawalState.NeedsReconciliation, WithdrawalState.Submitted)]
    [InlineData(WithdrawalState.NeedsReconciliation, WithdrawalState.Cancelled)]
    public void Illegal_Transitions_Are_Refused(WithdrawalState from, WithdrawalState to)
        => WithdrawalTransitions.CanTransition(from, to).ShouldBeFalse();

    [Theory]
    [InlineData(WithdrawalState.Completed, true)]
    [InlineData(WithdrawalState.Failed, true)]
    [InlineData(WithdrawalState.Cancelled, true)]
    [InlineData(WithdrawalState.Previewed, false)]
    [InlineData(WithdrawalState.Requested, false)]
    [InlineData(WithdrawalState.Submitting, false)]
    [InlineData(WithdrawalState.Submitted, false)]
    [InlineData(WithdrawalState.NeedsReconciliation, false)]
    public void Final_States_Are_Exactly_The_Terminal_Three(
        WithdrawalState state, bool expected)
        => WithdrawalTransitions.IsFinal(state).ShouldBe(expected);

    [Theory]
    [InlineData(WithdrawalState.Requested, true)]
    [InlineData(WithdrawalState.Submitting, true)]
    [InlineData(WithdrawalState.Submitted, true)]
    [InlineData(WithdrawalState.Completed, true)]
    [InlineData(WithdrawalState.NeedsReconciliation, true)]
    [InlineData(WithdrawalState.Previewed, false)]
    [InlineData(WithdrawalState.Failed, false)]
    [InlineData(WithdrawalState.Cancelled, false)]
    public void Rolling_Total_Counts_Committed_Withdrawals_Only(
        WithdrawalState state, bool expected)
        => WithdrawalTransitions.CountsTowardRollingTotal(state).ShouldBe(expected);
}
