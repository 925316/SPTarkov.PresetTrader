using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace PresetTrader;

/// <summary>
/// Detects purchases from this trader ahead of ItemEventStaticRouter.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers - 1)]
public class PresetTraderItemMovingRouter(
    JsonUtil jsonUtil,
    PresetTraderRefresher refresher,
    ISptLogger<PresetTraderItemMovingRouter> logger
) : StaticRouter(
    jsonUtil,
    [
        new RouteAction<ItemEventRouterRequest>(
            "/client/game/profile/items/moving",
            (url, info, sessionID, output, cancellationToken) =>
            {
                NotifyIfPurchaseFromTrader(info, refresher, logger);
                return ValueTask.FromResult(string.Empty);
            }
        ),
    ]
)
{
    private static void NotifyIfPurchaseFromTrader(
        ItemEventRouterRequest info,
        PresetTraderRefresher refresher,
        ISptLogger<PresetTraderItemMovingRouter> logger)
    {
        try
        {
            if (info.Data is not { Count: > 0 })
            {
                return;
            }

            var traderId = refresher.GetTraderIdString();
            if (string.IsNullOrEmpty(traderId))
            {
                return;
            }

            foreach (var evt in info.Data)
            {
                if (evt.ValueKind == JsonValueKind.Object
                    && evt.TryGetProperty("Action", out var actionEl)
                    && actionEl.GetString() == ItemEventActions.TRADING_CONFIRM
                    && evt.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "buy_from_trader"
                    && evt.TryGetProperty("tid", out var tidEl)
                    && tidEl.GetString() == traderId)
                {
                    refresher.NotifyTrade();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[PresetTrader]: Failed to inspect item moving event: {ex}");
        }
    }
}
