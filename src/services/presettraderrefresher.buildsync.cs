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
    // Caller must hold _assortGate.
    private int RefreshCore(MongoId traderId)
    {
        if (!tradersTable.TryGetValue(traderId, out var traderData))
        {
            logger.Error($"[PresetTrader]: Trader {traderId} not found, cannot refresh weapon builds");
            return 0;
        }

        var syncedCount = 0;
        var currentBuildIds = new HashSet<MongoId>();

        foreach (var (_, profile) in profileHelper.GetProfiles())
        {
            var weaponBuilds = profile?.UserBuildData?.WeaponBuilds;
            if (weaponBuilds is null || weaponBuilds.Count == 0)
            {
                continue;
            }

            foreach (var build in weaponBuilds)
            {
                if (build?.Items is null || build.Items.Count == 0)
                {
                    continue;
                }

                currentBuildIds.Add(build.Id);

                var signature = ComputeBuildSignature(build.Items);

                if (_buildRootIds.TryGetValue(build.Id, out var existingRootId)
                    && _buildSignatures.TryGetValue(build.Id, out var existingSignature)
                    && existingSignature == signature)
                {
                    continue;
                }

                if (_buildRootIds.TryGetValue(build.Id, out var staleRootId))
                {
                    RemoveBuildAssort(traderData, staleRootId);
                    _buildRootIds.Remove(build.Id);
                    _buildSignatures.Remove(build.Id);
                }

                try
                {
                    var itemList = cloner.Clone(build.Items);
                    if (itemList is null || itemList.Count == 0)
                    {
                        logger.Warning(
                            $"[PresetTrader]: Failed to clone build '{build.Name}' ({build.Id}), skipping");
                        continue;
                    }

                    var newRootId = itemList.RemapRootItemId();

                    var rootItem = itemList.FirstOrDefault(item => item.Id == newRootId);
                    if (rootItem is null)
                    {
                        logger.Warning(
                            $"[PresetTrader]: Root item not found for build '{build.Name}' ({build.Id}), skipping");
                        continue;
                    }

                    rootItem.ParentId = TraderRootParentId;
                    rootItem.SlotId = TraderRootParentId;
                    rootItem.Upd ??= new Upd();
                    rootItem.Upd.StackObjectsCount = 999;
                    rootItem.Upd.UnlimitedCount = true;
                    rootItem.Upd.BuyRestrictionCurrent = 0;

                    foreach (var part in itemList)
                    {
                        part.Upd ??= new Upd();
                        part.Upd.SpawnedInSession = itemHelper.IsOfBaseclass(part.Template, BaseClasses.AMMO)
                            ? null
                            : true;
                        NormalizeDurability(part);
                    }

                    var price = (int)itemList.Select(item => item.Template).Sum(itemHelper.GetItemMaxPrice);

                    if (price <= 0)
                    {
                        logger.Warning(
                            $"[PresetTrader]: Skipping build '{build.Name}' ({build.Id}) - total price is 0");
                        continue;
                    }

                    traderData.Assort.Items.AddRange(itemList);

                    traderData.Assort.BarterScheme[newRootId] =
                    [
                        [
                            new BarterScheme
                            {
                                Count = price,
                                Template = Money.ROUBLES
                            }
                        ]
                    ];

                    traderData.Assort.LoyalLevelItems[newRootId] = 1;

                    _buildRootIds[build.Id] = newRootId;
                    _buildSignatures[build.Id] = signature;

                    syncedCount++;
                }
                catch (Exception ex)
                {
                    logger.Error(
                        $"[PresetTrader]: Failed to process build '{build.Name}' ({build.Id}): {ex}");
                }
            }
        }

        foreach (var kvp in _buildRootIds.ToList())
        {
            if (!currentBuildIds.Contains(kvp.Key))
            {
                RemoveBuildAssort(traderData, kvp.Value);
                _buildRootIds.Remove(kvp.Key);
                _buildSignatures.Remove(kvp.Key);
            }
        }

        return syncedCount;
    }


    private void RemoveBuildAssort(Trader traderData, MongoId rootId)
    {
        var toRemove = new HashSet<MongoId> { rootId };
        var queue = new Queue<MongoId>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var item in traderData.Assort.Items)
            {
                if (item.ParentId is not null && item.ParentId.Equals(current) && toRemove.Add(item.Id))
                {
                    queue.Enqueue(item.Id);
                }
            }
        }

        traderData.Assort.Items.RemoveAll(item => toRemove.Contains(item.Id));
        traderData.Assort.BarterScheme.Remove(rootId);
        traderData.Assort.LoyalLevelItems.Remove(rootId);
    }

    // Only track equipment composition. Build names, item IDs, and runtime state
    private static string ComputeBuildSignature(List<Item> items)
    {
        var parts = items
            .Select(item => $"{item.Template}|{item.SlotId ?? string.Empty}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return string.Join(";", parts);
    }

    private void NormalizeDurability(Item item)
    {
        if (item.Upd?.Repairable is null)
        {
            return;
        }

        var maxDurability = itemHelper.GetItem(item.Template).Value?.Properties?.MaxDurability;
        if (maxDurability is null or <= 0)
        {
            return;
        }

        item.Upd.Repairable.MaxDurability = maxDurability;
        item.Upd.Repairable.Durability = maxDurability;
    }
}
