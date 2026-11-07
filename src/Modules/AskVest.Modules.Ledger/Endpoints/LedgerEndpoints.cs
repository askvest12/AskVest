using System.Globalization;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Ledger.Endpoints;

public static class LedgerEndpoints
{
    public static void Map(RouteGroupBuilder api, bool probesEnabled)
    {
        MapBookEndpoints(api.MapGroup("/practice").RequireAuthorization(CompliancePolicyNames.PracticeEligible), Book.Practice, probesEnabled);
        MapBookEndpoints(api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible), Book.Real, probesEnabled);
    }

    private static void MapBookEndpoints(RouteGroupBuilder group, Book book, bool probesEnabled)
    {
        group.MapGet("/balances", (HttpContext httpContext, ILedger ledger, CancellationToken cancellationToken) =>
                GetBalancesAsync(httpContext, ledger, book, cancellationToken))
            .WithName($"Get{book}Balances")
            .WithSummary("Per-asset available and held balances for the caller.")
            .WithTags("Ledger");

        group.MapGet("/ledger/entries", (HttpContext httpContext, LedgerQueryService queries, int? limit, string? cursor, CancellationToken cancellationToken) =>
                GetEntriesAsync(httpContext, queries, book, limit, cursor, cancellationToken))
            .WithName($"Get{book}LedgerEntries")
            .WithSummary("The caller's own ledger entries, newest first.")
            .WithTags("Ledger");

        if (probesEnabled)
        {
            group.MapPost("/diagnostics/ledger-post", (HttpContext httpContext, ILedger ledger, CancellationToken cancellationToken) =>
                    PostProbeAsync(httpContext, ledger, book, cancellationToken))
                .WithName($"Post{book}LedgerProbe")
                .WithSummary("Diagnostic credit or debit posting behind the eligibility gate.")
                .WithTags("Diagnostics")
                .WithMetadata(new RequiresIdempotencyAttribute())
                .ExcludeFromDescription();
        }
    }

    private static async Task<IResult> GetBalancesAsync(
        HttpContext httpContext,
        ILedger ledger,
        Book book,
        CancellationToken cancellationToken)
    {
        var result = await ledger.GetBalancesAsync(CurrentUserId(httpContext), book, cancellationToken);
        if (result.IsFailure)
        {
            return LedgerResults.Error(result.Error!, httpContext);
        }

        var data = result.Value.Select(balance => new
        {
            asset = balance.AssetCode,
            available = balance.Available.ToString("0.############################", CultureInfo.InvariantCulture),
            held = balance.Held.ToString("0.############################", CultureInfo.InvariantCulture),
        });
        return LedgerResults.Ok(data, "Balances loaded.", httpContext);
    }

    private static async Task<IResult> GetEntriesAsync(
        HttpContext httpContext,
        LedgerQueryService queries,
        Book book,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await queries.GetEntriesAsync(
            CurrentUserId(httpContext),
            book,
            Math.Clamp(limit ?? PageRequest.DefaultLimit, 1, PageRequest.MaxLimit),
            cursor,
            cancellationToken);
        return LedgerResults.Ok(
            new
            {
                items = page.Items.Select(item => new
                {
                    entryId = item.EntryId,
                    journalId = item.JournalId,
                    postedAt = item.PostedAt,
                    reasonCode = item.ReasonCode,
                    referenceType = item.ReferenceType,
                    referenceId = item.ReferenceId,
                    asset = item.AssetCode,
                    amount = item.Amount.ToString("0.############################", CultureInfo.InvariantCulture),
                }),
                nextCursor = page.NextCursor,
                hasMore = page.HasMore,
                limit = page.Limit,
            },
            "Ledger history loaded.",
            httpContext);
    }

    private static async Task<IResult> PostProbeAsync(
        HttpContext httpContext,
        ILedger ledger,
        Book book,
        CancellationToken cancellationToken)
    {
        LedgerProbeBody? body;
        try
        {
            body = await httpContext.Request.ReadFromJsonAsync<LedgerProbeBody>(cancellationToken);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            body = null;
        }

        var kind = body?.Kind?.Trim().ToLowerInvariant();
        var asset = body?.Asset?.Trim().ToUpperInvariant();
        var amountValid = decimal.TryParse(
            body?.Amount,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var amount);
        if (kind is not ("credit" or "debit") || string.IsNullOrEmpty(asset) || !amountValid || amount <= 0m)
        {
            return LedgerResults.Error(
                new Error(ErrorCodes.ValidationFailed, "The probe body requires kind, asset and amount.", ErrorType.Validation),
                httpContext);
        }

        var userId = CurrentUserId(httpContext);
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty;
        var source = book == Book.Practice
            ? LedgerAccounts.PracticeIssuance(book)
            : LedgerAccounts.SystemClearing(book);
        var crediting = kind == "credit";
        var request = new PostJournalRequest(
            $"ledger:probe:{userId:N}:{idempotencyKey}",
            book,
            crediting
                ? (book == Book.Practice ? LedgerReasonCodes.PracticeCreditGrant : LedgerReasonCodes.Deposit)
                : LedgerReasonCodes.Withdrawal,
            "diagnostics",
            null,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(book, userId), asset!, crediting ? amount : -amount),
                new LedgerLine(source, asset!, crediting ? -amount : amount),
            ]);
        var result = await ledger.PostJournalAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return LedgerResults.Error(result.Error!, httpContext);
        }

        return LedgerResults.Ok(
            new
            {
                journalId = result.Value.Id,
                book = LedgerText.ToDb(book),
                reasonCode = result.Value.ReasonCode,
                postedAt = result.Value.PostedAt,
            },
            "Probe journal posted.",
            httpContext);
    }


    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);

    private sealed record LedgerProbeBody(string? Kind, string? Asset, string? Amount);
}
