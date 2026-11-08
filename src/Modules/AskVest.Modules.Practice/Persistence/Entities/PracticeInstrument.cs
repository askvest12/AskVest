namespace AskVest.Modules.Practice.Persistence.Entities;

public sealed class PracticeInstrument
{
    public string InstrumentId { get; set; } = "";

    public string AssetCode { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
