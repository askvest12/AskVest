using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Catalog.Persistence.Entities;

namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// A fully validated token write, produced during validation and applied
/// only when the whole import commits.
/// </summary>
internal sealed class TokenPlan
{
    public required string InstrumentId { get; init; }

    public required string Version { get; init; }

    public required TokenData Data { get; init; }

    public required TokenStatus Status { get; init; }

    public required TradingScheduleKind ScheduleKind { get; init; }

    public required ClaimType ClaimType { get; init; }

    public required DividendPolicy DividendPolicy { get; init; }

    public required string CanonicalAddress { get; init; }

    public required string Caip19 { get; init; }

    public required string AssetCode { get; init; }

    public required List<string> Countries { get; init; }

    public Token? Existing { get; init; }
}
