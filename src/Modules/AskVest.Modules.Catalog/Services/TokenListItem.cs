using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Persistence.Entities;

namespace AskVest.Modules.Catalog.Services;

internal sealed record TokenListItem(Token Token, TokenAvailability? Availability);
