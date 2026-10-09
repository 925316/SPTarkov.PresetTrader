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
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class Main(
    ISptLogger<Main> logger,
    ModHelper modHelper,
    PresetTraderRefresher refresher
)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, "db/base.json");
        if (traderBase is null)
        {
            logger.Error("[PresetTrader]: db/base.json not found, aborting preset population");
            return Task.CompletedTask;
        }

        refresher.SetTraderId(traderBase.Id);
        refresher.InitialLoad();

        return Task.CompletedTask;
    }
}
