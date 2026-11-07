using AskVest.Modules.Catalog.Configuration;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Platform;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// Runs the reviewed data files through the importer. The real import is one
/// transaction: catalog writes, ledger asset registration, events and outbox
/// messages commit or roll back together. Validate-only runs every check
/// without writing.
/// </summary>
internal sealed class CatalogImportService(
    CatalogDbContext db,
    CatalogImportValidator validator,
    ILedger ledger,
    IOptions<CatalogOptions> options,
    IHostEnvironment environment,
    ITransactionScope scope,
    IOutbox outbox,
    ILogger<CatalogImportService> logger)
{
    public Task<Result<CatalogImportReport>> ImportAsync(CancellationToken cancellationToken = default)
        => RunAsync(validateOnly: false, cancellationToken);

    public Task<Result<CatalogImportReport>> ValidateAsync(CancellationToken cancellationToken = default)
        => RunAsync(validateOnly: true, cancellationToken);

    private async Task<Result<CatalogImportReport>> RunAsync(bool validateOnly, CancellationToken cancellationToken)
    {
        if (options.Value.LoadSampleData && environment.IsProduction())
        {
            return Result.Failure<CatalogImportReport>(new Error(
                ErrorCodes.BusinessRuleViolated,
                "Sample catalog data cannot be loaded in Production.",
                ErrorType.BusinessRule));
        }

        var problems = new List<string>();
        var dataSets = new List<CatalogDataSet>();
        var dataPath = ResolveDataPath(options.Value.DataPath);
        if (CatalogDataReader.TryRead(dataPath, out var real, out var realProblems))
        {
            dataSets.Add(real!);
        }
        else
        {
            problems.AddRange(realProblems);
        }

        if (options.Value.LoadSampleData)
        {
            var samplePath = options.Value.SampleDataPath is { } configured
                ? ResolveDataPath(configured)
                : dataPath + "-sample";
            if (CatalogDataReader.TryRead(samplePath, out var sample, out var sampleProblems))
            {
                dataSets.Add(sample!);
            }
            else
            {
                problems.AddRange(sampleProblems);
            }
        }

        if (problems.Count > 0)
        {
            return Invalid(problems);
        }

        if (validateOnly)
        {
            var checkedResult = await validator.ApplyAsync(dataSets, apply: false, cancellationToken);
            if (!checkedResult.IsValid)
            {
                return Invalid(checkedResult.Problems);
            }

            return Result<CatalogImportReport>.Success(Report(validateOnly: true, checkedResult));
        }

        await scope.BeginAsync(cancellationToken);
        try
        {
            // SaveChanges would otherwise open a nested transaction on the shared connection.
            await db.Database.UseTransactionAsync(scope.Transaction!, cancellationToken);
            var result = await validator.ApplyAsync(dataSets, apply: true, cancellationToken);
            if (!result.IsValid)
            {
                await scope.RollbackAsync(cancellationToken);
                return Invalid(result.Problems);
            }

            foreach (var (code, scale) in result.AssetsToRegister.Distinct())
            {
                var registered = await ledger.RegisterAssetAsync(code, AssetKind.Token, scale, cancellationToken);
                if (registered.IsFailure)
                {
                    await scope.RollbackAsync(cancellationToken);
                    return Result.Failure<CatalogImportReport>(registered.Error!);
                }
            }

            db.CatalogEvents.AddRange(result.Events);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var (messageType, payload, dedupeKey) in result.OutboxMessages)
            {
                await outbox.EnqueueAsync(messageType, payload, dedupeKey, cancellationToken);
            }

            await scope.CommitAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Catalog import committed: {Chains} chains, {Issuers} issuers, {Instruments} instruments, {Tokens} tokens, {Delisted} delisted, {Events} events, {Messages} outbox messages.",
                    result.ChainsUpserted, result.IssuersUpserted, result.InstrumentsUpserted,
                    result.TokensUpserted, result.TokensDelisted, result.Events.Count, result.OutboxMessages.Count);
            }
            return Result<CatalogImportReport>.Success(Report(validateOnly: false, result));
        }
        catch
        {
            await scope.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string ResolveDataPath(string path)
    {
        if (Path.IsPathRooted(path) || Directory.Exists(path))
        {
            return path;
        }

        var besideBinary = Path.Combine(AppContext.BaseDirectory, path);
        return Directory.Exists(besideBinary) ? besideBinary : path;
    }

    private static CatalogImportReport Report(bool validateOnly, CatalogImportResult result)
        => new(
            validateOnly,
            result.ChainsUpserted,
            result.IssuersUpserted,
            result.InstrumentsUpserted,
            result.TokensUpserted,
            result.TokensDelisted,
            result.Events.Count,
            result.OutboxMessages.Count,
            result.Warnings);

    private static Result<CatalogImportReport> Invalid(List<string> problems)
        => Result.Failure<CatalogImportReport>(new Error(
            ErrorCodes.ValidationFailed,
            $"Catalog data is invalid: {string.Join("; ", problems)}",
            ErrorType.Validation));
}
