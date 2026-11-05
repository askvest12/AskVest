namespace AskVest.Modules.Identity.Domain.Enums;

public enum UserStatus
{
    PendingVerification,
    Active,
    Disabled,
    DeletionRequested,
    Anonymized,
}
