using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AskVest.Api;

internal sealed class StrictDecimalJsonConverter : JsonConverter<decimal>
{
    private const NumberStyles AllowedStyles = NumberStyles.AllowDecimalPoint
        | NumberStyles.AllowLeadingSign
        | NumberStyles.AllowLeadingWhite
        | NumberStyles.AllowTrailingWhite;

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (decimal.TryParse(text, AllowedStyles, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        throw new JsonException("Decimal values must be JSON strings in invariant decimal notation.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
