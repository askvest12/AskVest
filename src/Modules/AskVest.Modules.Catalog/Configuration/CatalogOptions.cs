using System.ComponentModel.DataAnnotations;

namespace AskVest.Modules.Catalog.Configuration;

public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    [Required]
    public string DataPath { get; set; } = "data/catalog";

    public string? SampleDataPath { get; set; }

    public bool LoadSampleData { get; set; }

    [Range(1, 120)]
    public int ReviewMaxMonths { get; set; } = 12;
}
