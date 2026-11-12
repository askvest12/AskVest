namespace AskVest.Modules.Trading.Configuration;

public sealed class GatewayRouteOptions
{
    public string Provider { get; set; } = "fake";

    public string SettlementAsset { get; set; } = "USD";
}
