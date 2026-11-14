using AskVest.Modules.Learning.Configuration;
using Microsoft.Extensions.Hosting;

namespace AskVest.Modules.Learning.Content;

/// <summary>
/// Loads the real content folder plus the optional sample folder, validates
/// everything together and returns the immutable snapshot. Any problem stops
/// startup with the full list.
/// </summary>
internal static class LearningContentLoader
{
    public static readonly IReadOnlyList<string> DefaultBannedPhrases =
    [
        "guaranteed",
        "guarantee",
        "risk-free",
        "risk free",
        "cannot lose",
        "can't lose",
        "sure profit",
        "sure thing",
        "no risk",
        "profit guaranteed",
        "get rich",
        "non-custodial",
        "self-custody",
        "self custody",
        "your keys",
    ];

    public static LearningContentSnapshot Load(LearningOptions options, IHostEnvironment environment)
    {
        if (options.LoadSampleContent && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Sample learning content cannot be loaded in Production.");
        }

        var bannedPhrases = options.BannedPhrases.Count > 0
            ? options.BannedPhrases
            : DefaultBannedPhrases;
        var tracks = new List<TrackContent>();
        var problems = new List<string>();

        if (LearningContentReader.TryRead(options.ContentPath, out var real, out var realProblems))
        {
            tracks.AddRange(real!.Tracks);
        }
        else
        {
            problems.AddRange(realProblems);
        }

        if (options.LoadSampleContent)
        {
            var samplePath = options.SampleContentPath ?? options.ContentPath + "-sample";
            if (LearningContentReader.TryRead(samplePath, out var sample, out var sampleProblems))
            {
                tracks.AddRange(sample!.Tracks);
            }
            else
            {
                problems.AddRange(sampleProblems);
            }
        }

        if (problems.Count == 0)
        {
            problems.AddRange(LearningContentValidator.Validate(
                new ContentFile(tracks), bannedPhrases));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Learning content validation failed: {string.Join("; ", problems)}");
        }

        return new LearningContentSnapshot(tracks);
    }
}
