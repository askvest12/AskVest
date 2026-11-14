using AskVest.SharedKernel;

namespace AskVest.Modules.Wallets.Screening;

public enum ScreeningOutcomeKind
{
    Clear,
    Hit,
    Error,
}

/// <summary>
/// The screening verdict for one chain id and address. The reference is an
/// opaque provider correlation id, safe to store and show.
/// </summary>
public sealed record ScreeningResult(
    ScreeningOutcomeKind Kind,
    string? Reference);
