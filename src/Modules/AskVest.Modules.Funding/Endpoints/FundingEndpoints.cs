using System.Text;
using System.Text.Json;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Endpoints;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Funding.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Funding.Endpoints;

public static class FundingEndpoints
{
    public static void Map(RouteGroupBuilder api, bool probesEnabled = false)
    {
        if (probesEnabled)
        {
            api.MapPost("/diagnostics/funding", ScenarioProbeAsync)
                .WithName("FundingScenarioProbe")
                .WithSummary("Diagnostic: set a fake funding or FX provider scenario.")
                .WithTags("Diagnostics")
                .WithMetadata(new RequiresIdempotencyAttribute());
        }

        api.MapPost("/webhooks/funding/{provider}", ReceiveWebhookAsync)
            .WithName("ReceiveFundingWebhook")
            .WithSummary("Receive a signed funding provider event.")
            .WithTags("Webhooks")
            .WithMetadata(new IdempotencyExemptAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        var real = api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible);

        real.MapGet("/funding/methods", ListMethodsAsync)
            .WithName("ListFundingMethods")
            .WithSummary("Funding methods available for the caller's country.")
            .WithTags("Real Funding");

        real.MapPost("/funding/accounts", CreateAccountAsync)
            .WithName("CreateFundingAccount")
            .WithSummary("Create or return the caller's funding instruction for a currency.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapGet("/funding/accounts", ListAccountsAsync)
            .WithName("ListFundingAccounts")
            .WithSummary("The caller's funding instructions.")
            .WithTags("Real Funding");

        real.MapGet("/funding/deposits", ListDepositsAsync)
            .WithName("ListFundingDeposits")
            .WithSummary("The caller's funding deposits, newest first.")
            .WithTags("Real Funding");

        real.MapGet("/funding/deposits/{depositId:guid}", GetDepositAsync)
            .WithName("GetFundingDeposit")
            .WithSummary("One of the caller's funding deposits.")
            .WithTags("Real Funding");

        real.MapPost("/funding/conversions/quotes", CreateConversionQuoteAsync)
            .WithName("CreateFundingConversionQuote")
            .WithSummary("Create a firm internal currency conversion quote.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapPost("/funding/conversions", ExecuteConversionAsync)
            .WithName("ExecuteFundingConversion")
            .WithSummary("Execute a conversion quote.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapGet("/funding/conversions", ListConversionsAsync)
            .WithName("ListFundingConversions")
            .WithSummary("The caller's conversions, newest first.")
            .WithTags("Real Funding");

        real.MapGet("/funding/beneficiaries", ListBeneficiariesAsync)
            .WithName("ListFundingBeneficiaries")
            .WithSummary("The caller's withdrawal beneficiaries, masked.")
            .WithTags("Real Funding");

        real.MapPost("/funding/beneficiaries", AddBeneficiaryAsync)
            .WithName("AddFundingBeneficiary")
            .WithSummary("Verify and add a withdrawal beneficiary; it cools off before use.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapDelete("/funding/beneficiaries/{beneficiaryId:guid}", RemoveBeneficiaryAsync)
            .WithName("RemoveFundingBeneficiary")
            .WithSummary("Remove a withdrawal beneficiary. Repeating is harmless.")
            .WithTags("Real Funding")
            .WithMetadata(new IdempotencyExemptAttribute());

        real.MapPost("/funding/withdrawals/previews", CreateWithdrawalPreviewAsync)
            .WithName("CreateFundingWithdrawalPreview")
            .WithSummary("Preview a cash withdrawal: amount, fee, total and terms hash.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapPost("/funding/withdrawals", PlaceWithdrawalAsync)
            .WithName("PlaceFundingWithdrawal")
            .WithSummary("Place a cash withdrawal from a preview.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapGet("/funding/withdrawals", ListWithdrawalsAsync)
            .WithName("ListFundingWithdrawals")
            .WithSummary("The caller's cash withdrawals, newest first.")
            .WithTags("Real Funding");

        real.MapGet("/funding/withdrawals/{withdrawalId:guid}", GetWithdrawalAsync)
            .WithName("GetFundingWithdrawal")
            .WithSummary("One of the caller's cash withdrawals.")
            .WithTags("Real Funding");

        real.MapPost("/funding/withdrawals/{withdrawalId:guid}/cancel", CancelWithdrawalAsync)
            .WithName("CancelFundingWithdrawal")
            .WithSummary("Cancel a requested cash withdrawal and release the hold.")
            .WithTags("Real Funding")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> ListMethodsAsync(
        HttpContext httpContext,
        FundingAccountService accounts,
        CancellationToken cancellationToken)
    {
        var result = await accounts.ListMethodsAsync(CurrentUserId(httpContext), cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(
                result.Value.Select(ToMethodResponse).ToArray(), "Methods loaded.", httpContext);
    }

    private static async Task<IResult> CreateAccountAsync(
        CreateFundingAccountRequest request,
        HttpContext httpContext,
        FundingAccountService accounts,
        CancellationToken cancellationToken)
    {
        var result = await accounts.CreateAccountAsync(
            CurrentUserId(httpContext), request.Currency ?? string.Empty, cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToAccountResponse(result.Value), "Funding account ready.", httpContext);
    }

    private static async Task<IResult> ListAccountsAsync(
        HttpContext httpContext,
        FundingAccountService accounts,
        CancellationToken cancellationToken)
    {
        var result = await accounts.ListAccountsAsync(CurrentUserId(httpContext), cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(
                result.Value.Select(ToAccountResponse).ToArray(), "Accounts loaded.", httpContext);
    }

    private static async Task<IResult> ListDepositsAsync(
        HttpContext httpContext,
        FundingAccountService accounts,
        string? state,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        DepositState? filter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            filter = state.Trim().ToLowerInvariant() switch
            {
                "pending" => DepositState.Pending,
                "credited" => DepositState.Credited,
                "quarantined" => DepositState.Quarantined,
                "failed" => DepositState.Failed,
                "reversed" => DepositState.Reversed,
                "ignored" => DepositState.Ignored,
                _ => null,
            };
            if (filter is null)
            {
                return FundingResults.Error(new Error(
                    ErrorCodes.ValidationFailed,
                    "The state filter is not recognized.",
                    ErrorType.Validation), httpContext);
            }
        }

        var result = await accounts.ListDepositsAsync(
            CurrentUserId(httpContext), filter, cursor, limit ?? 50, cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(
                new
                {
                    items = result.Value.Items.Select(ToDepositResponse).ToArray(),
                    nextCursor = result.Value.NextCursor,
                    hasMore = result.Value.HasMore,
                },
                "Deposits loaded.", httpContext);
    }

    private static async Task<IResult> GetDepositAsync(
        Guid depositId,
        HttpContext httpContext,
        FundingAccountService accounts,
        CancellationToken cancellationToken)
    {
        var result = await accounts.GetDepositAsync(
            CurrentUserId(httpContext), depositId, cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToDepositResponse(result.Value), "Deposit loaded.", httpContext);
    }

    private static async Task<IResult> CreateConversionQuoteAsync(
        CreateConversionQuoteRequest request,
        HttpContext httpContext,
        FundingConversionService conversions,
        CancellationToken cancellationToken)
    {
        var result = await conversions.CreateQuoteAsync(
            CurrentUserId(httpContext),
            request.Pair ?? string.Empty,
            request.SourceAmount ?? string.Empty,
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToConversionResponse(result.Value), "Quote created.", httpContext);
    }

    private static async Task<IResult> ExecuteConversionAsync(
        ExecuteConversionRequest request,
        HttpContext httpContext,
        FundingConversionService conversions,
        CancellationToken cancellationToken)
    {
        if (request.QuoteId is null)
        {
            return FundingResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "quoteId is required.",
                ErrorType.Validation), httpContext);
        }

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        var result = await conversions.ExecuteAsync(
            CurrentUserId(httpContext),
            request.QuoteId.Value,
            idempotencyKey,
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToConversionResponse(result.Value), "Conversion completed.", httpContext);
    }

    private static async Task<IResult> ListConversionsAsync(
        HttpContext httpContext,
        FundingConversionService conversions,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var result = await conversions.ListAsync(
            CurrentUserId(httpContext), cursor, limit ?? 50, cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(
                new
                {
                    items = result.Value.Items.Select(ToConversionResponse).ToArray(),
                    nextCursor = result.Value.NextCursor,
                    hasMore = result.Value.HasMore,
                },
                "Conversions loaded.", httpContext);
    }

    private static async Task<IResult> ListBeneficiariesAsync(
        HttpContext httpContext,
        FundingBeneficiaryService beneficiaries,
        CancellationToken cancellationToken)
    {
        var result = await beneficiaries.ListAsync(CurrentUserId(httpContext), cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(
                result.Value.Select(ToBeneficiaryResponse).ToArray(), "Beneficiaries loaded.", httpContext);
    }

    private static async Task<IResult> AddBeneficiaryAsync(
        AddBeneficiaryRequest request,
        HttpContext httpContext,
        FundingBeneficiaryService beneficiaries,
        CancellationToken cancellationToken)
    {
        var sessionId = SessionId(httpContext);
        if (sessionId is null)
        {
            return FundingResults.Error(new Error(
                ErrorCodes.AuthStepUpInvalid,
                "A session is required for this action.",
                ErrorType.Forbidden), httpContext);
        }

        var result = await beneficiaries.AddAsync(
            CurrentUserId(httpContext),
            sessionId.Value,
            request.Currency ?? string.Empty,
            request.Details ?? new Dictionary<string, string>(),
            request.StepUpToken,
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToBeneficiaryResponse(result.Value), "Beneficiary added.", httpContext);
    }

    private static async Task<IResult> RemoveBeneficiaryAsync(
        Guid beneficiaryId,
        HttpContext httpContext,
        FundingBeneficiaryService beneficiaries,
        CancellationToken cancellationToken)
    {
        var result = await beneficiaries.RemoveAsync(
            CurrentUserId(httpContext),
            beneficiaryId,
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToBeneficiaryResponse(result.Value), "Beneficiary removed.", httpContext);
    }

    private static async Task<IResult> CreateWithdrawalPreviewAsync(
        CreateWithdrawalPreviewRequest request,
        HttpContext httpContext,
        FundingWithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        if (request.BeneficiaryId is null)
        {
            return FundingResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "beneficiaryId is required.",
                ErrorType.Validation), httpContext);
        }

        var result = await withdrawals.PreviewAsync(
            CurrentUserId(httpContext),
            request.Currency ?? string.Empty,
            request.ReceiveAmount,
            request.BeneficiaryId.Value,
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToWithdrawalResponse(result.Value), "Preview created.", httpContext);
    }

    private static async Task<IResult> PlaceWithdrawalAsync(
        PlaceWithdrawalRequest request,
        HttpContext httpContext,
        FundingWithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        if (request.PreviewId is null)
        {
            return FundingResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "previewId is required.",
                ErrorType.Validation), httpContext);
        }

        var sessionId = SessionId(httpContext);
        if (sessionId is null)
        {
            return FundingResults.Error(new Error(
                ErrorCodes.AuthStepUpInvalid,
                "A session is required for this action.",
                ErrorType.Forbidden), httpContext);
        }

        var result = await withdrawals.PlaceAsync(
            CurrentUserId(httpContext),
            sessionId.Value,
            request.PreviewId.Value,
            request.StepUpToken,
            httpContext.Request.Headers["Idempotency-Key"].ToString(),
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToWithdrawalResponse(result.Value), "Withdrawal placed.", httpContext);
    }

    private static async Task<IResult> ListWithdrawalsAsync(
        HttpContext httpContext,
        FundingWithdrawalService withdrawals,
        string? state,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        WithdrawalState? filter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            filter = state.Trim().ToLowerInvariant() switch
            {
                "previewed" => WithdrawalState.Previewed,
                "requested" => WithdrawalState.Requested,
                "under_review" => WithdrawalState.UnderReview,
                "submitting" => WithdrawalState.Submitting,
                "submitted" => WithdrawalState.Submitted,
                "completed" => WithdrawalState.Completed,
                "failed" => WithdrawalState.Failed,
                "cancelled" => WithdrawalState.Cancelled,
                "needs_reconciliation" => WithdrawalState.NeedsReconciliation,
                _ => null,
            };
            if (filter is null)
            {
                return FundingResults.Error(new Error(
                    ErrorCodes.ValidationFailed,
                    "The state filter is not recognized.",
                    ErrorType.Validation), httpContext);
            }
        }

        var result = await withdrawals.ListAsync(
            CurrentUserId(httpContext), filter, cursor, limit ?? 50, cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(
                new
                {
                    items = result.Value.Items.Select(ToWithdrawalResponse).ToArray(),
                    nextCursor = result.Value.NextCursor,
                    hasMore = result.Value.HasMore,
                },
                "Withdrawals loaded.", httpContext);
    }

    private static async Task<IResult> GetWithdrawalAsync(
        Guid withdrawalId,
        HttpContext httpContext,
        FundingWithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        var result = await withdrawals.GetAsync(
            CurrentUserId(httpContext), withdrawalId, cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToWithdrawalResponse(result.Value), "Withdrawal loaded.", httpContext);
    }

    private static async Task<IResult> CancelWithdrawalAsync(
        Guid withdrawalId,
        HttpContext httpContext,
        FundingWithdrawalService withdrawals,
        CancellationToken cancellationToken)
    {
        var result = await withdrawals.CancelAsync(
            CurrentUserId(httpContext),
            withdrawalId,
            FundingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, httpContext)
            : FundingResults.Ok(ToWithdrawalResponse(result.Value), "Withdrawal cancelled.", httpContext);
    }

    private static async Task<IResult> ReceiveWebhookAsync(
        string provider,
        HttpRequest request,
        FundingWebhookService webhookService,
        IOptions<FundingOptions> options,
        CancellationToken cancellationToken)
    {
        var maxBodyBytes = options.Value.MaxWebhookBodyBytes;
        if (request.ContentLength > maxBodyBytes)
        {
            return FundingResults.Error(
                new Error(
                    ErrorCodes.RequestTooLarge,
                    "The request body is too large.",
                    ErrorType.Validation),
                request.HttpContext,
                StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyCappedAsync(request, maxBodyBytes, cancellationToken);
        if (body is null)
        {
            return FundingResults.Error(
                new Error(
                    ErrorCodes.RequestTooLarge,
                    "The request body is too large.",
                    ErrorType.Validation),
                request.HttpContext,
                StatusCodes.Status413PayloadTooLarge);
        }

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "X-Webhook-Timestamp", "X-Webhook-Signature" })
        {
            if (request.Headers.TryGetValue(name, out var value))
            {
                headers[name] = value.ToString();
            }
        }

        var result = await webhookService.ProcessAsync(
            provider, body, headers, FundingResults.CorrelationId(request.HttpContext), cancellationToken);
        return result.IsFailure
            ? FundingResults.Error(result.Error!, request.HttpContext)
            : FundingResults.Ok<object?>(null, "Webhook received.", request.HttpContext);
    }

    private static async Task<IResult> ScenarioProbeAsync(
        FundingScenarioRequest request,
        HttpContext httpContext,
        FakeFundingState fundingState,
        FakeFxRateProvider fxProvider,
        CancellationToken cancellationToken)
    {
        var target = request.Target?.Trim().ToLowerInvariant() ?? string.Empty;
        var scenario = request.Scenario?.Trim() ?? string.Empty;
        switch (target)
        {
            case "funding" when FakeFundingState.Scenarios.Contains(scenario, StringComparer.Ordinal):
                fundingState.Scenario = scenario;
                break;
            case "fx" when FakeFxRateProvider.Scenarios.Contains(scenario, StringComparer.Ordinal):
                fxProvider.Scenario = scenario;
                break;
            default:
                return FundingResults.Error(new Error(
                    ErrorCodes.ValidationFailed,
                    "The scenario target or value is not recognized.",
                    ErrorType.Validation), httpContext);
        }

        return FundingResults.Ok(new { applied = true }, "Scenario set.", httpContext);
    }

    private static object ToMethodResponse(Services.FundingMethodView method)
        => new
        {
            currency = method.Currency,
            deposit = new
            {
                minimum = Money(method.DepositMinimum),
                maximum = Money(method.DepositMaximum),
                dailyMaximum = Money(method.DepositDailyMaximum),
            },
            withdrawal = new
            {
                minimum = Money(method.WithdrawalMinimum),
                maximum = Money(method.WithdrawalMaximum),
                daily24Maximum = Money(method.WithdrawalDaily24Maximum),
                daily30Maximum = Money(method.WithdrawalDaily30Maximum),
                flatFee = Money(method.WithdrawalFlatFee),
                feeBps = method.WithdrawalFeeBps,
            },
        };

    private static object ToAccountResponse(FundingAccount account)
        => new
        {
            id = account.Id,
            currency = account.Currency,
            instruction = JsonSerializer.Deserialize<JsonElement>(account.InstructionJson),
            createdAt = account.CreatedAt,
        };

    private static object ToDepositResponse(FundingDeposit deposit)
        => new
        {
            id = deposit.Id,
            currency = deposit.Currency,
            amount = Money(deposit.Amount),
            providerFee = Money(deposit.ProviderFee),
            state = FundingText.ToDb(deposit.State),
            detailCode = deposit.DetailCode,
            shortfall = Money(deposit.Shortfall),
            createdAt = deposit.CreatedAt,
            creditedAt = deposit.CreditedAt,
            reversedAt = deposit.ReversedAt,
        };

    private static object ToConversionResponse(FundingConversion conversion)
        => new
        {
            id = conversion.Id,
            pair = conversion.Pair,
            sourceCurrency = conversion.SourceCurrency,
            targetCurrency = conversion.TargetCurrency,
            sourceAmount = Money(conversion.SourceAmount),
            midRate = Money(conversion.MidRate),
            effectiveRate = Money(conversion.EffectiveRate),
            receiveAmount = Money(conversion.ReceiveAmount),
            feeAmount = Money(conversion.FeeAmount),
            netAmount = Money(conversion.NetAmount),
            termsHash = conversion.TermsHash,
            state = FundingText.ToDb(conversion.State),
            expiresAt = conversion.ExpiresAt,
            createdAt = conversion.CreatedAt,
            completedAt = conversion.CompletedAt,
        };

    private static object ToBeneficiaryResponse(FundingBeneficiary beneficiary)
        => new
        {
            id = beneficiary.Id,
            currency = beneficiary.Currency,
            maskedDisplay = beneficiary.MaskedDisplay,
            nameVerified = beneficiary.NameVerified,
            state = FundingText.ToDb(beneficiary.State),
            activeFrom = beneficiary.ActiveFrom,
            createdAt = beneficiary.CreatedAt,
        };

    private static object ToWithdrawalResponse(FundingWithdrawal withdrawal)
        => new
        {
            id = withdrawal.Id,
            currency = withdrawal.Currency,
            beneficiaryId = withdrawal.BeneficiaryId,
            receiveAmount = Money(withdrawal.ReceiveAmount),
            feeAmount = Money(withdrawal.FeeAmount),
            totalDebited = Money(withdrawal.TotalDebited),
            termsHash = withdrawal.TermsHash,
            state = FundingText.ToDb(withdrawal.State),
            expiresAt = withdrawal.ExpiresAt,
            previewUsed = withdrawal.PreviewUsed,
            detailCode = withdrawal.DetailCode,
            createdAt = withdrawal.CreatedAt,
            requestedAt = withdrawal.RequestedAt,
            submittedAt = withdrawal.SubmittedAt,
            completedAt = withdrawal.CompletedAt,
            failedAt = withdrawal.FailedAt,
            cancelledAt = withdrawal.CancelledAt,
        };

    private static string Money(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string?> ReadBodyCappedAsync(
        HttpRequest request,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > maxBodyBytes)
            {
                return null;
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);

    private static Guid? SessionId(HttpContext httpContext)
        => Guid.TryParse(httpContext.User.FindFirst("sid")?.Value, out var sessionId)
            ? sessionId
            : null;
}
