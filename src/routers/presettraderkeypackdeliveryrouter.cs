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
/// Runs AFTER ItemEventStaticRouter (OnLoadOrder.Routers): receives the full
/// items/moving response envelope as `output` and rewrites it to deliver the
/// contents of purchased key packs (empty container sold on the shelf, keys
/// injected into the bought instance). Returns the input untouched when the
/// request contains no key pack purchase or anything is missing.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class PresetTraderKeyPackDeliveryRouter(
    JsonUtil jsonUtil,
    PresetTraderRefresher refresher
) : StaticRouter(
    jsonUtil,
    [
        new RouteAction<ItemEventRouterRequest>(
            "/client/game/profile/items/moving",
            (url, info, sessionID, output, cancellationToken) =>
                ValueTask.FromResult(refresher.TryInjectKeyPacks(sessionID, info, output))
        ),
    ]
) { }
