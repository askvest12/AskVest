using AskVest.Modules.Wallets.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class DepositAndAddressTransitionsTests
{
    [Theory]
    [InlineData(DepositState.Pending, DepositState.Credited, true)]
    [InlineData(DepositState.Pending, DepositState.Quarantined, true)]
    [InlineData(DepositState.Pending, DepositState.Ignored, true)]
    [InlineData(DepositState.Credited, DepositState.Pending, false)]
    [InlineData(DepositState.Quarantined, DepositState.Credited, false)]
    [InlineData(DepositState.Ignored, DepositState.Pending, false)]
    [InlineData(DepositState.Pending, DepositState.Pending, false)]
    public void Deposit_Transitions(DepositState from, DepositState to, bool expected)
        => DepositTransitions.CanTransition(from, to).ShouldBe(expected);

    [Theory]
    [InlineData(DepositState.Pending, false)]
    [InlineData(DepositState.Credited, true)]
    [InlineData(DepositState.Quarantined, true)]
    [InlineData(DepositState.Ignored, true)]
    public void Deposit_Terminal_States(DepositState state, bool expected)
        => DepositTransitions.IsTerminal(state).ShouldBe(expected);

    [Theory]
    [InlineData(WithdrawalAddressState.CoolingOff, WithdrawalAddressState.Active, true)]
    [InlineData(WithdrawalAddressState.CoolingOff, WithdrawalAddressState.Removed, true)]
    [InlineData(WithdrawalAddressState.Active, WithdrawalAddressState.Removed, true)]
    [InlineData(WithdrawalAddressState.Active, WithdrawalAddressState.CoolingOff, false)]
    [InlineData(WithdrawalAddressState.Active, WithdrawalAddressState.Active, false)]
    [InlineData(WithdrawalAddressState.Removed, WithdrawalAddressState.Active, false)]
    [InlineData(WithdrawalAddressState.Removed, WithdrawalAddressState.CoolingOff, false)]
    [InlineData(WithdrawalAddressState.CoolingOff, WithdrawalAddressState.CoolingOff, false)]
    public void Address_Transitions(
        WithdrawalAddressState from, WithdrawalAddressState to, bool expected)
        => WithdrawalAddressTransitions.CanTransition(from, to).ShouldBe(expected);
}
