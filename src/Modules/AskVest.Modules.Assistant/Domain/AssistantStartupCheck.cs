using AskVest.Modules.Assistant.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Assistant.Domain;

/// <summary>
/// Loads the system prompt and evaluation set during host startup so a bad
/// file or a version mismatch stops the process before it serves traffic.
/// </summary>
internal sealed class AssistantStartupCheck(IOptions<AssistantOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        AssistantContentLoader.LoadPrompt(
            Path.Combine(AppContext.BaseDirectory, settings.PromptPath),
            settings.PolicyVersion);
        AssistantContentLoader.LoadEvaluationSet(
            Path.Combine(AppContext.BaseDirectory, settings.EvaluationSetPath));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
