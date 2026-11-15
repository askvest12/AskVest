using AskVest.Modules.Funding.Configuration;
using AskVest.Modules.Funding.Domain;
using AskVest.Modules.Funding.Persistence;
using AskVest.Modules.Funding.Persistence.Entities;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Identity.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Cash withdrawal beneficiaries. Details pass through to the provider and
/// are never stored, logged or returned; only the provider reference and a
/// masked display persist. New beneficiaries cool off before first use and
/// every addition consumes a step-up grant bound to the currency and the
/// provider beneficiary reference.
/// </summary>
internal sealed class FundingBeneficiaryService(
    FundingDbContext db,
    FundingProviderRegistry providers,
    IStepUpAuthorizer stepUp,
    IOptions<FundingOptions> options,
    TimeProvider timeProvider,
    ITransactionScope scope,
    FundingEventWriter events)
{
    public async Task<Result<IReadOnlyList<FundingBeneficiary>>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rows = await db.Beneficiaries
            .Where(row => row.UserId == userId && row.State != BeneficiaryState.Removed)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var row in rows.Where(row =>
            row.State == BeneficiaryState.CoolingOff
            && row.ActiveFrom is { } activeFrom && activeFrom <= now))
        {
            row.State = BeneficiaryState.Active;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success<IReadOnlyList<FundingBeneficiary>>(rows);
    }

    public async Task<Result<FundingBeneficiary>> AddAsync(
        Guid userId,
        Guid sessionId,
        string currency,
        IReadOnlyDictionary<string, string> details,
        string? stepUpToken,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stepUpToken))
        {
            return Result.Failure<FundingBeneficiary>(new Error(
                ErrorCodes.AuthStepUpInvalid,
                "A step-up grant is required to add a beneficiary.",
                ErrorType.Forbidden));
        }

        var code = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        var option = options.Value.Currencies.FirstOrDefault(
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal));
        if (option is null)
        {
            return Result.Failure<FundingBeneficiary>(FundingResults.NotAvailable("method_unsupported"));
        }

        var clean = details
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.Ordinal);
        if (clean.Count == 0 || clean.Count > 16
            || clean.Values.Any(value => value.Length > 256))
        {
            return Result.Failure<FundingBeneficiary>(FundingResults.BeneficiaryInvalid("bad_details"));
        }

        var ownsScope = !scope.IsActive;
        if (ownsScope)
        {
            await scope.BeginAsync(cancellationToken);
        }

        try
        {
            await EnlistAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);

            var count = await db.Beneficiaries
                .CountAsync(
                    row => row.UserId == userId && row.State != BeneficiaryState.Removed,
                    cancellationToken);
            if (count >= options.Value.BeneficiaryMaximum)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingBeneficiary>(FundingResults.BeneficiaryInvalid("book_full"));
            }

            var provider = providers.Get(option.Provider);
            FundingBeneficiaryResult verified;
            try
            {
                verified = await provider.AddBeneficiaryAsync(
                    userId.ToString("D"), code, clean, cancellationToken);
            }
            catch (FundingProviderException exception)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingBeneficiary>(
                    FundingAccountService.MapProviderFailure(exception));
            }

            if (!verified.NameMatch)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingBeneficiary>(FundingResults.BeneficiaryInvalid("name_mismatch"));
            }

            var duplicate = await db.Beneficiaries.AsNoTracking()
                .AnyAsync(
                    row => row.UserId == userId
                        && row.ProviderKey == provider.Key
                        && row.ProviderReference == verified.ProviderReference
                        && row.State != BeneficiaryState.Removed,
                    cancellationToken);
            if (duplicate)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingBeneficiary>(FundingResults.BeneficiaryInvalid("duplicate"));
            }

            var consumed = await stepUp.ConsumeAsync(
                userId,
                sessionId,
                StepUpPurposes.ChangeWithdrawalAddress,
                $"{code}:{verified.ProviderReference}",
                stepUpToken,
                cancellationToken);
            if (consumed.IsFailure)
            {
                await RollbackAsync(ownsScope, cancellationToken);
                return Result.Failure<FundingBeneficiary>(consumed.Error!);
            }

            var now = timeProvider.GetUtcNow();
            var coolingOff = TimeSpan.FromHours((double)options.Value.BeneficiaryCoolingOffHours);
            var row = new FundingBeneficiary
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                Currency = code,
                ProviderKey = provider.Key,
                ProviderReference = verified.ProviderReference,
                MaskedDisplay = verified.MaskedDisplay,
                NameVerified = verified.NameMatch,
                State = coolingOff == TimeSpan.Zero
                    ? BeneficiaryState.Active
                    : BeneficiaryState.CoolingOff,
                ActiveFrom = coolingOff == TimeSpan.Zero ? now : now + coolingOff,
                CreatedAt = now,
            };
            db.Beneficiaries.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            events.Write(
                userId, FundingEventType.BeneficiaryStateChanged, "beneficiary", row.Id,
                null, FundingText.ToDb(row.State), correlationId);
            await CommitIfOwnerAsync(ownsScope, cancellationToken);
            return Result.Success(row);
        }
        catch (DbUpdateException exception) when (ownsScope
            && exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            await scope.RollbackAsync(cancellationToken);
            return Result.Failure<FundingBeneficiary>(FundingResults.BeneficiaryInvalid("duplicate"));
        }
        catch
        {
            await RollbackAsync(ownsScope, cancellationToken);
            throw;
        }
    }

    public async Task<Result<FundingBeneficiary>> RemoveAsync(
        Guid userId,
        Guid beneficiaryId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var row = await db.Beneficiaries
            .FirstOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.Id == beneficiaryId,
                cancellationToken);
        if (row is null)
        {
            return Result.Failure<FundingBeneficiary>(FundingResults.NotFound("beneficiary"));
        }

        if (row.State == BeneficiaryState.Removed)
        {
            return Result.Success(row);
        }

        var from = FundingText.ToDb(row.State);
        row.State = BeneficiaryState.Removed;
        row.RemovedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        events.Write(
            userId, FundingEventType.BeneficiaryStateChanged, "beneficiary", row.Id,
            from, FundingText.ToDb(BeneficiaryState.Removed), correlationId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(row);
    }

    /// <summary>
    /// The beneficiary is usable when active, or still cooling off but past
    /// its active-from time. Returns the active-from for cooling-off errors.
    /// </summary>
    internal static bool IsUsable(FundingBeneficiary row, DateTimeOffset now)
        => row.State == BeneficiaryState.Active
            || (row.State == BeneficiaryState.CoolingOff
                && row.ActiveFrom is { } activeFrom && activeFrom <= now);

    private async Task EnlistAsync(CancellationToken cancellationToken)
    {
        if (scope.Transaction is not null)
        {
            await db.Database.UseTransactionAsync(scope.Transaction, cancellationToken);
        }
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
        => await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('funding'), hashtext({userId.ToString("D")}))",
            cancellationToken);

    private async Task CommitIfOwnerAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope)
        {
            await scope.CommitAsync(cancellationToken);
        }
    }

    private async Task RollbackAsync(bool ownsScope, CancellationToken cancellationToken)
    {
        if (ownsScope && scope.IsActive)
        {
            await scope.RollbackAsync(cancellationToken);
        }
    }
}
