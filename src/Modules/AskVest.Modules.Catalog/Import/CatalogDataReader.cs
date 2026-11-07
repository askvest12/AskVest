using System.Text.Json;
using System.Text.Json.Serialization;
using AskVest.Modules.Catalog.Import.Data;

namespace AskVest.Modules.Catalog.Import;

internal sealed record CatalogDataSet(
    CatalogChainsFile Chains,
    CatalogIssuersFile Issuers,
    CatalogInstrumentsFile Instruments);

internal static class CatalogDataReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static bool TryRead(string directory, out CatalogDataSet? dataSet, out List<string> problems)
    {
        dataSet = null;
        problems = [];
        if (!Directory.Exists(directory))
        {
            problems.Add($"catalog data directory '{directory}' does not exist");
            return false;
        }

        var chains = ReadFile<CatalogChainsFile>(Path.Combine(directory, "chains.json"), "chains", problems);
        var issuers = ReadFile<CatalogIssuersFile>(Path.Combine(directory, "issuers.json"), "issuers", problems);
        var instruments = ReadFile<CatalogInstrumentsFile>(Path.Combine(directory, "instruments.json"), "instruments", problems);
        if (chains is null || issuers is null || instruments is null)
        {
            return false;
        }

        dataSet = new CatalogDataSet(chains, issuers, instruments);
        return problems.Count == 0;
    }

    private static T? ReadFile<T>(string path, string name, List<string> problems)
    {
        if (!File.Exists(path))
        {
            problems.Add($"catalog data file '{name}.json' is missing");
            return default;
        }

        try
        {
            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(json, SerializerOptions);
            if (value is null)
            {
                problems.Add($"catalog data file '{name}.json' is empty or invalid");
                return default;
            }

            return value;
        }
        catch (JsonException exception)
        {
            problems.Add($"catalog data file '{name}.json' is not valid strict JSON: {exception.Message}");
            return default;
        }
        catch (NotSupportedException exception)
        {
            problems.Add($"catalog data file '{name}.json' contains an unsupported value: {exception.Message}");
            return default;
        }
    }
}
