using AskVest.Modules.Learning.Endpoints;
using AskVest.Modules.Learning.Persistence;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Learning.Services;

internal static class LearningScope
{
    /// <summary>
    /// Enlists the context in the ambient transaction so EF writes join the
    /// scope instead of opening a nested transaction on the shared connection.
    /// </summary>
    public static async Task EnlistAsync(
        LearningDbContext db,
        ITransactionScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }
}
