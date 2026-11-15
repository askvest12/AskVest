namespace AskVest.Modules.Funding.Domain;

public enum FundingEventType
{
    FundingAccountCreated,
    DepositStateChanged,
    ConversionCompleted,
    BeneficiaryStateChanged,
    WithdrawalStateChanged,
    RestrictionAdded,
    RestrictionCleared,
    PauseChanged,
    OperatorAction,
}
