using AskVest.Modules.Catalog.Persistence.Entities;

namespace AskVest.Modules.Catalog.Services;

internal sealed record InstrumentDetail(Instrument Instrument, IReadOnlyList<TokenListItem> Tokens);
