using System.Data;
using System.Text.Json;
using AskVest.Modules.Assistant.Agent;
using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Assistant.Persistence;
using AskVest.Modules.Assistant.Persistence.Entities;
using AskVest.Modules.Assistant.Policy;
using AskVest.Modules.Assistant.Tools;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Services;

/// <summary>
/// Conversation storage and the send-message pipeline: validation, per-user
/// limits counted from stored messages, the agent run outside any database
/// transaction, then one short transaction persisting the messages and the
/// audit event.
/// </summary>
internal sealed class AssistantChatService(
    AssistantDbContext db,
    AssistantAgent agent,
    ITransactionScope scope,
    IOptions<AssistantOptions> options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AssistantSendResult> SendMessageAsync(
        Guid userId,
        Guid? conversationId,
        string? text,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return AssistantSendResult.Fail(Unavailable());
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > settings.MaxMessageCharacters)
        {
            return AssistantSendResult.Fail(new Error(
                ErrorCodes.ValidationFailed,
                $"The message must be between 1 and {settings.MaxMessageCharacters} characters.",
                ErrorType.Validation));
        }

        var limited = await CheckRateLimitAsync(userId, settings, cancellationToken);
        if (limited is not null)
        {
            await RecordAuditAsync(userId, null, null, AuditEvent.OutcomeRateLimited, settings, cancellationToken);
            return AssistantSendResult.Fail(RateLimited(limited.Value), limited.Value);
        }

        Conversation? conversation = null;
        if (conversationId is { } id)
        {
            conversation = await db.Conversations
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.UserId == userId,
                    cancellationToken);
            if (conversation is null)
            {
                return AssistantSendResult.Fail(new Error(
                    ErrorCodes.ResourceNotFound, "The conversation was not found.", ErrorType.NotFound));
            }
        }

        var history = conversation is null
            ? []
            : await LoadHistoryAsync(conversation.Id, settings.HistoryMessageCount, cancellationToken);

        var turn = await agent.RunAsync(history, text, userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (turn.Text is null)
        {
            await RecordAuditAsync(
                userId, conversation?.Id, null, turn.Outcome, settings, cancellationToken, turn.Audit);
            return AssistantSendResult.Fail(Unavailable());
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        AssistantReply reply;
        try
        {
            await AssistantScope.EnlistAsync(db, scope, cancellationToken);
            var isNew = conversation is null;
            conversation ??= new Conversation
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                Title = TitleFor(text),
                CreatedAt = now,
                UpdatedAt = now,
            };
            if (isNew)
            {
                db.Conversations.Add(conversation);
            }

            var answeredAt = timeProvider.GetUtcNow();
            var userMessage = new Message
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conversation.Id,
                UserId = userId,
                Role = Message.RoleUser,
                Text = text,
                CreatedAt = now,
                IdempotencyKey = idempotencyKey,
            };
            var assistantMessage = new Message
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conversation.Id,
                UserId = userId,
                Role = Message.RoleAssistant,
                Text = turn.Text,
                Citations = JsonSerializer.Serialize(turn.Citations, JsonOptions),
                FactCards = JsonSerializer.Serialize(turn.FactCards, JsonOptions),
                Actions = JsonSerializer.Serialize(turn.Actions, JsonOptions),
                PolicyOutcome = turn.Outcome,
                CreatedAt = answeredAt,
            };
            db.Messages.AddRange(userMessage, assistantMessage);
            conversation.UpdatedAt = answeredAt;
            db.AuditEvents.Add(AuditEntity(
                userId, conversation.Id, assistantMessage.Id, turn.Audit, answeredAt));
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            reply = new AssistantReply(
                conversation.Id,
                assistantMessage.Id,
                turn.Text,
                turn.Citations,
                turn.FactCards,
                turn.Actions,
                options.Value.PolicyVersion,
                turn.PolicyBlocked);
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }

        return AssistantSendResult.Ok(reply);
    }

    public async Task<AssistantConversationPage> ListConversationsAsync(
        Guid userId,
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
            SELECT id, title, created_at, updated_at
            FROM assistant.conversations
            WHERE user_id = @userId
              AND (@hasCursor = FALSE
                   OR updated_at < @cursorAt
                   OR (updated_at = @cursorAt AND id < @cursorId::uuid))
            ORDER BY updated_at DESC, id DESC
            LIMIT @limit
            """;
        AddParameter(command, "userId", userId);
        AddParameter(command, "hasCursor", hasCursor);
        AddParameter(command, "cursorAt", hasCursor ? cursorAt : (object)DBNull.Value);
        AddParameter(command, "cursorId", hasCursor ? cursorId : (object)DBNull.Value);
        AddParameter(command, "limit", limit + 1);

        var rows = new List<ConversationRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ConversationRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken),
                    await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken)));
            }
        }

        var hasMore = rows.Count > limit;
        var items = rows.Take(limit)
            .Select(row => new AssistantConversationItem(row.Id, row.Title, row.CreatedAt, row.UpdatedAt))
            .ToList();
        var nextCursor = hasMore && items.Count > 0
            ? CursorCodec.Encode($"{items[^1].UpdatedAt:O}|{items[^1].Id}")
            : null;
        return new AssistantConversationPage(items, nextCursor, hasMore, limit);
    }

    public async Task<AssistantConversationDetail?> GetConversationAsync(
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(
                candidate => candidate.Id == conversationId && candidate.UserId == userId,
                cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        var messages = await db.Messages
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Take(200)
            .ToListAsync(cancellationToken);

        return new AssistantConversationDetail(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messages.Select(message => new AssistantStoredMessage(
                message.Id,
                message.Role,
                message.Text,
                JsonSerializer.Deserialize<JsonElement>(message.Citations),
                JsonSerializer.Deserialize<JsonElement>(message.FactCards),
                JsonSerializer.Deserialize<JsonElement>(message.Actions),
                message.PolicyOutcome,
                message.CreatedAt)).ToList());
    }

    public async Task<bool> DeleteConversationAsync(
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(
                candidate => candidate.Id == conversationId && candidate.UserId == userId,
                cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await AssistantScope.EnlistAsync(db, scope, cancellationToken);
            var messages = await db.Messages
                .Where(message => message.ConversationId == conversation.Id)
                .ToListAsync(cancellationToken);
            db.Messages.RemoveRange(messages);
            db.Conversations.Remove(conversation);
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }

            return true;
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private async Task<int?> CheckRateLimitAsync(
        Guid userId,
        AssistantOptions settings,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hourStart = now.AddHours(-1);
        var dayStart = now.AddDays(-1);

        var hourCount = await db.Messages.CountAsync(
            message => message.UserId == userId
                && message.Role == Message.RoleUser
                && message.CreatedAt >= hourStart,
            cancellationToken);
        if (hourCount >= settings.MessagesPerHour)
        {
            var oldest = await db.Messages
                .Where(message => message.UserId == userId
                    && message.Role == Message.RoleUser
                    && message.CreatedAt >= hourStart)
                .MinAsync(message => message.CreatedAt, cancellationToken);
            return RetryAfterSeconds(oldest.AddHours(1) - now);
        }

        var dayCount = await db.Messages.CountAsync(
            message => message.UserId == userId
                && message.Role == Message.RoleUser
                && message.CreatedAt >= dayStart,
            cancellationToken);
        if (dayCount >= settings.MessagesPerDay)
        {
            var oldest = await db.Messages
                .Where(message => message.UserId == userId
                    && message.Role == Message.RoleUser
                    && message.CreatedAt >= dayStart)
                .MinAsync(message => message.CreatedAt, cancellationToken);
            return RetryAfterSeconds(oldest.AddDays(1) - now);
        }

        return null;
    }

    private static int RetryAfterSeconds(TimeSpan remaining)
        => Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));

    private async Task<List<LanguageModelMessage>> LoadHistoryAsync(
        Guid conversationId,
        int count,
        CancellationToken cancellationToken)
    {
        var rows = await db.Messages
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(count)
            .Select(message => new { message.Role, message.Text })
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows
            .Select(message => new LanguageModelMessage(message.Role, message.Text))
            .ToList();
    }

    private async Task RecordAuditAsync(
        Guid userId,
        Guid? conversationId,
        Guid? messageId,
        string outcome,
        AssistantOptions settings,
        CancellationToken cancellationToken,
        AssistantAuditData? audit = null)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await AssistantScope.EnlistAsync(db, scope, cancellationToken);
            db.AuditEvents.Add(AuditEntity(
                userId,
                conversationId,
                messageId,
                audit ?? new AssistantAuditData(
                    settings.Model, settings.PolicyVersion, outcome, [], [], [], 0, 0, 0),
                timeProvider.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    private static AuditEvent AuditEntity(
        Guid userId,
        Guid? conversationId,
        Guid? messageId,
        AssistantAuditData audit,
        DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ConversationId = conversationId,
            MessageId = messageId,
            ModelId = audit.ModelId,
            PolicyVersion = audit.PolicyVersion,
            Outcome = audit.Outcome,
            ToolNames = JsonSerializer.Serialize(audit.ToolNames, JsonOptions),
            CitationSourceIds = JsonSerializer.Serialize(audit.CitationSourceIds, JsonOptions),
            ToolResultHashes = JsonSerializer.Serialize(audit.ToolResultHashes, JsonOptions),
            InputTokens = audit.InputTokens,
            OutputTokens = audit.OutputTokens,
            LatencyMilliseconds = audit.LatencyMilliseconds,
            CreatedAt = createdAt,
        };

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset updatedAt, out Guid id)
    {
        updatedAt = default;
        id = Guid.Empty;
        if (!CursorCodec.TryDecode(cursor, out var payload) || payload is null)
        {
            return false;
        }

        var parts = payload.Split('|', 2);
        return parts.Length == 2
            && DateTimeOffset.TryParse(parts[0], out updatedAt)
            && Guid.TryParse(parts[1], out id);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record ConversationRow(
        Guid Id,
        string Title,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private static string TitleFor(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static Error Unavailable()
        => new(ErrorCodes.AssistantUnavailable, "The assistant is unavailable.", ErrorType.Unavailable);

    private static Error RateLimited(int retryAfterSeconds)
        => new(
            ErrorCodes.RateLimitExceeded,
            $"Too many assistant messages. Try again in {retryAfterSeconds} seconds.",
            ErrorType.RateLimited);
}

internal sealed record AssistantSendResult(Result<AssistantReply> Result, int RetryAfterSeconds)
{
    public static AssistantSendResult Ok(AssistantReply reply)
        => new(global::AskVest.SharedKernel.Result.Success(reply), 0);

    public static AssistantSendResult Fail(Error error, int retryAfterSeconds = 0)
        => new(global::AskVest.SharedKernel.Result.Failure<AssistantReply>(error), retryAfterSeconds);
}

internal sealed record AssistantReply(
    Guid ConversationId,
    Guid MessageId,
    string Text,
    IReadOnlyList<AssistantCitation> Citations,
    IReadOnlyList<FactCard> FactCards,
    IReadOnlyList<AssistantAction> Actions,
    string PolicyVersion,
    bool PolicyBlocked);

internal sealed record AssistantConversationPage(
    IReadOnlyList<AssistantConversationItem> Items,
    string? NextCursor,
    bool HasMore,
    int Limit);

internal sealed record AssistantConversationItem(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record AssistantConversationDetail(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AssistantStoredMessage> Messages);

internal sealed record AssistantStoredMessage(
    Guid Id,
    string Role,
    string Text,
    JsonElement Citations,
    JsonElement FactCards,
    JsonElement Actions,
    string? PolicyOutcome,
    DateTimeOffset CreatedAt);
