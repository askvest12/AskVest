namespace AskVest.Modules.Compliance.Domain.Enums;

public enum ComplianceEventType
{
    ConsentAccepted,
    CountrySet,
    CountryChanged,
    KycStarted,
    KycApproved,
    KycRejected,
    ScreeningClear,
    ScreeningHit,
    WebhookRejected,
}
