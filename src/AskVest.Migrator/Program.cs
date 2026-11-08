using AskVest.Modules.Catalog;
using AskVest.Modules.Catalog.Import;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Compliance;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Identity;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Learning;
using AskVest.Modules.Learning.Persistence;
using AskVest.Modules.Ledger;
using AskVest.Modules.Ledger.Persistence;
using AskVest.Modules.MarketData;
using AskVest.Modules.MarketData.Persistence;
using AskVest.Modules.Practice;
using AskVest.Modules.Practice.Persistence;
using AskVest.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((_, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AddPlatformPersistence(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddComplianceModule(builder.Configuration);
builder.Services.AddLedgerModule(builder.Configuration);
builder.Services.AddCatalogModule(builder.Configuration);
builder.Services.AddMarketDataModule(builder.Configuration);
builder.Services.AddPracticeModule(builder.Configuration);
builder.Services.AddLearningModule(builder.Configuration, builder.Environment);

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AskVest.Migrator");

var connectionString = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(connectionString))
{
    logger.LogCritical("ConnectionStrings:Platform is required.");
    return 1;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
try
{
    var applied = await MigrationRunner.RunAsync(
        connectionString,
        [
            connectionString => new IdentityDbContext(IdentityDbContextOptions.Create(connectionString)),
            connectionString => new ComplianceDbContext(ComplianceDbContextOptions.Create(connectionString)),
            connectionString => new LedgerDbContext(LedgerDbContextOptions.Create(connectionString)),
            connectionString => new CatalogDbContext(CatalogDbContextOptions.Create(connectionString)),
            connectionString => new MarketDataDbContext(MarketDataDbContextOptions.Create(connectionString)),
            connectionString => new PracticeDbContext(PracticeDbContextOptions.Create(connectionString)),
            connectionString => new LearningDbContext(LearningDbContextOptions.Create(connectionString)),
        ],
        cancellation.Token);
    if (logger.IsEnabled(LogLevel.Information))
    {
        if (applied.Count == 0)
        {
            logger.LogInformation("No pending migrations.");
        }
        else
        {
            foreach (var migrationId in applied)
            {
                logger.LogInformation("Applied migration {MigrationId}.", migrationId);
            }
        }
    }

    await using var scope = host.Services.CreateAsyncScope();
    var import = await scope.ServiceProvider
        .GetRequiredService<CatalogImportService>()
        .ImportAsync(cancellation.Token);
    if (import.IsFailure)
    {
        if (logger.IsEnabled(LogLevel.Critical))
        {
            logger.LogCritical("Catalog import failed: {Error}", import.Error!.Message);
        }

        return 1;
    }

    var report = import.Value;
    if (logger.IsEnabled(LogLevel.Information))
    {
        logger.LogInformation(
            "Catalog import finished: validateOnly={ValidateOnly} chains={Chains} issuers={Issuers} instruments={Instruments} tokens={Tokens} delisted={Delisted} events={Events} outbox={Outbox} warnings={Warnings}.",
            report.ValidateOnly, report.Chains, report.Issuers, report.Instruments,
            report.Tokens, report.TokensDelisted, report.Events, report.OutboxMessages, report.Warnings.Count);
    }

    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    logger.LogCritical(exception, "Migration run failed.");
    return 1;
}
