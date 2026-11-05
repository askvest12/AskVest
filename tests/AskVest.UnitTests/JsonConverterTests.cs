using System.Text.Json;
using AskVest.Api;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class JsonConverterTests
{
    private static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        JsonOptionsConfiguration.Configure(options);
        return options;
    }

    [Fact]
    public void Decimal_SerializesAsInvariantString()
    {
        var json = JsonSerializer.Serialize(12.34m, Options);
        json.ShouldBe("\"12.34\"");
    }

    [Fact]
    public void Decimal_RejectsJsonNumber()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<decimal>("12.34", Options));
    }

    [Fact]
    public void Decimal_AcceptsString()
        => JsonSerializer.Deserialize<decimal>("\"12.340\"", Options).ShouldBe(12.340m);

    [Fact]
    public void Timestamp_SerializesWithZ()
    {
        var value = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        JsonSerializer.Serialize(value, Options).ShouldBe("\"2026-10-16T12:00:00.0000000Z\"");
    }

    [Fact]
    public void Timestamp_ConvertsOffsetToUtc()
    {
        var value = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-10-16T14:00:00+02:00\"", Options);
        value.Offset.ShouldBe(TimeSpan.Zero);
        value.Hour.ShouldBe(12);
    }

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Probe>("{\"known\":1,\"extra\":2}", Options));
    }

    private sealed record Probe(int Known);
}
