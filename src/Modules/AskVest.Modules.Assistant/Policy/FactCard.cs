namespace AskVest.Modules.Assistant.Policy;

/// <summary>
/// One server-built fact card: the trusted numbers and labels a client may
/// display instead of trusting the model's wording.
/// </summary>
internal sealed record FactCard(
    string CardType,
    string Title,
    IReadOnlyList<FactCardField> Fields);
