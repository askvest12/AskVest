namespace AskVest.Modules.Funding.Domain;

public enum ConversionState
{
    Quoted,
    Completed,
    Expired,
}

/// <summary>
/// A conversion quote completes at most once; expiry is lazy (checked
/// against the stored expiry) and the cleanup job removes old rows.
/// </summary>
public static class ConversionTransitions
{
    public static bool CanTransition(ConversionState from, ConversionState to)
        => from == ConversionState.Quoted
            && to is ConversionState.Completed or ConversionState.Expired;

    public static bool IsTerminal(ConversionState state)
        => state is ConversionState.Completed or ConversionState.Expired;
}
