using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Persistence.Entities;

namespace AskVest.Modules.Catalog.Services;

internal sealed record TokenDetail(
    Token Token,
    Issuer? Issuer,
    Chain? Chain,
    IReadOnlyList<string> Countries,
    TokenAvailability? Availability,
    string? ExplorerLink);
