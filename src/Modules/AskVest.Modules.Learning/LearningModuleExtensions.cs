using AskVest.Modules.Learning.Configuration;
using AskVest.Modules.Learning.Content;
using AskVest.Modules.Learning.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Learning;

public static class LearningModuleExtensions
{
    public static IServiceCollection AddLearningModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection(LearningOptions.SectionName).Get<LearningOptions>()
            ?? new LearningOptions();
        var snapshot = LearningContentLoader.Load(options, environment);

        services.AddSingleton<IValidateOptions<LearningOptions>>(LearningOptionsValidator.Instance);
        services.AddOptions<LearningOptions>()
            .Bind(configuration.GetSection(LearningOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(snapshot);
        services.AddScoped<Services.LearningReadService>();
        services.AddScoped<Services.LearningCommandService>();
        services.AddScoped<Services.LearningProgressService>();
        services.AddValidatorsFromAssembly(typeof(LearningModuleExtensions).Assembly);
        services.TryAddSingleton(TimeProvider.System);

        var connectionString = configuration.GetConnectionString("Learning")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("ConnectionStrings:Learning or ConnectionStrings:Platform is required.");

        services.AddDbContext<LearningDbContext>((IServiceProvider provider, DbContextOptionsBuilder optionsBuilder) =>
        {
            var platformOptions = provider.GetRequiredService<IOptions<AskVest.Platform.PlatformOptions>>().Value;
            var scope = provider.GetService<AskVest.Platform.ITransactionScope>();
            LearningDbContextOptions.Configure(optionsBuilder, connectionString, platformOptions.CommandTimeoutSeconds, scope?.Connection);
        });

        return services;
    }
}
