namespace AskVest.Modules.Catalog.Import.Data;

internal sealed record TradingScheduleData
{
    public required string Kind { get; init; }

    public string? TimeZone { get; init; }

    public string? Note { get; init; }
}
