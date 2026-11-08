using System.Data;
using System.Globalization;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Persistence;
using AskVest.Modules.Ledger.Persistence.Entities;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AskVest.Modules.Ledger.Services;

internal sealed class LedgerService(
    LedgerDbContext db,
    ITransactionScope scope,
    IOutbox outbox,
    TimeProvider timeProvider) : ILedger
{
    public const string JournalPostedMessageType = "LedgerJournalPosted";

    public Task<Result<LedgerJournal>> PostJournalAsync(PostJournalRequest request, CancellationToken cancellationToken = default)
        => PostCoreAsync(request, reversesJournalId: null, cancellationToken);

    public async Task<Result<LedgerJournal>> ReverseJournalAsync(
        string idempotencyKey,
        Guid journalId,
        CancellationToken cancellationToken = default)
    {
        var journal = await db.Journals.FirstOrDefaultAsync(j => j.Id == journalId, cancellationToken);
        if (journal is null)
        {
            return Result.Failure<LedgerJournal>(new Error(
                ErrorCodes.ResourceNotFound,
                "The journal was not found.",
                ErrorType.NotFound));
        }

        var alreadyReversed = await db.Journals.AnyAsync(j => j.ReversesJournalId == journalId, cancellationToken);
        if (alreadyReversed)
        {
            return AlreadyReversed();
        }

        var entries = await db.JournalEntries
            .Where(entry => entry.JournalId == journalId)
            .OrderBy(entry => entry.LineNumber)
            .ToListAsync(cancellationToken);
        var accountIds = entries.Select(entry => entry.AccountId).Distinct().ToList();
        var accounts = await db.Accounts
            .Where(account => accountIds.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, cancellationToken);

        var lines = entries.Select(entry =>
        {
            var account = accounts[entry.AccountId];
            return new LedgerLine(
                new LedgerAccountRef(account.Book, account.OwnerType, account.OwnerId, account.Kind),
                entry.AssetCode,
                -entry.Amount);
        }).ToList();

        var request = new PostJournalRequest(
            idempotencyKey,
            journal.Book,
            LedgerReasonCodes.Reversal,
            "journal",
            journal.Id,
            lines);
        return await PostCoreAsync(request, journalId, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<LedgerBalance>>> GetBalancesAsync(
        Guid userId,
        Book book,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.Accounts
            .Where(account => account.OwnerType == AccountOwnerType.User
                && account.OwnerId == userId
                && account.Book == book
                && (account.Kind == AccountKind.UserAvailable || account.Kind == AccountKind.UserHeld))
            .Join(
                db.AccountBalances,
                account => account.Id,
                balance => balance.AccountId,
                (account, balance) => new { account.AssetCode, account.Kind, balance.Balance })
            .ToListAsync(cancellationToken);

        var balances = rows
            .GroupBy(row => row.AssetCode, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LedgerBalance(
                group.Key,
                group.Where(row => row.Kind == AccountKind.UserAvailable).Sum(row => row.Balance),
                group.Where(row => row.Kind == AccountKind.UserHeld).Sum(row => row.Balance)))
            .ToList();
        return Result.Success<IReadOnlyList<LedgerBalance>>(balances);
    }

    public async Task<PageResult<LedgerMovement>> GetMovementsAsync(
        Guid userId,
        Book book,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var hasCursor = TryDecodeMovementCursor(cursor, out var cursorAt, out var cursorJournalId);
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.id, j.posted_at, j.reason_code, j.reference_type, j.reference_id,
                   e.asset_code, SUM(e.amount) AS net
            FROM ledger.journal_entries e
            JOIN ledger.journals j ON j.id = e.journal_id AND j.book = e.book
            JOIN ledger.accounts a ON a.id = e.account_id AND a.book = e.book
            WHERE a.owner_type = 'user' AND a.owner_id = @userId AND e.book = @book
              AND (@hasCursor = FALSE
                   OR j.posted_at > @cursorAt
                   OR (j.posted_at = @cursorAt AND j.id > @cursorJournalId::uuid))
            GROUP BY j.id, j.posted_at, j.reason_code, j.reference_type, j.reference_id, e.asset_code
            ORDER BY j.posted_at ASC, j.id ASC, e.asset_code ASC
            LIMIT @limit
            """;
        AddParameter(command, "userId", userId);
        AddParameter(command, "book", LedgerText.ToDb(book));
        AddParameter(command, "hasCursor", hasCursor);
        AddParameter(command, "cursorAt", hasCursor ? cursorAt : (object)DBNull.Value);
        AddParameter(command, "cursorJournalId", hasCursor ? cursorJournalId : (object)DBNull.Value);
        AddParameter(command, "limit", limit + 1);

        var rows = new List<LedgerMovement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var postedValue = await reader.GetFieldValueAsync<object>(1, cancellationToken);
            rows.Add(new LedgerMovement(
                await reader.GetFieldValueAsync<Guid>(0, cancellationToken),
                postedValue is DateTime posted
                    ? new DateTimeOffset(DateTime.SpecifyKind(posted, DateTimeKind.Utc))
                    : (DateTimeOffset)postedValue,
                await reader.GetFieldValueAsync<string>(2, cancellationToken),
                await reader.GetFieldValueAsync<string>(3, cancellationToken),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : await reader.GetFieldValueAsync<Guid>(4, cancellationToken),
                await reader.GetFieldValueAsync<string>(5, cancellationToken),
                await reader.GetFieldValueAsync<decimal>(6, cancellationToken)));
        }

        await reader.DisposeAsync();
        if (closeConnection)
        {
            await connection.CloseAsync();
        }

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var last = page.Count > 0 ? page[^1] : null;
        var nextCursor = hasMore && last is not null
            ? CursorCodec.Encode($"{last.PostedAt:O}|{last.JournalId}")
            : null;
        return new PageResult<LedgerMovement>(page, nextCursor, hasMore, limit);
    }

    public async Task<AssetKind?> GetAssetKindAsync(
        string assetCode,
        CancellationToken cancellationToken = default)
    {
        var normalized = assetCode.Trim().ToUpperInvariant();
        var kind = await db.Assets.AsNoTracking()
            .Where(asset => asset.Code == normalized)
            .Select(asset => (AssetKind?)asset.Kind)
            .FirstOrDefaultAsync(cancellationToken);
        return kind;
    }

    public async Task<Result> RegisterAssetAsync(
        string code,
        AssetKind kind,
        int scale,
        CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length == 0 || normalized.Length > 16 || scale < 0 || scale > 18)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The asset code and scale are not valid.",
                ErrorType.Validation));
        }

        var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ledger.assets (code, kind, scale, created_at)
            VALUES ({normalized}, {LedgerText.ToDb(kind)}, {scale}, {now})
            ON CONFLICT (code) DO NOTHING
            """,
            cancellationToken);

        var existing = await db.Assets.AsNoTracking()
            .Where(asset => asset.Code == normalized)
            .Select(asset => new { asset.Kind, asset.Scale })
            .FirstAsync(cancellationToken);
        if (existing.Kind != kind || existing.Scale != scale)
        {
            return Result.Failure(new Error(
                ErrorCodes.ResourceConflict,
                "The asset is already registered with different properties.",
                ErrorType.Conflict));
        }

        return Result.Success();
    }

    private async Task<Result<LedgerJournal>> PostCoreAsync(
        PostJournalRequest request,
        Guid? reversesJournalId,
        CancellationToken cancellationToken)
    {
        if (JournalValidation.ValidateShape(request) is { } shapeError)
        {
            return Result.Failure<LedgerJournal>(shapeError);
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            var result = await PostInsideScopeAsync(request, reversesJournalId, cancellationToken);
            if (result.IsFailure)
            {
                if (ownsScope)
                {
                    await scope.RollbackAsync(cancellationToken);
                }

                return result;
            }

            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (PostgresException exception) when (IsDeadlockOrLockTimeout(exception))
        {
            if (ownsScope)
            {
                await scope.RollbackAsync(cancellationToken);
            }

            return Result.Failure<LedgerJournal>(new Error(
                ErrorCodes.ServiceUnavailable,
                "The ledger is temporarily unavailable. Retry the request.",
                ErrorType.Unavailable));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (ownsScope)
            {
                await scope.RollbackAsync(cancellationToken);
            }

            return Result.Failure<LedgerJournal>(MapUniqueViolation(exception));
        }
        catch
        {
            if (ownsScope)
            {
                await scope.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private async Task<Result<LedgerJournal>> PostInsideScopeAsync(
        PostJournalRequest request,
        Guid? reversesJournalId,
        CancellationToken cancellationToken)
    {
        var assetCodes = request.Lines.Select(line => line.AssetCode).Distinct(StringComparer.Ordinal).ToList();
        var assets = await db.Assets.AsNoTracking()
            .Where(asset => assetCodes.Contains(asset.Code))
            .ToDictionaryAsync(asset => asset.Code, asset => asset.Scale, StringComparer.Ordinal, cancellationToken);

        foreach (var line in request.Lines)
        {
            if (JournalValidation.ValidateLine(line, request.Book, assets) is { } lineError)
            {
                return Result.Failure<LedgerJournal>(lineError);
            }
        }

        if (JournalValidation.ValidateZeroSum(request.Lines) is { } balanceError)
        {
            return Result.Failure<LedgerJournal>(balanceError);
        }

        var contentHash = JournalValidation.ContentHash(
            request.Book,
            request.ReasonCode,
            request.ReferenceType,
            request.ReferenceId,
            reversesJournalId,
            request.Lines);

        var existing = await db.Journals.AsNoTracking()
            .FirstOrDefaultAsync(journal => journal.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return ReplayOrConflict(existing, contentHash);
        }

        var journalId = Guid.CreateVersion7();
        var postedAt = timeProvider.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ledger.journals
                (id, book, reason_code, reference_type, reference_id, idempotency_key, content_hash,
                 reverses_journal_id, correlation_id, posted_at)
            VALUES
                ({journalId}, {LedgerText.ToDb(request.Book)}, {request.ReasonCode}, {request.ReferenceType},
                 {request.ReferenceId}, {request.IdempotencyKey}, {contentHash}, {reversesJournalId},
                 {Guid.Empty}, {postedAt})
            ON CONFLICT (idempotency_key) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            var raced = await db.Journals.AsNoTracking()
                .FirstAsync(journal => journal.IdempotencyKey == request.IdempotencyKey, cancellationToken);
            return ReplayOrConflict(raced, contentHash);
        }

        var accountIds = await EnsureAccountsAsync(request, cancellationToken);
        var deltas = await CheckBalancesAsync(accountIds, request.Lines, cancellationToken);
        if (deltas.IsFailure)
        {
            return Result.Failure<LedgerJournal>(deltas.Error!);
        }

        await InsertEntriesAsync(journalId, request, accountIds, cancellationToken);
        await ApplyBalanceDeltasAsync(deltas.Value!, cancellationToken);
        await EnqueuePostedMessageAsync(journalId, request, reversesJournalId, cancellationToken);

        return Result.Success(new LedgerJournal(
            journalId,
            request.Book,
            request.ReasonCode,
            request.ReferenceType,
            request.ReferenceId,
            postedAt));
    }

    private static Result<LedgerJournal> ReplayOrConflict(Journal existing, string contentHash)
    {
        if (!string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return Result.Failure<LedgerJournal>(new Error(
                ErrorCodes.LedgerIdempotencyConflict,
                "The idempotency key was already used with different journal content.",
                ErrorType.Conflict));
        }

        return Result.Success(new LedgerJournal(
            existing.Id,
            existing.Book,
            existing.ReasonCode,
            existing.ReferenceType,
            existing.ReferenceId,
            existing.PostedAt));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool TryDecodeMovementCursor(string? cursor, out DateTimeOffset postedAt, out Guid journalId)
    {
        postedAt = default;
        journalId = Guid.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        return separator > 0
            && DateTimeOffset.TryParse(payload[..separator], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out postedAt)
            && Guid.TryParse(payload[(separator + 1)..], out journalId);
    }

    private async Task<Dictionary<AccountKey, Guid>> EnsureAccountsAsync(
        PostJournalRequest request,
        CancellationToken cancellationToken)
    {
        var keys = request.Lines
            .Select(line => new AccountKey(
                request.Book,
                line.Account.OwnerType,
                line.Account.OwnerId,
                line.Account.Kind,
                line.AssetCode))
            .Distinct()
            .OrderBy(key => key.BookText, StringComparer.Ordinal)
            .ThenBy(key => key.OwnerTypeText, StringComparer.Ordinal)
            .ThenBy(key => key.OwnerId)
            .ThenBy(key => key.KindText, StringComparer.Ordinal)
            .ThenBy(key => key.AssetCode, StringComparer.Ordinal)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var resolved = new Dictionary<AccountKey, Guid>();
        foreach (var key in keys)
        {
            var allowNegative = key.OwnerType == AccountOwnerType.System;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ledger.accounts
                    (id, book, owner_type, owner_id, kind, asset_code, allow_negative, created_at)
                VALUES
                    ({Guid.CreateVersion7()}, {key.BookText}, {key.OwnerTypeText}, {key.OwnerId},
                     {key.KindText}, {key.AssetCode}, {allowNegative}, {now})
                ON CONFLICT (book, owner_type, owner_id, kind, asset_code) DO NOTHING
                """,
                cancellationToken);
            var id = await db.Accounts.AsNoTracking()
                .Where(account => account.Book == key.Book
                    && account.OwnerType == key.OwnerType
                    && account.OwnerId == key.OwnerId
                    && account.Kind == key.Kind
                    && account.AssetCode == key.AssetCode)
                .Select(account => account.Id)
                .FirstAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ledger.account_balances (account_id, balance, allow_negative, version, updated_at)
                VALUES ({id}, 0, {allowNegative}, 1, {now})
                ON CONFLICT (account_id) DO NOTHING
                """,
                cancellationToken);
            resolved[key] = id;
        }

        return resolved;
    }

    private async Task<Result<Dictionary<Guid, decimal>>> CheckBalancesAsync(
        IReadOnlyDictionary<AccountKey, Guid> accountIds,
        IReadOnlyList<LedgerLine> lines,
        CancellationToken cancellationToken)
    {
        var accountByLine = lines.Select(line => new AccountKey(
            line.Account.Book,
            line.Account.OwnerType,
            line.Account.OwnerId,
            line.Account.Kind,
            line.AssetCode)).ToList();
        var deltas = new Dictionary<Guid, decimal>();
        foreach (var (line, key) in lines.Zip(accountByLine))
        {
            var id = accountIds[key];
            deltas[id] = deltas.GetValueOrDefault(id) + line.Amount;
        }

        var locked = new Dictionary<Guid, (decimal Balance, bool AllowNegative)>();
        foreach (var accountId in deltas.Keys.OrderBy(id => id))
        {
            var row = await db.AccountBalances
                .FromSql($"""
                    SELECT account_id, balance, allow_negative, version, updated_at
                    FROM ledger.account_balances
                    WHERE account_id = {accountId}
                    FOR UPDATE
                    """)
                .AsNoTracking()
                .FirstAsync(cancellationToken);
            locked[accountId] = (row.Balance, row.AllowNegative);
        }

        foreach (var (accountId, delta) in deltas)
        {
            var (balance, allowNegative) = locked[accountId];
            if (!allowNegative && balance + delta < 0m)
            {
                return Result.Failure<Dictionary<Guid, decimal>>(new Error(
                    ErrorCodes.LedgerInsufficientFunds,
                    "The account does not hold enough funds for this journal.",
                    ErrorType.BusinessRule));
            }
        }

        return Result.Success(deltas);
    }

    private async Task InsertEntriesAsync(
        Guid journalId,
        PostJournalRequest request,
        IReadOnlyDictionary<AccountKey, Guid> accountIds,
        CancellationToken cancellationToken)
    {
        var bookText = LedgerText.ToDb(request.Book);
        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ledger.journal_entries
                    (id, journal_id, book, account_id, asset_code, amount, line_number)
                VALUES
                    ({Guid.CreateVersion7()}, {journalId}, {bookText},
                     {accountIds[new AccountKey(line.Account.Book, line.Account.OwnerType, line.Account.OwnerId, line.Account.Kind, line.AssetCode)]},
                     {line.AssetCode}, {line.Amount}, {index + 1})
                """,
                cancellationToken);
        }
    }

    private async Task ApplyBalanceDeltasAsync(
        IReadOnlyDictionary<Guid, decimal> deltas,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (accountId, delta) in deltas)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE ledger.account_balances
                SET balance = balance + {delta}, version = version + 1, updated_at = {now}
                WHERE account_id = {accountId}
                """,
                cancellationToken);
        }
    }

    private async Task EnqueuePostedMessageAsync(
        Guid journalId,
        PostJournalRequest request,
        Guid? reversesJournalId,
        CancellationToken cancellationToken)
    {
        var userIds = request.Lines
            .Where(line => line.Account.OwnerType == AccountOwnerType.User)
            .Select(line => line.Account.OwnerId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        await outbox.EnqueueAsync(
            JournalPostedMessageType,
            new LedgerJournalPostedMessage(
                journalId,
                request.Book,
                request.ReasonCode,
                request.ReferenceType,
                request.ReferenceId ?? reversesJournalId,
                userIds),
            $"ledger:journal-posted:{journalId}",
            cancellationToken);
    }

    private static bool IsDeadlockOrLockTimeout(PostgresException exception)
        => exception.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable;

    private static Error MapUniqueViolation(PostgresException exception)
        => string.Equals(exception.ConstraintName, "ix_journals_reverses_journal_id", StringComparison.Ordinal)
            ? AlreadyReversed().Error!
            : new Error(
                ErrorCodes.ResourceConflict,
                "The journal conflicts with an existing record.",
                ErrorType.Conflict);

    private static Result<LedgerJournal> AlreadyReversed()
        => Result.Failure<LedgerJournal>(new Error(
            ErrorCodes.LedgerAlreadyReversed,
            "The journal was already reversed.",
            ErrorType.Conflict));

    private sealed record AccountKey(
        Book Book,
        AccountOwnerType OwnerType,
        Guid OwnerId,
        AccountKind Kind,
        string AssetCode)
    {
        public string BookText => LedgerText.ToDb(Book);

        public string OwnerTypeText => LedgerText.ToDb(OwnerType);

        public string KindText => LedgerText.ToDb(Kind);
    }
}
