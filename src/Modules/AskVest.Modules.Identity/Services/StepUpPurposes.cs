namespace AskVest.Modules.Identity.Services;

public static class StepUpPurposes
{
    public const string AuthorizeOrder = "authorize_order";
    public const string AuthorizeWithdrawal = "authorize_withdrawal";
    public const string ChangeWithdrawalAddress = "change_withdrawal_address";
    public const string EnableEarn = "enable_earn";
    public const string DeleteAccount = "delete_account";
    public const string ChangePin = "change_pin";

    private static readonly HashSet<string> _known = new(StringComparer.Ordinal)
    {
        AuthorizeOrder,
        AuthorizeWithdrawal,
        ChangeWithdrawalAddress,
        EnableEarn,
        DeleteAccount,
        ChangePin,
    };

    public static bool IsKnown(string? purpose) => purpose is not null && _known.Contains(purpose);
}
