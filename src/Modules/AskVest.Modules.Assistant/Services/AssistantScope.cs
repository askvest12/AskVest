using AskVest.Modules.Assistant.Persistence;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Assistant.Services;

internal static class AssistantScope
{
    /// <summary>
    /// Enlists the context in the ambient transaction so EF writes join the
    /// scope instead of opening a nested transaction on the shared connection.
    /// </summary>
    public static async Task EnlistAsync(
        AssistantDbContext db,
        ITransactionScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }
}
