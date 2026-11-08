using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

namespace AskVest.Modules.Learning.Configuration;

public sealed class LearningOptions
{
    public const string SectionName = "Learning";

    [Required]
    public string ContentPath { get; set; } = "data/learning";

    public string? SampleContentPath { get; set; }

    public bool LoadSampleContent { get; set; }

    public Collection<string> BannedPhrases { get; } = [];
}
