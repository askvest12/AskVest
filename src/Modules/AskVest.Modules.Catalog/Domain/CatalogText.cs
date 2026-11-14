using AskVest.Modules.Catalog.Domain.Enums;

namespace AskVest.Modules.Catalog.Domain;

internal static class CatalogText
{
    public static string ToDb(ChainStatus value) => value switch
    {
        ChainStatus.Enabled => "enabled",
        ChainStatus.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ChainStatus ChainStatusFromDb(string value) => value switch
    {
        "enabled" => ChainStatus.Enabled,
        "disabled" => ChainStatus.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(IssuerStatus value) => value switch
    {
        IssuerStatus.Approved => "approved",
        IssuerStatus.Suspended => "suspended",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static IssuerStatus IssuerStatusFromDb(string value) => value switch
    {
        "approved" => IssuerStatus.Approved,
        "suspended" => IssuerStatus.Suspended,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(InstrumentKind value) => value switch
    {
        InstrumentKind.Stock => "stock",
        InstrumentKind.Etf => "etf",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static InstrumentKind InstrumentKindFromDb(string value) => value switch
    {
        "stock" => InstrumentKind.Stock,
        "etf" => InstrumentKind.Etf,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(InstrumentStatus value) => value switch
    {
        InstrumentStatus.Active => "active",
        InstrumentStatus.Delisted => "delisted",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static InstrumentStatus InstrumentStatusFromDb(string value) => value switch
    {
        "active" => InstrumentStatus.Active,
        "delisted" => InstrumentStatus.Delisted,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(TokenStatus value) => value switch
    {
        TokenStatus.Draft => "draft",
        TokenStatus.Listed => "listed",
        TokenStatus.Suspended => "suspended",
        TokenStatus.Delisted => "delisted",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static TokenStatus TokenStatusFromDb(string value) => value switch
    {
        "draft" => TokenStatus.Draft,
        "listed" => TokenStatus.Listed,
        "suspended" => TokenStatus.Suspended,
        "delisted" => TokenStatus.Delisted,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(ClaimType value) => value switch
    {
        ClaimType.DirectShareClaim => "direct_share_claim",
        ClaimType.FundUnitClaim => "fund_unit_claim",
        ClaimType.SyntheticExposure => "synthetic_exposure",
        ClaimType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ClaimType ClaimTypeFromDb(string value) => value switch
    {
        "direct_share_claim" => ClaimType.DirectShareClaim,
        "fund_unit_claim" => ClaimType.FundUnitClaim,
        "synthetic_exposure" => ClaimType.SyntheticExposure,
        "other" => ClaimType.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(DividendPolicy value) => value switch
    {
        DividendPolicy.Distributed => "distributed",
        DividendPolicy.Reinvested => "reinvested",
        DividendPolicy.NotPassedThrough => "not_passed_through",
        DividendPolicy.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static DividendPolicy DividendPolicyFromDb(string value) => value switch
    {
        "distributed" => DividendPolicy.Distributed,
        "reinvested" => DividendPolicy.Reinvested,
        "not_passed_through" => DividendPolicy.NotPassedThrough,
        "other" => DividendPolicy.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(TradingScheduleKind value) => value switch
    {
        TradingScheduleKind.Always => "always",
        TradingScheduleKind.UnderlyingMarketHours => "underlying_market_hours",
        TradingScheduleKind.IssuerWindows => "issuer_windows",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static TradingScheduleKind TradingScheduleKindFromDb(string value) => value switch
    {
        "always" => TradingScheduleKind.Always,
        "underlying_market_hours" => TradingScheduleKind.UnderlyingMarketHours,
        "issuer_windows" => TradingScheduleKind.IssuerWindows,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(ExternalTransferPolicy value) => value switch
    {
        ExternalTransferPolicy.Allowed => "allowed",
        ExternalTransferPolicy.Restricted => "restricted",
        ExternalTransferPolicy.NotSupported => "not_supported",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ExternalTransferPolicy ExternalTransferPolicyFromDb(string value) => value switch
    {
        "allowed" => ExternalTransferPolicy.Allowed,
        "restricted" => ExternalTransferPolicy.Restricted,
        "not_supported" => ExternalTransferPolicy.NotSupported,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(CatalogEventType value) => value switch
    {
        CatalogEventType.Imported => "imported",
        CatalogEventType.Listed => "listed",
        CatalogEventType.Suspended => "suspended",
        CatalogEventType.Relisted => "relisted",
        CatalogEventType.Delisted => "delisted",
        CatalogEventType.Updated => "updated",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CatalogEventType CatalogEventTypeFromDb(string value) => value switch
    {
        "imported" => CatalogEventType.Imported,
        "listed" => CatalogEventType.Listed,
        "suspended" => CatalogEventType.Suspended,
        "relisted" => CatalogEventType.Relisted,
        "delisted" => CatalogEventType.Delisted,
        "updated" => CatalogEventType.Updated,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
