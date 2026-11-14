using System.Globalization;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Wallets.Custody;
using AskVest.Modules.Wallets.Domain;
using AskVest.Modules.Wallets.Endpoints;
using AskVest.Modules.Wallets.Persistence.Entities;
using AskVest.Modules.Wallets.Screening;
using AskVest.Modules.Wallets.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Wallets.Endpoints;

public static class WalletsEndpoints
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] CustodyScenarios =
    [
        "normal",
        "rejected",
        "submit_timeout_lost",
        "submit_timeout_filled",
        "delayed",
        "status_unavailable",
        "holdings_mismatch",
    ];

    private static readonly string[] ScreeningScenarios = ["normal", "error"];

    public static void Map(RouteGroupBuilder api, bool probesEnabled = false)
    {
        if (probesEnabled)
        {
            api.MapPost("/diagnostics/wallets-custody-scenario", CustodyScenarioProbeAsync)
                .WithName("WalletsCustodyScenarioProbe")
                .WithSummary("Diagnostic: set the fake custody provider scenario.")
                .WithTags("Diagnostics")
                .WithMetadata(new RequiresIdempotencyAttribute());

            api.MapPost("/diagnostics/wallets-screening-scenario", ScreeningScenarioProbeAsync)
                .WithName("WalletsScreeningScenarioProbe")
                .WithSummary("Diagnostic: set the fake screening provider scenario.")
                .WithTags("Diagnostics")
                .WithMetadata(new RequiresIdempotencyAttribute());

            api.MapPost("/diagnostics/wallets-deposit", DepositInjectionProbeAsync)
                .WithName("WalletsDepositProbe")
                .WithSummary("Diagnostic: inject a fake custody deposit.")
                .WithTags("Diagnostics")
                .WithMetadata(new RequiresIdempotencyAttribute());
        }

        var real = api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible);

        real.MapGet("/wallets", ListWalletsAsync)
            .WithName("ListWallets")
            .WithSummary("The caller's deposit instructions per chain.")
            .WithTags("Real Wallets");

        real.MapGet("/wallets/{chainId}", GetWalletAsync)
            .WithName("GetWallet")
            .WithSummary("The caller's deposit instruction for one chain.")
            .WithTags("Real Wallets");

        real.MapGet("/deposits", ListDepositsAsync)
            .WithName("ListDeposits")
            .WithSummary("The caller's detected deposits, newest first.")
            .WithTags("Real Wallets");

        real.MapGet("/withdrawal-addresses", ListAddressesAsync)
            .WithName("ListWithdrawalAddresses")
            .WithSummary("The caller's withdrawal address book.")
            .WithTags("Real Wallets");

        real.MapPost("/withdrawal-addresses", AddAddressAsync)
            .WithName("AddWithdrawalAddress")
            .WithSummary("Add a withdrawal destination after step-up and screening.")
            .WithTags("Real Wallets")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapDelete("/withdrawal-addresses/{addressId:guid}", RemoveAddressAsync)
            .WithName("RemoveWithdrawalAddress")
            .WithSummary("Remove a withdrawal destination. Idempotent by design.")
            .WithTags("Real Wallets");

        real.MapPost("/withdrawals/previews", CreatePreviewAsync)
            .WithName("CreateWithdrawalPreview")
            .WithSummary("Preview a withdrawal with frozen terms and a terms hash.")
            .WithTags("Real Wallets")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapPost("/withdrawals", PlaceWithdrawalAsync)
            .WithName("PlaceWithdrawal")
            .WithSummary("Place a withdrawal against a preview and step-up grant.")
            .WithTags("Real Wallets")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapGet("/withdrawals", ListWithdrawalsAsync)
            .WithName("ListWithdrawals")
            .WithSummary("The caller's withdrawals, newest first.")
            .WithTags("Real Wallets");

        real.MapGet("/withdrawals/{withdrawalId:guid}", GetWithdrawalAsync)
            .WithName("GetWithdrawal")
            .WithSummary("One of the caller's withdrawals.")
            .WithTags("Real Wallets");

        real.MapPost("/withdrawals/{withdrawalId:guid}/cancel", CancelWithdrawalAsync)
            .WithName("CancelWithdrawal")
            .WithSummary("Cancel a withdrawal while cancellation is still legal.")
            .WithTags("Real Wallets")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> ListWalletsAsync(
        HttpContext httpContext,
        WalletService wallets,
        CancellationToken cancellationToken)
    {
        var result = await wallets.ListWalletsAsync(CurrentUserId(httpContext), cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(
                result.Value.Select(ToWalletResponse).ToArray(), "Wallets loaded.", httpContext);
    }

    private static async Task<IResult> GetWalletAsync(
        string chainId,
        HttpContext httpContext,
        WalletService wallets,
        CancellationToken cancellationToken)
    {
        var result = await wallets.GetWalletAsync(CurrentUserId(httpContext), chainId, cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToWalletResponse(result.Value), "Wallet loaded.", httpContext);
    }

    private static async Task<IResult> ListDepositsAsync(
        HttpContext httpContext,
        WalletService wallets,
        int? limit,
        CancellationToken cancellationToken)
    {
        var result = await wallets.ListDepositsAsync(
            CurrentUserId(httpContext), limit ?? 50, cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(
                result.Value.Select(ToDepositResponse).ToArray(), "Deposits loaded.", httpContext);
    }

    private static async Task<IResult> ListAddressesAsync(
        HttpContext httpContext,
        AddressBookService addresses,
        CancellationToken cancellationToken)
    {
        var result = await addresses.ListAsync(CurrentUserId(httpContext), cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(
                result.Value.Select(ToAddressResponse).ToArray(), "Addresses loaded.", httpContext);
    }

    private static async Task<IResult> AddAddressAsync(
        AddAddressRequest request,
        HttpContext httpContext,
        AddressBookService addresses,
        CancellationToken cancellationToken)
    {
        var sessionId = SessionId(httpContext);
        if (sessionId is null)
        {
            return WalletsResults.Error(new Error(
                ErrorCodes.AuthUnauthenticated,
                "The session is not authenticated.",
                ErrorType.Unauthenticated), httpContext);
        }

        var result = await addresses.AddAsync(
            CurrentUserId(httpContext),
            sessionId.Value,
            request.ChainId,
            request.Address,
            request.Label,
            request.StepUpToken,
            WalletsResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToAddressResponse(result.Value), "Address added.", httpContext);
    }

    private static async Task<IResult> RemoveAddressAsync(
        Guid addressId,
        HttpContext httpContext,
        AddressBookService addresses,
        CancellationToken cancellationToken)
    {
        var result = await addresses.RemoveAsync(
            CurrentUserId(httpContext), addressId, cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToAddressResponse(result.Value), "Address removed.", httpContext);
    }

    private static async Task<IResult> CreatePreviewAsync(
        PreviewWithdrawalRequest request,
        HttpContext httpContext,
        WithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        if (!decimal.TryParse(request.GrossQuantity, NumberStyles.Number, Invariant, out var gross))
        {
            return WalletsResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "GrossQuantity must be a positive decimal.",
                ErrorType.Validation), httpContext);
        }

        var result = await withdrawals.CreatePreviewAsync(
            CurrentUserId(httpContext), request.TokenId, request.AddressId, gross, cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToWithdrawalResponse(result.Value), "Preview created.", httpContext);
    }

    private static async Task<IResult> PlaceWithdrawalAsync(
        PlaceWithdrawalRequest request,
        HttpContext httpContext,
        WithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        var sessionId = SessionId(httpContext);
        if (sessionId is null)
        {
            return WalletsResults.Error(new Error(
                ErrorCodes.AuthUnauthenticated,
                "The session is not authenticated.",
                ErrorType.Unauthenticated), httpContext);
        }

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        var result = await withdrawals.PlaceAsync(
            CurrentUserId(httpContext),
            sessionId.Value,
            request.PreviewId,
            request.StepUpToken,
            idempotencyKey,
            WalletsResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToWithdrawalResponse(result.Value), "Withdrawal placed.", httpContext);
    }

    private static async Task<IResult> ListWithdrawalsAsync(
        HttpContext httpContext,
        WithdrawalService withdrawals,
        int? limit,
        CancellationToken cancellationToken)
    {
        var result = await withdrawals.ListAsync(
            CurrentUserId(httpContext), limit ?? 50, cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(
                result.Value.Select(ToWithdrawalResponse).ToArray(), "Withdrawals loaded.", httpContext);
    }

    private static async Task<IResult> GetWithdrawalAsync(
        Guid withdrawalId,
        HttpContext httpContext,
        WithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        var result = await withdrawals.GetAsync(
            CurrentUserId(httpContext), withdrawalId, cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToWithdrawalResponse(result.Value), "Withdrawal loaded.", httpContext);
    }

    private static async Task<IResult> CancelWithdrawalAsync(
        Guid withdrawalId,
        HttpContext httpContext,
        WithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        var result = await withdrawals.CancelAsync(
            CurrentUserId(httpContext),
            withdrawalId,
            WalletsResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? WalletsResults.Error(result.Error!, httpContext)
            : WalletsResults.Ok(ToWithdrawalResponse(result.Value), "Withdrawal cancelled.", httpContext);
    }

    private static object ToWalletResponse(WalletAddress address)
        => new
        {
            id = address.Id,
            chainId = address.ChainId,
            address = address.Address,
            memo = string.IsNullOrEmpty(address.Memo) ? null : address.Memo,
            createdAt = address.CreatedAt,
        };

    private static object ToDepositResponse(Deposit deposit)
        => new
        {
            id = deposit.Id,
            chainId = deposit.ChainId,
            tokenId = deposit.TokenId == Guid.Empty ? (Guid?)null : deposit.TokenId,
            quantity = Money(deposit.Quantity),
            state = WalletsText.ToDb(deposit.State),
            detailCode = deposit.DetailCode,
            detectedAt = deposit.DetectedAt,
            creditedAt = deposit.CreditedAt,
        };

    private static object ToAddressResponse(WithdrawalAddress address)
        => new
        {
            id = address.Id,
            chainId = address.ChainId,
            address = address.CanonicalAddress,
            label = string.IsNullOrEmpty(address.Label) ? null : address.Label,
            state = WalletsText.ToDb(address.State),
            activeFrom = address.ActiveFrom,
            createdAt = address.CreatedAt,
        };

    private static object ToWithdrawalResponse(Withdrawal withdrawal)
        => new
        {
            id = withdrawal.Id,
            tokenId = withdrawal.TokenId,
            addressId = withdrawal.AddressId,
            chainId = withdrawal.DestinationChainId,
            destinationAddress = withdrawal.DestinationAddress,
            grossQuantity = Money(withdrawal.GrossQuantity),
            fee = Money(withdrawal.Fee),
            netQuantity = Money(withdrawal.NetQuantity),
            termsHash = withdrawal.TermsHash,
            state = WalletsText.ToDb(withdrawal.State),
            expiresAt = withdrawal.ExpiresAt,
            settlementReference = withdrawal.SettlementReference,
            detailCode = withdrawal.DetailCode,
            createdAt = withdrawal.CreatedAt,
            requestedAt = withdrawal.RequestedAt,
            submittedAt = withdrawal.SubmittedAt,
            completedAt = withdrawal.CompletedAt,
            failedAt = withdrawal.FailedAt,
            cancelledAt = withdrawal.CancelledAt,
        };

    private static string Money(decimal value) => value.ToString(Invariant);

    private static async Task<IResult> CustodyScenarioProbeAsync(
        HttpContext httpContext,
        CustodyScenarioRequest request,
        FakeCustodyProvider custody,
        CancellationToken cancellationToken)
    {
        var scenario = request.Scenario?.Trim() ?? string.Empty;
        if (!CustodyScenarios.Contains(scenario, StringComparer.Ordinal))
        {
            return WalletsResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "The scenario is not recognized.",
                ErrorType.Validation), httpContext);
        }

        custody.SetScenario(scenario);
        return WalletsResults.Ok(new { applied = true }, "Scenario set.", httpContext);
    }

    private static async Task<IResult> ScreeningScenarioProbeAsync(
        HttpContext httpContext,
        ScreeningScenarioRequest request,
        FakeAddressScreeningProvider screening,
        CancellationToken cancellationToken)
    {
        var scenario = request.Scenario?.Trim() ?? string.Empty;
        if (!ScreeningScenarios.Contains(scenario, StringComparer.Ordinal))
        {
            return WalletsResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "The scenario is not recognized.",
                ErrorType.Validation), httpContext);
        }

        screening.SetScenario(scenario);
        return WalletsResults.Ok(new { applied = true }, "Scenario set.", httpContext);
    }

    private static async Task<IResult> DepositInjectionProbeAsync(
        HttpContext httpContext,
        DepositInjectionRequest request,
        FakeCustodyProvider custody,
        CancellationToken cancellationToken)
    {
        if (!decimal.TryParse(request.Quantity, NumberStyles.Number, Invariant, out var quantity)
            || quantity <= 0m)
        {
            return WalletsResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "Quantity must be a positive decimal.",
                ErrorType.Validation), httpContext);
        }

        var finality = string.Equals(request.Finality, "pending", StringComparison.Ordinal)
            ? CustodyDepositFinality.Pending
            : CustodyDepositFinality.Final;
        custody.InjectDepositTo(
            request.ChainId,
            request.DestinationAddress,
            request.TokenContractAddress,
            quantity,
            request.SenderAddress,
            finality);
        return WalletsResults.Ok(new { applied = true }, "Deposit injected.", httpContext);
    }

    private static Guid? SessionId(HttpContext httpContext)
        => Guid.TryParse(httpContext.User.FindFirst("sid")?.Value, out var sessionId)
            ? sessionId
            : null;

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
