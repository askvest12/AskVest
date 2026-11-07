using System.Data;
using System.Globalization;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Persistence;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Ledger.Services;

internal sealed class LedgerQueryService(LedgerDbContext db)
{
    public async Task<PageResult<LedgerEntryItem>> GetEntriesAsync(
        Guid userId,
        Book book,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.journal_id, j.posted_at, j.reason_code, j.reference_type, j.reference_id,
                   e.asset_code, e.amount
            FROM ledger.journal_entries e
            JOIN ledger.journals j ON j.id = e.journal_id AND j.book = e.book
            JOIN ledger.accounts a ON a.id = e.account_id AND a.book = e.book
            WHERE a.owner_type = 'user' AND a.owner_id = @userId AND e.book = @book
              AND (@hasCursor = FALSE
                   OR j.posted_at < @cursorAt
                   OR (j.posted_at = @cursorAt AND e.id < @cursorId::uuid))
            ORDER BY j.posted_at DESC, e.id DESC
            LIMIT @limit
            """;
        AddParameter(command, "userId", userId);
        AddParameter(command, "book", LedgerText.ToDb(book));
        AddParameter(command, "hasCursor", hasCursor);
        AddParameter(command, "cursorAt", hasCursor ? cursorAt : (object)DBNull.Value);
        AddParameter(command, "cursorId", hasCursor ? cursorId : (object)DBNull.Value);
        AddParameter(command, "limit", limit + 1);

        var rows = new List<LedgerEntryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var postedValue = await reader.GetFieldValueAsync<object>(2, cancellationToken);
            rows.Add(new LedgerEntryItem(
                await reader.GetFieldValueAsync<Guid>(0, cancellationToken),
                await reader.GetFieldValueAsync<Guid>(1, cancellationToken),
                postedValue is DateTime posted
                    ? new DateTimeOffset(DateTime.SpecifyKind(posted, DateTimeKind.Utc))
                    : (DateTimeOffset)postedValue,
                await reader.GetFieldValueAsync<string>(3, cancellationToken),
                await reader.GetFieldValueAsync<string>(4, cancellationToken),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : await reader.GetFieldValueAsync<Guid>(5, cancellationToken),
                await reader.GetFieldValueAsync<string>(6, cancellationToken),
                await reader.GetFieldValueAsync<decimal>(7, cancellationToken)));
        }

        await reader.DisposeAsync();
        if (closeConnection)
        {
            await connection.CloseAsync();
        }

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var nextCursor = hasMore && page.Count > 0
            ? CursorCodec.Encode($"{page[^1].PostedAt:O}|{page[^1].EntryId}")
            : null;
        return new PageResult<LedgerEntryItem>(page, nextCursor, hasMore, limit);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset postedAt, out Guid entryId)
    {
        postedAt = default;
        entryId = Guid.Empty;
        if (cursor is null || !CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var separator = payload.IndexOf('|', StringComparison.Ordinal);
        return separator > 0
            && DateTimeOffset.TryParse(payload[..separator], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out postedAt)
            && Guid.TryParse(payload[(separator + 1)..], out entryId);
    }
}
