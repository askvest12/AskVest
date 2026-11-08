using AskVest.Modules.MarketData.Domain.Enums;

namespace AskVest.Modules.MarketData.Domain;

public sealed record FreshnessAssessment(
    FreshnessLabel Label,
    long AgeSeconds,
    bool UsableForExecution);
