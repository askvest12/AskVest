using System.Text.Json;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// Strict argument parsing for tool calls. Every check is explicit: the
/// object must contain only known properties, required fields must exist
/// with the right JSON kind, lists are capped, and no property named like
/// a user id is ever accepted.
/// </summary>
internal static class ToolArguments
{
    public const string ForbiddenUserIdProperty = "user_id_not_allowed";

    public static bool ContainsUserIdProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains("userid", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("user_id", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("user-id", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (ContainsUserIdProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsUserIdProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string? UnknownProperty(JsonElement arguments, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
            {
                return property.Name;
            }
        }

        return null;
    }

    public static string? RequireString(JsonElement arguments, string name, int maxLength)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return $"'{name}' is required and must be a string.";
        }

        var text = value.GetString()!;
        return text.Length is 0 || text.Length > maxLength
            ? $"'{name}' must be between 1 and {maxLength} characters."
            : null;
    }

    public static string? OptionalString(JsonElement arguments, string name, int maxLength)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return $"'{name}' must be a string.";
        }

        var text = value.GetString()!;
        return text.Length > maxLength ? $"'{name}' must be at most {maxLength} characters." : null;
    }

    public static string? RequireStringArray(
        JsonElement arguments,
        string name,
        int minItems,
        int maxItems,
        int maxItemLength,
        out string[] items)
    {
        items = [];
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return $"'{name}' is required and must be an array.";
        }

        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length is 0 or > 128)
            {
                return $"'{name}' entries must be non-empty strings of at most 128 characters.";
            }
        }

        if (count < minItems || count > maxItems)
        {
            return $"'{name}' must contain between {minItems} and {maxItems} entries.";
        }

        items = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        return null;
    }

    public static bool TryGetOptionalInt(JsonElement arguments, string name, int min, int max, out int value)
    {
        value = 0;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed)
            || parsed < min || parsed > max)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryGetOptionalDecimalString(JsonElement arguments, string name, out decimal value)
    {
        value = 0;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String
            || !decimal.TryParse(element.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0 || parsed > 1_000_000_000m)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static string? OptionalEnum(JsonElement arguments, string name, params string[] allowed)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return $"'{name}' must be a string.";
        }

        var text = value.GetString()!;
        return allowed.Contains(text, StringComparer.Ordinal) ? null : $"'{name}' must be one of: {string.Join(", ", allowed)}.";
    }

    public static bool TryGetTimestamp(JsonElement arguments, string name, out DateTimeOffset value)
    {
        value = default;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            element.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out value);
    }
}
