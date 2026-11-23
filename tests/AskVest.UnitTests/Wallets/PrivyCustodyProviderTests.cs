using System.Net;
using System.Text;
using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Wallets;

public sealed class PrivyCustodyProviderTests
{
    private const string UsdcContract = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913";
    private const string UserAddress = "0x1111111111111111111111111111111111111111";
    private const string DestinationAddress = "0x2222222222222222222222222222222222222222";
    private const string TokenContract = "0x3333333333333333333333333333333333333333";

    private static PrivyCustodyProvider Provider(
        StubHandler handler,
        Action<WalletsOptions>? configure = null)
    {
        var options = new WalletsOptions();
        options.Custody.Routes.Add(new CustodyRouteOptions
        {
            ChainId = "eip155:8453",
            Provider = PrivyCustodyProvider.ProviderKey,
        });
        options.Custody.Privy.AppId = "app-id";
        options.Custody.Privy.AppSecret = "app-secret";
        options.Custody.Privy.TreasuryWalletId = "treasury-wallet";
        configure?.Invoke(options);

        var stablecoins = new ApprovedStablecoins();
        var usdc = new ApprovedStablecoinOption
        {
            ChainId = "eip155:8453",
            Contract = UsdcContract,
            LedgerAssetCode = "USDC",
            Decimals = 6,
        };
        usdc.Aliases.Add("usdc");
        stablecoins.Replace([usdc]);

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://privy.test"),
        };
        return new PrivyCustodyProvider(
            client,
            Options.Create(options),
            stablecoins,
            TimeProvider.System,
            NullLogger<PrivyCustodyProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Deposit_Instruction_Creates_Wallet_Once_And_Reuses_It()
    {
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"data":[]}""")
                : Json(HttpStatusCode.OK, $$"""{"id":"wallet-1","address":"{{UserAddress}}","chain_type":"ethereum"}"""));
        var provider = Provider(handler);
        var ct = TestContext.Current.CancellationToken;

        var first = await provider.GetDepositInstructionAsync(
            "3fa85f64-5717-4562-b3fc-2c963f66afa6", "eip155:8453", ct);
        first.IsSuccess.ShouldBeTrue();
        first.Value.Address.ShouldBe(UserAddress);
        first.Value.ProviderReference.ShouldBe("wallet-1");
        handler.Requests.Count.ShouldBe(2);

        handler.Respond = request => Json(
            HttpStatusCode.OK,
            $$"""{"data":[{"id":"wallet-1","address":"{{UserAddress}}","chain_type":"ethereum"}],"next_cursor":null}""");
        var second = await provider.GetDepositInstructionAsync(
            "3fa85f64-5717-4562-b3fc-2c963f66afa6", "eip155:8453", ct);
        second.Value.ProviderReference.ShouldBe("wallet-1");
        handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(1);
    }

    [Fact]
    public async Task Non_Evm_Chain_Has_No_Deposit_Instruction()
    {
        var provider = Provider(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")));
        var result = await provider.GetDepositInstructionAsync(
            "3fa85f64-5717-4562-b3fc-2c963f66afa6", "solana:mainnet", TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Deposits_Map_Received_Transfers_With_Finality()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/transactions?"))
            {
                return Json(HttpStatusCode.OK, """
                    {"transactions":[
                        {"caip2":"eip155:8453","transaction_hash":"0xabc","privy_transaction_id":"ptx-1",
                         "status":"confirmed","created_at":1730000000000,"wallet_id":"w1",
                         "details":{"type":"transfer_received","sender":"0xsender","recipient":"0x1111111111111111111111111111111111111111",
                                    "asset":"usdc","raw_value":"150000000","raw_value_decimals":6}},
                        {"caip2":"eip155:8453","transaction_hash":"0xdef","status":"confirmed","created_at":1730000000000,
                         "details":{"type":"transfer_sent","sender":"0x1111111111111111111111111111111111111111","recipient":"0xother",
                                    "asset":"usdc","raw_value":"1","raw_value_decimals":6}}
                    ],"next_cursor":null}
                    """);
            }

            if (url.Contains("/v1/wallets?") || url.EndsWith("/v1/wallets"))
            {
                return Json(HttpStatusCode.OK,
                    $$"""{"data":[{"id":"w1","address":"{{UserAddress}}","chain_type":"ethereum"}],"next_cursor":null}""");
            }

            return Json(HttpStatusCode.NotFound, "{}");
        });
        var provider = Provider(handler);
        var ct = TestContext.Current.CancellationToken;

        var page = await provider.ListDepositsAsync("eip155:8453", null, ct);
        page.IsSuccess.ShouldBeTrue();
        var deposit = page.Value.Deposits.ShouldHaveSingleItem();
        deposit.ProviderDepositId.ShouldBe("eip155:8453:0xabc");
        deposit.ProviderKey.ShouldBe("privy");
        deposit.DestinationAddress.ShouldBe(UserAddress);
        deposit.ChainId.ShouldBe("eip155:8453");
        deposit.TokenContractAddress.ShouldBe(UsdcContract);
        deposit.Quantity.ShouldBe(150m);
        deposit.SenderAddress.ShouldBe("0xsender");
        deposit.SettlementReference.ShouldBe("0xabc");
        deposit.Finality.ShouldBe(CustodyDepositFinality.Final);
    }

    [Fact]
    public async Task Withdrawal_Submit_Sends_Erc20_Transfer_With_Reference_Id()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"data":{"hash":"0xhash","transaction_id":"tx-1","caip2":"eip155:8453"}}"""));
        var provider = Provider(handler);
        var ct = TestContext.Current.CancellationToken;

        var result = await provider.SubmitWithdrawalAsync(new CustodyWithdrawRequest(
            "withdrawal-ref-1",
            "eip155:8453",
            TokenContract,
            6,
            DestinationAddress,
            1.5m), ct);

        result.Kind.ShouldBe(CustodySubmitKind.Acknowledged);
        result.ProviderReference.ShouldBe("tx-1");

        var request = handler.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ToString().ShouldContain("/v1/wallets/treasury-wallet/rpc");
        request.IdempotencyKey.ShouldBe("withdrawal-ref-1");
        var body = handler.Bodies.Single();
        body.ShouldContain("\"method\":\"eth_sendTransaction\"");
        body.ShouldContain("\"caip2\":\"eip155:8453\"");
        body.ShouldContain("\"reference_id\":\"withdrawal-ref-1\"");
        body.ShouldContain($"\"to\":\"{TokenContract}\"");
        body.ShouldContain("0xa9059cbb");
        body.ShouldContain(DestinationAddress[2..].ToLowerInvariant().PadLeft(64, '0'));
        body.ShouldContain("16e360"); // 1.5 * 10^6 = 1500000 in hex
    }

    [Fact]
    public async Task Withdrawal_Status_Maps_Provider_States()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"transactions":[{"id":"t1","caip2":"eip155:8453","transaction_hash":"0xabc","status":"confirmed","created_at":1730000000000}]}"""));
        var provider = Provider(handler);

        var status = await provider.GetWithdrawalStatusAsync(
            "withdrawal-ref-1", TestContext.Current.CancellationToken);
        status.Kind.ShouldBe(CustodyWithdrawalStatusKind.Completed);
        status.SettlementReference.ShouldBe("0xabc");
        handler.Requests.Single().RequestUri.ToString().ShouldContain("reference_id=withdrawal-ref-1");
    }

    [Fact]
    public async Task Withdrawal_Status_Empty_Is_NotFound_And_Failure_Is_Unavailable()
    {
        var empty = Provider(new StubHandler(_ => Json(HttpStatusCode.OK, """{"transactions":[]}""")));
        var notFound = await empty.GetWithdrawalStatusAsync(
            "missing", TestContext.Current.CancellationToken);
        notFound.Kind.ShouldBe(CustodyWithdrawalStatusKind.NotFound);

        var failing = Provider(new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{}")));
        var unavailable = await failing.GetWithdrawalStatusAsync(
            "missing", TestContext.Current.CancellationToken);
        unavailable.Kind.ShouldBe(CustodyWithdrawalStatusKind.Unavailable);
    }

    [Fact]
    public async Task Non_Evm_Withdrawal_Is_Rejected_Definitively()
    {
        var provider = Provider(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")));
        var result = await provider.SubmitWithdrawalAsync(new CustodyWithdrawRequest(
            "ref", "solana:mainnet", "mint", 9, "dest", 1m),
            TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(CustodySubmitKind.Rejected);
        result.ReasonCode.ShouldBe("unsupported_chain_family");
    }

    [Fact]
    public async Task Provider_Server_Error_On_Submit_Is_Unknown_Never_Rejected()
    {
        var provider = Provider(new StubHandler(_ => Json(HttpStatusCode.BadGateway, "{}")));
        var result = await provider.SubmitWithdrawalAsync(new CustodyWithdrawRequest(
            "ref", "eip155:8453", TokenContract, 6, DestinationAddress, 1m),
            TestContext.Current.CancellationToken);
        result.Kind.ShouldBe(CustodySubmitKind.Unknown);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var idempotencyKey = request.Headers.TryGetValues("privy-idempotency-key", out var values)
                ? string.Join(",", values)
                : null;
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, idempotencyKey));
            Bodies.Add(body);
            return Respond(request);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri RequestUri,
        string Body,
        string? IdempotencyKey);
}
