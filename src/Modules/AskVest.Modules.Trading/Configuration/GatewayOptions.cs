namespace AskVest.Modules.Trading.Configuration;

/// <summary>
/// Gateway routes keyed by the catalog token gateway key. Only the fake
/// provider exists in this phase; a real adapter registers its own provider
/// name here when research picks a venue.
/// </summary>
public sealed class GatewayOptions
{
    public Dictionary<string, GatewayRouteOptions> Routes { get; } = new(StringComparer.Ordinal);
}
