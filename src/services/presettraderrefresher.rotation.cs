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
public partial class PresetTraderRefresher
{
    public void RecordOtherTraderAssortFetch()
    {
        Interlocked.Exchange(ref _lastOtherTraderAssortFetchUnixMs, CurrentUnixMs());
    }

    /// <summary>
    /// Rotates on a deliberate open/refresh: a lone assort fetch that is
    /// neither a bulk-refresh companion nor the post-purchase auto-refresh.
    /// </summary>
    public async Task OnOwnTraderAssortFetchedAsync(MongoId sessionId, CancellationToken cancellationToken)
    {
        if (_traderId is null)
        {
            return;
        }

        // Let companion fetches land before deciding.
        try
        {
            await Task.Delay(RotationDecisionDelayMs, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        var now = CurrentUnixMs();

        if (now - Interlocked.Read(ref _lastOtherTraderAssortFetchUnixMs) < CompanionWindowMs)
        {
            return;
        }

        if (now - Interlocked.Read(ref _lastPurchaseUnixMs) < PostPurchaseSuppressMs)
        {
            return;
        }

        if (now - Interlocked.Read(ref _lastRotationUnixMs) < MinRotationIntervalMs)
        {
            return;
        }

        Interlocked.Exchange(ref _lastRotationUnixMs, now);
        Rotate(_traderId.Value, sessionId);
    }

    public void NotifyTrade()
    {
        Interlocked.Exchange(ref _lastPurchaseUnixMs, CurrentUnixMs());
    }


    public void InitialLoad()
    {
        if (_traderId is null)
        {
            return;
        }

        var traderId = _traderId.Value;

        if (!tradersTable.TryGetValue(traderId, out var traderData))
        {
            logger.Error($"[PresetTrader]: Trader {traderId} not found, cannot load presets");
            return;
        }

        _rotationState = RotationBuilds;

        ValidateQuestPresetMapping();

        lock (_assortGate)
        {
            _buildRootIds.Clear();
            _buildSignatures.Clear();

            var buildCount = RefreshCore(traderId);
            var gearCount = PopulatePresets(traderData, "db/gearPresets.json");
            var armorCount = PopulatePresets(traderData, "db/armorPresets.json");

            logger.Success(
                $"[PresetTrader]: Loaded {buildCount} weapon build(s), {gearCount} gear preset(s) and {armorCount} armor preset(s) to trader {traderId}. Opening the trader or refreshing rotates through key packs and gunsmith/quest weapon presets.");
        }
    }

    private void Rotate(MongoId traderId, MongoId sessionId)
    {
        if (!tradersTable.TryGetValue(traderId, out var traderData))
        {
            logger.Error($"[PresetTrader]: Trader {traderId} not found, cannot rotate assorts");
            return;
        }

        lock (_assortGate)
        {
            var questNames = GetActiveQuestPresetNames(sessionId);
            var questSet = questNames.Count > 0
                ? new HashSet<string>(questNames, StringComparer.Ordinal)
                : null;

            _rotationState = (_rotationState + 1) % (questSet is null ? 2 : RotationStateCount);

            traderData.Assort.Items.Clear();
            traderData.Assort.BarterScheme.Clear();
            traderData.Assort.LoyalLevelItems.Clear();

            _buildRootIds.Clear();
            _buildSignatures.Clear();
            _keyPackByRootId.Clear();

            switch (_rotationState)
            {
                case RotationBuilds:
                    RefreshCore(traderId);
                    PopulatePresets(traderData, "db/gearPresets.json");
                    PopulatePresets(traderData, "db/armorPresets.json");
                    break;
                case RotationKeyPacks:
                    PopulateKeyPacks(traderData, "db/keyPresets.json");
                    break;
                case RotationGunsmith:
                    PopulateWeaponPresets(traderData, "db/weaponPresets.json", questSet);
                    break;
            }
        }
    }

}
