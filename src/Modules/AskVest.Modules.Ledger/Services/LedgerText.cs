using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Ledger.Services;

internal static class LedgerText
{
    public static string ToDb(Book book) => book == Book.Practice ? "practice" : "real";

    public static string ToDb(AssetKind kind) => kind switch
    {
        AssetKind.Fiat => "fiat",
        AssetKind.Stablecoin => "stablecoin",
        _ => "token",
    };

    public static string ToDb(AccountOwnerType type) => type == AccountOwnerType.User ? "user" : "system";

    public static string ToDb(AccountKind kind) => kind switch
    {
        AccountKind.UserAvailable => "user_available",
        AccountKind.UserHeld => "user_held",
        AccountKind.SystemClearing => "system_clearing",
        AccountKind.SystemFees => "system_fees",
        _ => "system_practice_issuance",
    };

    public static Book BookFromDb(string value) => value == "practice" ? Book.Practice : Book.Real;

    public static AssetKind AssetKindFromDb(string value) => value switch
    {
        "fiat" => AssetKind.Fiat,
        "stablecoin" => AssetKind.Stablecoin,
        _ => AssetKind.Token,
    };

    public static AccountOwnerType OwnerTypeFromDb(string value) => value == "user" ? AccountOwnerType.User : AccountOwnerType.System;

    public static AccountKind AccountKindFromDb(string value) => value switch
    {
        "user_available" => AccountKind.UserAvailable,
        "user_held" => AccountKind.UserHeld,
        "system_clearing" => AccountKind.SystemClearing,
        "system_fees" => AccountKind.SystemFees,
        _ => AccountKind.SystemPracticeIssuance,
    };
}
