using System.Data.Common;
using Npgsql;

namespace AskVest.Platform;

public interface ITransactionScope
{
    NpgsqlConnection Connection { get; }

    DbTransaction? Transaction { get; }

    bool IsActive { get; }

    Task BeginAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
