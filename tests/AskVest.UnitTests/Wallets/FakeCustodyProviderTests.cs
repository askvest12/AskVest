using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class FakeCustodyProviderTests
{
    private static FakeCustodyProvider Provider(string scenario = "normal")
    {
        var options = Options.Create(new WalletsOptions
        {
            Custody =
            {
                Routes =
                {
                    new CustodyRouteOptions { ChainId = "eip155:11155111", Provider = "fake" },
                },
                Fake = { Scenario = scenario, CompletionDelaySeconds = 2, PendingFinalitySeconds = 30 },
            },
        });
        return new FakeCustodyProvider(options, TimeProvider.System);
    }

    [Fact]
    public async Task Deposit_Instructions_Are_Deterministic_Per_User_And_Chain()
    {
        var provider = Provider();
        var ct = TestContext.Current.CancellationToken;
        var first = await provider.GetDepositInstructionAsync("user-1", "eip155:11155111", ct);
        var second = await provider.GetDepositInstructionAsync("user-1", "eip155:11155111", ct);
        first.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(second.Value);
        first.Value.ProviderReference.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unknown_Chain_Has_No_Custody()
    {
        var provider = Provider();
        var result = await provider.GetDepositInstructionAsync(
            "user-1", "eip155:999", TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Withdrawal_Submit_Is_Idempotent_By_Our_Reference()
    {
        var provider = Provider();
        var ct = TestContext.Current.CancellationToken;
        var request = new CustodyWithdrawRequest(
            "ref-1", "eip155:11155111", "0xabc", 18, "dest-1", 5m);
        var first = await provider.SubmitWithdrawalAsync(request, ct);
        var second = await provider.SubmitWithdrawalAsync(request, ct);
        first.Kind.ShouldBe(CustodySubmitKind.Acknowledged);
        second.Kind.ShouldBe(CustodySubmitKind.Acknowledged);
        provider.WithdrawalCount("ref-1").ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_Scenario_Rejects_Submissions()
    {
        var provider = Provider("rejected");
        var result = await provider.SubmitWithdrawalAsync(
            new CustodyWithdrawRequest("ref-2", "eip155:11155111", "0xt", 18, "dest", 1m),
            TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(CustodySubmitKind.Rejected);
    }

    [Fact]
    public async Task Submit_Timeout_Lost_Acks_But_The_Withdrawal_Is_Gone()
    {
        var provider = Provider("submit_timeout_lost");
        var ct = TestContext.Current.CancellationToken;
        var result = await provider.SubmitWithdrawalAsync(
            new CustodyWithdrawRequest("ref-3", "eip155:11155111", "0xt", 18, "dest", 1m), ct);
        result.Kind.ShouldBe(CustodySubmitKind.Acknowledged);
        var status = await provider.GetWithdrawalStatusAsync("ref-3", ct);
        status.Kind.ShouldBe(CustodyWithdrawalStatusKind.NotFound);
    }

    [Fact]
    public async Task Submit_Timeout_Filled_Reports_Unknown_But_The_Withdrawal_Exists()
    {
        var provider = Provider("submit_timeout_filled");
        var ct = TestContext.Current.CancellationToken;
        var result = await provider.SubmitWithdrawalAsync(
            new CustodyWithdrawRequest("ref-5", "eip155:11155111", "0xt", 18, "dest", 1m), ct);
        result.Kind.ShouldBe(CustodySubmitKind.Unknown);
        var status = await provider.GetWithdrawalStatusAsync("ref-5", ct);
        status.Kind.ShouldBe(CustodyWithdrawalStatusKind.Pending);
    }

    [Fact]
    public async Task Withdrawal_Completes_After_The_Configured_Delay()
    {
        var provider = Provider();
        var ct = TestContext.Current.CancellationToken;
        await provider.SubmitWithdrawalAsync(
            new CustodyWithdrawRequest("ref-4", "eip155:11155111", "0xt", 18, "dest", 2m), ct);
        var early = await provider.GetWithdrawalStatusAsync("ref-4", ct);
        early.Kind.ShouldBe(CustodyWithdrawalStatusKind.Pending);
        await Task.Delay(2100, ct);
        var late = await provider.GetWithdrawalStatusAsync("ref-4", ct);
        late.Kind.ShouldBe(CustodyWithdrawalStatusKind.Completed);
        late.SettlementReference.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_Unavailable_Scenario_Fails_Status_Polls()
    {
        var provider = Provider("status_unavailable");
        var status = await provider.GetWithdrawalStatusAsync(
            "anything", TestContext.Current.CancellationToken);
        status.Kind.ShouldBe(CustodyWithdrawalStatusKind.Unavailable);
    }

    [Fact]
    public async Task Issued_Deposit_Addresses_Are_Internal()
    {
        var provider = Provider();
        var ct = TestContext.Current.CancellationToken;
        var instruction = (await provider.GetDepositInstructionAsync("user-9", "eip155:11155111", ct)).Value;
        var internalResult = await provider.IsInternalAddressAsync(
            "eip155:11155111", instruction.Address, ct);
        internalResult.Value.ShouldBeTrue();
        var externalResult = await provider.IsInternalAddressAsync(
            "eip155:11155111", "some-external-address", ct);
        externalResult.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task Pending_Deposits_Report_Final_After_The_Pending_Window()
    {
        var options = Options.Create(new WalletsOptions
        {
            Custody =
            {
                Routes = { new CustodyRouteOptions { ChainId = "eip155:11155111", Provider = "fake" } },
                Fake = { PendingFinalitySeconds = 0 },
            },
        });
        var provider = new FakeCustodyProvider(options, TimeProvider.System);
        provider.InjectDepositTo(
            "eip155:11155111", "dest-1", "contract-1", 3m, "sender-1",
            CustodyDepositFinality.Pending);
        var page = (await provider.ListDepositsAsync(
            "eip155:11155111", null, TestContext.Current.CancellationToken)).Value;
        page.Deposits.ShouldHaveSingleItem().Finality.ShouldBe(CustodyDepositFinality.Final);
    }
}
