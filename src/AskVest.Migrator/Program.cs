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
    var applied = await MigrationRunner.RunAsync(connectionString, cancellation.Token);
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

    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    logger.LogCritical(exception, "Migration run failed.");
    return 1;
}
