using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Configuration;

namespace AskVest.Modules.MarketData.Services;

internal static class ProviderSymbols
{
    public static string For(CatalogInstrument instrument, MarketDataOptions settings)
        => settings.SymbolOverrides.TryGetValue(instrument.Id, out var mapped)
            ? mapped
            : $"{instrument.Symbol}:{instrument.ListingExchange ?? instrument.CountryOfListing}";
}
