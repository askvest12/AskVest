using AskVest.Modules.Ledger.Endpoints;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Ledger;

public static class LedgerEndpointsExtensions
{
    public static void MapLedgerEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
        => LedgerEndpoints.Map(api, probesEnabled);
}
