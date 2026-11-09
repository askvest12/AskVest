using AskVest.Modules.Assistant.Configuration;
using AskVest.Modules.Assistant.Persistence;
using AskVest.Modules.Identity.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Services;

/// <summary>
/// Deletes conversations past the retention window and every conversation
/// owned by an anonymized account. Runs in batches of 500 so a large backlog
/// never holds one long transaction.
/// </summary>
internal sealed class AssistantRetention(
    AssistantDbContext db,
    IUserDirectory directory,
    ITransactionScope scope,
    IOptions<AssistantOptions> options,
    TimeProvider timeProvider,
    ILogger<AssistantRetention> logger)
{
    private const int BatchSize = 500;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var cutoff = timeProvider.GetUtcNow().AddDays(-settings.ConversationRetentionDays);
        var removed = 0;

        while (true)
        {
            var ids = await db.Conversations
                .Where(conversation => conversation.UpdatedAt < cutoff)
                .OrderBy(conversation => conversation.UpdatedAt)
                .Select(conversation => conversation.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            await DeleteConversationsAsync(ids, cancellationToken);
            removed += ids.Count;
            if (ids.Count < BatchSize)
            {
                break;
            }
        }

        var userIds = await db.Conversations
            .Select(conversation => conversation.UserId)
            .Distinct()
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var userId in userIds)
        {
            var snapshot = await directory.FindAsync(userId, cancellationToken);
            if (snapshot?.IsAnonymized != true)
            {
                continue;
            }

            var ids = await db.Conversations
                .Where(conversation => conversation.UserId == userId)
                .Select(conversation => conversation.Id)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                continue;
            }

            await DeleteConversationsAsync(ids, cancellationToken);
            removed += ids.Count;
        }

        if (removed > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Assistant retention removed {Count} conversations.", removed);
        }
    }

    private async Task DeleteConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await AssistantScope.EnlistAsync(db, scope, cancellationToken);
            var messages = await db.Messages
                .Where(message => conversationIds.Contains(message.ConversationId))
                .ToListAsync(cancellationToken);
            db.Messages.RemoveRange(messages);
            var conversations = await db.Conversations
                .Where(conversation => conversationIds.Contains(conversation.Id))
                .ToListAsync(cancellationToken);
            db.Conversations.RemoveRange(conversations);
            await db.SaveChangesAsync(cancellationToken);
            if (ownsScope)
            {
                await scope.CommitAsync(cancellationToken);
            }
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
}
