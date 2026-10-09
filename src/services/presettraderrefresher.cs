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
[Injectable(InjectionType.Singleton, TypePriority = OnUpdateOrder.InsuranceCallbacks)]
public partial class PresetTraderRefresher(
    ISptLogger<PresetTraderRefresher> logger,
    ProfileHelper profileHelper,
    TradersTable tradersTable,
    ItemHelper itemHelper,
    ICloner cloner,
    ModHelper modHelper,
    JsonUtil jsonUtil)
    : IOnUpdate
{
    private const string TraderRootParentId = "hideout";
    private const int RefreshIntervalSeconds = 30;
    private const int RotationDecisionDelayMs = 250;
    private const int CompanionWindowMs = 500;
    private const int PostPurchaseSuppressMs = 1500;
    private const int MinRotationIntervalMs = 500;

    private const int RotationBuilds = 0;
    private const int RotationKeyPacks = 1;
    private const int RotationGunsmith = 2;
    private const int RotationStateCount = 3;

    private readonly object _assortGate = new();

    private MongoId? _traderId;
    private int _rotationState;
    private long _lastOtherTraderAssortFetchUnixMs;
    private long _lastPurchaseUnixMs;
    private long _lastRotationUnixMs;
    private readonly Dictionary<MongoId, MongoId> _buildRootIds = new();
    private readonly Dictionary<MongoId, string> _buildSignatures = new();

    // Assort root id -> key pack template tree (full pack from keyPresets.json).
    // Rebuilt on every populate; read by the post-purchase delivery router.
    private readonly Dictionary<MongoId, List<Item>> _keyPackByRootId = new();

    public void SetTraderId(MongoId traderId)
    {
        _traderId = traderId;
    }

    public string GetTraderIdString()
    {
        return _traderId?.ToString() ?? string.Empty;
    }

    private static long CurrentUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        if (_traderId is null)
        {
            return Task.FromResult(true);
        }

        if (_rotationState != RotationBuilds)
        {
            return Task.FromResult(false);
        }

        if (secondsSinceLastRun < RefreshIntervalSeconds)
        {
            return Task.FromResult(false);
        }

        Refresh();

        return Task.FromResult(true);
    }

    public int Refresh()
    {
        if (_traderId is null)
        {
            return 0;
        }

        var traderId = _traderId.Value;

        lock (_assortGate)
        {
            return RefreshCore(traderId);
        }
    }

}
