using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;

namespace AskVest.Api;

internal static class JsonOptionsConfiguration
{
    public static void Configure(JsonSerializerOptions serializerOptions)
    {
        serializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        serializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        serializerOptions.Converters.Add(new StrictDecimalJsonConverter());
        serializerOptions.Converters.Add(new UtcTimestampJsonConverter());
        serializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        serializerOptions.NumberHandling = JsonNumberHandling.Strict;
    }

    public static void Configure(Microsoft.AspNetCore.Http.Json.JsonOptions options)
        => Configure(options.SerializerOptions);
}
