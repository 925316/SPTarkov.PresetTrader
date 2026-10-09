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
/// Runs before SPT's TraderDynamicRouter (Routers=400000) so a rotation (if
/// any) happens before the assort snapshot. Returns empty; SPT serves the
/// (possibly rotated) assort.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers - 1)]
public class PresetTraderAssortRouter(
    JsonUtil jsonUtil,
    PresetTraderRefresher refresher
) : DynamicRouter(
    jsonUtil,
    [
        new RouteAction<EmptyRequestData>(
            "/client/trading/api/getTraderAssort/",
            async             (url, info, sessionID, output, cancellationToken) =>
            {
                var path = url.Split('?')[0];
                var traderId = path.Substring(path.LastIndexOf('/') + 1);

                if (traderId == refresher.GetTraderIdString())
                {
                    await refresher.OnOwnTraderAssortFetchedAsync(cancellationToken);
                }
                else
                {
                    refresher.RecordOtherTraderAssortFetch();
                }

                return string.Empty;
            }
        ),
    ]
) { }
