namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// What one import run did (or would do in validate-only mode).
/// </summary>
internal sealed record CatalogImportReport(
    bool ValidateOnly,
    int Chains,
    int Issuers,
    int Instruments,
    int Tokens,
    int TokensDelisted,
    int Events,
    int OutboxMessages,
    IReadOnlyList<string> Warnings);
