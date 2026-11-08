using System.Text.Json;
using System.Text.Json.Serialization;

namespace AskVest.Modules.Learning.Content;

internal static class LearningContentReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static bool TryRead(string directory, out ContentFile? content, out List<string> problems)
    {
        content = null;
        problems = [];
        var path = Path.Combine(directory, "content.json");
        if (!File.Exists(path))
        {
            problems.Add($"learning content file '{path}' is missing");
            return false;
        }

        try
        {
            var value = JsonSerializer.Deserialize<ContentFile>(File.ReadAllText(path), SerializerOptions);
            if (value is null)
            {
                problems.Add($"learning content file '{path}' is empty or invalid");
                return false;
            }

            content = value;
            return true;
        }
        catch (JsonException exception)
        {
            problems.Add($"learning content file '{path}' is not valid strict JSON: {exception.Message}");
            return false;
        }
        catch (NotSupportedException exception)
        {
            problems.Add($"learning content file '{path}' contains an unsupported value: {exception.Message}");
            return false;
        }
    }
}
