namespace AskVest.Modules.Assistant.Policy;

/// <summary>One labelled value inside a fact card. Values are exact strings.</summary>
internal sealed record FactCardField(string Name, string Value);
