using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

/// <summary>
/// Purges expired identity rows in bounded batches: one-time codes, step-up
/// grants, throttle buckets and sessions past their absolute expiry (their
/// refresh tokens, rotated or not, are removed by cascade once the session is
/// gone because reuse detection no longer needs them).
/// </summary>
public sealed class MaintenanceService(
    IdentityDbContext db,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider,
    ILogger<MaintenanceService> logger)
{
    private const int BatchSize = 500;

    public async Task<MaintenanceResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var throttleCutoff = now - options.Value.LoginThrottleWindow;

        var codes = await PurgeBatchesAsync(cancellationToken => db.Database.ExecuteSqlAsync(
            $"DELETE FROM identity.one_time_codes WHERE id IN (SELECT id FROM identity.one_time_codes WHERE expires_at <= {now} LIMIT {BatchSize})",
            cancellationToken), cancellationToken);
        var grants = await PurgeBatchesAsync(cancellationToken => db.Database.ExecuteSqlAsync(
            $"DELETE FROM identity.step_up_grants WHERE id IN (SELECT id FROM identity.step_up_grants WHERE expires_at <= {now} LIMIT {BatchSize})",
            cancellationToken), cancellationToken);
        var buckets = await PurgeBatchesAsync(cancellationToken => db.Database.ExecuteSqlAsync(
            $"DELETE FROM identity.throttle_buckets WHERE id IN (SELECT id FROM identity.throttle_buckets WHERE window_start <= {throttleCutoff} AND (locked_until IS NULL OR locked_until <= {now}) LIMIT {BatchSize})",
            cancellationToken), cancellationToken);
        var sessions = await PurgeBatchesAsync(cancellationToken => db.Database.ExecuteSqlAsync(
            $"DELETE FROM identity.sessions WHERE id IN (SELECT id FROM identity.sessions WHERE absolute_expires_at <= {now} LIMIT {BatchSize})",
            cancellationToken), cancellationToken);

        var result = new MaintenanceResult(codes, grants, buckets, sessions);
        if (result.Total > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Identity maintenance removed {Codes} codes, {Grants} step-up grants, {Buckets} throttle buckets and {Sessions} sessions.",
                codes, grants, buckets, sessions);
        }

        return result;
    }

    private static async Task<int> PurgeBatchesAsync(
        Func<CancellationToken, Task<int>> delete, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await delete(cancellationToken);
            total += deleted;
        }
        while (deleted >= BatchSize);

        return total;
    }
}

public sealed record MaintenanceResult(int Codes, int StepUpGrants, int ThrottleBuckets, int Sessions)
{
    public int Total => Codes + StepUpGrants + ThrottleBuckets + Sessions;
}
