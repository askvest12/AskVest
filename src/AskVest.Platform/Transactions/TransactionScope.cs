using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AskVest.Platform;

internal sealed class TransactionScope(
    string connectionString,
    ILogger<TransactionScope> logger) : ITransactionScope, IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private DbTransaction? _transaction;
    private bool _completed;

    public NpgsqlConnection Connection => _connection ??= new NpgsqlConnection(connectionString);

    public DbTransaction? Transaction => _transaction;

    public bool IsActive => _transaction is not null;

    public async Task BeginAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
        {
            return;
        }

        var connection = Connection;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        _transaction = await connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var transaction = _transaction
            ?? throw new InvalidOperationException("Cannot commit: no transaction scope is active.");
        await transaction.CommitAsync(cancellationToken);
        _completed = true;
        await EndTransactionAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
        await EndTransactionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && _transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Transaction scope rollback during dispose failed.");
            }
        }

        await EndTransactionAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private async Task EndTransactionAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        // The connection instance is shared with enlisted DbContexts for the life of the scope,
        // so commit and rollback close it for pooling instead of disposing the object.
        if (_connection is { State: ConnectionState.Open })
        {
            await _connection.CloseAsync();
        }
    }
}
