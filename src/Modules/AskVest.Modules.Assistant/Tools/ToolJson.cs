using System.Globalization;
using System.Text.Json;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// Payload serialization for tool results. Decimals are emitted as
/// invariant strings, matching the API convention that exact figures are
/// never JSON numbers.
/// </summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string Dec(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    public static string? Dec(decimal? value)
        => value?.ToString("G29", CultureInfo.InvariantCulture);

    public static string Ts(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
