using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
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

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.sp.bela.presettrader";
    public string Name { get; init; } = "PresetTrader";
    public string Author { get; init; } = "Bela";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.2");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/925316/SPTarkov.PresetTrader";
    public string License { get; init; } = "AGPL-3.0";
    public bool HasPrepatcher { get; init; }
}

public class MatchedSet
{
    public string? RootTpl { get; set; }
    public List<AttachmentRef>? Attachments { get; set; }
}

public class AttachmentRef
{
    public string? Slot { get; set; }
    public string? Tpl { get; set; }
}

public class PresetTraderConfig
{
    public bool EnableWeaponPresets { get; set; }
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class Main(
    ISptLogger<Main> logger,
    ModHelper modHelper,
    ItemHelper itemHelper,
    TradersTable tradersTable,
    PresetTraderRefresher refresher
)
    : IOnLoad
{
    private const string TraderRootParentId = "hideout";

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
        var addedCount = refresher.Refresh();
        var presetCount = PopulatePresets(traderBase.Id, "db/gearPresets.json");
        var armorCount = PopulatePresets(traderBase.Id, "db/armorPresets.json");

        var config = modHelper.GetJsonDataFromFile<PresetTraderConfig>(pathToMod, "db/config.json");
        var weaponPresetCount = 0;
        if (config?.EnableWeaponPresets == true)
        {
            weaponPresetCount = PopulateWeaponPresets(traderBase.Id, "db/weaponPresets.json");
        }
        else
        {
            logger.Info(
                $"[PresetTrader]: Gunsmith Weapon presets disabled in config.json, skipping");
        }

        logger.Success(
            $"[PresetTrader]: Added {addedCount} weapon build(s), {presetCount} gear preset(s), {armorCount} armor preset(s) and {weaponPresetCount} weapon preset(s) to trader {traderBase.Id}");

        return Task.CompletedTask;
    }

    private int PopulatePresets(MongoId traderId, string relativePath)
    {
        if (!tradersTable.TryGetValue(traderId, out var traderData))
        {
            logger.Error($"[PresetTrader]: Trader {traderId} not found, cannot add presets");
            return 0;
        }

        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var sets = modHelper.GetJsonDataFromFile<List<MatchedSet>>(pathToMod, relativePath);

        if (sets is null)
        {
            logger.Warning($"[PresetTrader]: {relativePath} not found or empty, skipping presets");
            return 0;
        }

        var added = 0;

        // Root tpls already for sale (weapon builds and previous preset passes included)
        var soldRootTpls = traderData.Assort.Items
            .Where(item => item.SlotId == TraderRootParentId)
            .Select(item => item.Template)
            .ToHashSet();

        if (sets is { Count: > 0 })
        {
            foreach (var set in sets)
            {
                try
                {
                    if (set is null || string.IsNullOrWhiteSpace(set.RootTpl))
                    {
                        continue;
                    }

                    var rootTemplate = itemHelper.GetItem(set.RootTpl).Value;
                    if (rootTemplate is null)
                    {
                        logger.Warning(
                            $"[PresetTrader]: Skipping preset '{set.RootTpl}' - tpl not found in item DB");
                        continue;
                    }

                    string? invalidAttachmentReason = null;
                    if (set.Attachments is { Count: > 0 })
                    {
                        foreach (var att in set.Attachments)
                        {
                            if (att is null ||
                                string.IsNullOrWhiteSpace(att.Tpl) ||
                                string.IsNullOrWhiteSpace(att.Slot))
                            {
                                invalidAttachmentReason = "malformed attachment entry";
                                break;
                            }

                            if (!itemHelper.GetItem(att.Tpl).Key)
                            {
                                invalidAttachmentReason = $"attachment tpl '{att.Tpl}' not found in item DB";
                                break;
                            }

                            if (rootTemplate.Properties?.Slots?.Any(slot => slot.Name == att.Slot) != true)
                            {
                                invalidAttachmentReason = $"slot '{att.Slot}' is not declared on root template '{set.RootTpl}'";
                                break;
                            }
                        }
                    }

                    if (invalidAttachmentReason is not null)
                    {
                        logger.Warning(
                            $"[PresetTrader]: Skipping preset '{set.RootTpl}' - {invalidAttachmentReason}");
                        continue;
                    }

                    if (!soldRootTpls.Add(set.RootTpl))
                    {
                        logger.Warning(
                            $"[PresetTrader]: Skipping preset '{set.RootTpl}' - already sold by another preset or weapon build");
                        continue;
                    }

                    var rootId = new MongoId();
                    var items = new List<Item>
                    {
                        new()
                        {
                            Id = rootId,
                            Template = set.RootTpl,
                            ParentId = TraderRootParentId,
                            SlotId = TraderRootParentId,
                            Upd = new Upd
                            {
                                StackObjectsCount = 999,
                                UnlimitedCount = true,
                                BuyRestrictionCurrent = 0
                            }
                        }
                    };

                    if (set.Attachments is { Count: > 0 })
                    {
                        foreach (var att in set.Attachments)
                        {
                            if (att is null ||
                                string.IsNullOrWhiteSpace(att.Tpl) ||
                                string.IsNullOrWhiteSpace(att.Slot))
                            {
                                continue;
                            }

                            items.Add(new Item
                            {
                                Id = new MongoId(),
                                Template = att.Tpl,
                                ParentId = rootId,
                                SlotId = att.Slot,
                                Upd = new Upd()
                            });
                        }
                    }

                    var price = (int)items.Select(x => x.Template).Sum(itemHelper.GetItemMaxPrice);

                    if (price <= 0)
                    {
                        logger.Warning(
                            $"[PresetTrader]: Skipping preset '{set.RootTpl}' - total price is 0");
                        continue;
                    }

                    traderData.Assort.Items.AddRange(items);

                    traderData.Assort.BarterScheme[rootId] =
                    [
                        [
                            new BarterScheme
                            {
                                Count = price,
                                Template = Money.ROUBLES
                            }
                        ]
                    ];

                    traderData.Assort.LoyalLevelItems[rootId] = 1;

                    added++;

                    logger.Debug(
                        $"[PresetTrader]: Added preset '{set.RootTpl}' for {price} roubles");
                }
                catch (Exception ex)
                {
                    logger.Error(
                        $"[PresetTrader]: Failed to process preset '{set?.RootTpl}': {ex}");
                }
            }
        }

        return added;
    }

    private int PopulateWeaponPresets(MongoId traderId, string relativePath)
    {
        if (!tradersTable.TryGetValue(traderId, out var traderData))
        {
            logger.Error($"[PresetTrader]: Trader {traderId} not found, cannot add weapon presets");
            return 0;
        }

        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var presets = modHelper.GetJsonDataFromFile<Dictionary<string, List<Item>>>(pathToMod, relativePath);

        if (presets is null)
        {
            logger.Warning($"[PresetTrader]: {relativePath} not found or empty, skipping weapon presets");
            return 0;
        }

        var added = 0;

        foreach (var (name, presetItems) in presets)
        {
            try
            {
                if (presetItems is not { Count: > 0 })
                {
                    continue;
                }

                var rootId = presetItems.RemapRootItemId();
                var root = presetItems.FirstOrDefault(item => item.Id == rootId);
                if (root is null)
                {
                    logger.Warning(
                        $"[PresetTrader]: Skipping weapon preset '{name}' - root item not found");
                    continue;
                }

                // Every child must point at a parent inside the tree whose template declares the slot
                var itemsById = presetItems.ToDictionary(item => item.Id.ToString());
                string? invalidReason = null;
                foreach (var child in presetItems)
                {
                    // Root gets reparented to the trader root below; some sources pre-tag it "hideout"
                    if (child.Id == rootId)
                    {
                        continue;
                    }

                    if (child.ParentId is null)
                    {
                        continue;
                    }

                    if (!itemsById.TryGetValue(child.ParentId, out var parent))
                    {
                        invalidReason = $"parent '{child.ParentId}' not found in preset tree";
                        break;
                    }

                    if (!itemHelper.GetItem(child.Template).Key)
                    {
                        invalidReason = $"attachment tpl '{child.Template}' not found in item DB";
                        break;
                    }

                    var parentProps = itemHelper.GetItem(parent.Template).Value?.Properties;
                    var slotDeclared = parentProps?.Slots?.Any(slot => slot.Name == child.SlotId) == true
                        || parentProps?.Cartridges?.Any(slot => slot.Name == child.SlotId) == true;
                    if (!slotDeclared)
                    {
                        invalidReason = $"slot '{child.SlotId}' is not declared on parent template '{parent.Template}'";
                        break;
                    }
                }

                if (invalidReason is not null)
                {
                    logger.Warning(
                        $"[PresetTrader]: Skipping weapon preset '{name}' - {invalidReason}");
                    continue;
                }

                root.ParentId = TraderRootParentId;
                root.SlotId = TraderRootParentId;
                root.Upd ??= new Upd();
                root.Upd.StackObjectsCount = 999;
                root.Upd.UnlimitedCount = true;
                root.Upd.BuyRestrictionCurrent = 0;

                var price = (int)presetItems.Select(item => item.Template).Sum(itemHelper.GetItemMaxPrice);
                if (price <= 0)
                {
                    logger.Warning(
                        $"[PresetTrader]: Skipping weapon preset '{name}' - total price is 0");
                    continue;
                }

                traderData.Assort.Items.AddRange(presetItems);

                traderData.Assort.BarterScheme[rootId] =
                [
                    [
                        new BarterScheme
                        {
                            Count = price,
                            Template = Money.ROUBLES
                        }
                    ]
                ];

                traderData.Assort.LoyalLevelItems[rootId] = 1;

                added++;

                logger.Debug(
                    $"[PresetTrader]: Added weapon preset '{name}' ({root.Template}) for {price} roubles");
            }
            catch (Exception ex)
            {
                logger.Error(
                    $"[PresetTrader]: Failed to process weapon preset '{name}': {ex}");
            }
        }

        return added;
    }
}

[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 1)]
public class PresetTraderRegistration(
    ISptLogger<PresetTraderRegistration> logger,
    ModHelper modHelper,
    ImageRouter imageRouter,
    TraderConfig traderConfig,
    TimeUtil timeUtil,
    ICloner cloner,
    TradersTable tradersTable,
    LocaleTable localeTable
)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, "db/base.json");
        if (traderBase is null)
        {
            logger.Error("[PresetTrader]: db/base.json not found, aborting trader registration");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(traderBase.Avatar))
        {
            logger.Error("[PresetTrader]: Trader avatar is missing, aborting trader registration");
            return Task.CompletedTask;
        }

        var traderImagePath = Path.Combine(pathToMod, "db", "headshot.jpg");
        if (!File.Exists(traderImagePath))
        {
            logger.Error($"[PresetTrader]: Trader image not found: {traderImagePath}");
            return Task.CompletedTask;
        }

        var traderAvatar = Path.ChangeExtension(traderBase.Avatar, null);
        imageRouter.AddRoute(traderAvatar, traderImagePath);

        if (traderConfig.UpdateTime.All(x => x.TraderId != traderBase.Id))
        {
            traderConfig.UpdateTime.Add(new UpdateTime
            {
                TraderId = traderBase.Id,
                Seconds = new MinMax<int>(
                    timeUtil.GetHoursAsSeconds(1),
                    timeUtil.GetHoursAsSeconds(2))
            });
        }

        if (!AddTraderWithEmptyAssortToDb(traderBase))
        {
            return Task.CompletedTask;
        }

        AddTraderToLocales(
            traderBase,
            "PresetTrader",
            "Sells weapon presets built and saved in your stash.");

        logger.Success(
            $"[PresetTrader]: Registered trader {traderBase.Id} ahead of profile validation");

        return Task.CompletedTask;
    }

    private bool AddTraderWithEmptyAssortToDb(TraderBase traderDetails)
    {
        var traderData = new Trader
        {
            Assort = new TraderAssort
            {
                Items = [],
                BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
                LoyalLevelItems = new Dictionary<MongoId, int>()
            },
            Base = cloner.Clone(traderDetails)!,
            QuestAssort = new()
            {
                { "Started", new() },
                { "Success", new() },
                { "Fail", new() }
            },
            Dialogue = []
        };

        if (!tradersTable.TryAdd(traderDetails.Id, traderData))
        {
            logger.Error(
                $"[PresetTrader]: Failed to add trader {traderDetails.Id}, id already exists; aborting");

            return false;
        }

        return true;
    }

    private void AddTraderToLocales(
        TraderBase baseJson,
        string firstName,
        string description)
    {
        var newTraderId = baseJson.Id;
        var fullName = baseJson.Name;
        var nickName = baseJson.Nickname;
        var location = baseJson.Location;

        foreach (var (_, localeKvP) in localeTable.Global)
        {
            localeKvP.AddTransformer(localeData =>
            {
                if (localeData is null)
                {
                    return localeData;
                }

                localeData[$"{newTraderId} FullName"] = fullName;
                localeData[$"{newTraderId} FirstName"] = firstName;
                localeData[$"{newTraderId} Nickname"] = nickName ?? fullName;
                localeData[$"{newTraderId} Location"] = location ?? string.Empty;
                localeData[$"{newTraderId} Description"] = description;

                return localeData;
            });
        }
    }
}

[Injectable(InjectionType.Singleton, TypePriority = OnUpdateOrder.InsuranceCallbacks)]
public class PresetTraderRefresher(
    ISptLogger<PresetTraderRefresher> logger,
    ProfileHelper profileHelper,
    TradersTable tradersTable,
    ItemHelper itemHelper,
    ICloner cloner)
    : IOnUpdate
{
    private const string TraderRootParentId = "hideout";
    private const int RefreshIntervalSeconds = 30;

    private MongoId? _traderId;
    private readonly Dictionary<MongoId, MongoId> _buildRootIds = new();
    private readonly Dictionary<MongoId, string> _buildSignatures = new();

    public void SetTraderId(MongoId traderId)
    {
        _traderId = traderId;
    }

    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        if (_traderId is null)
        {
            return Task.FromResult(true);
        }

        var traderId = _traderId.Value;

        if (secondsSinceLastRun < RefreshIntervalSeconds)
        {
            return Task.FromResult(false);
        }

        var synced = Refresh();
        if (synced > 0)
        {
            logger.Info(
                $"[PresetTrader]: Live refresh synced {synced} weapon build(s) to trader {traderId}");
        }

        return Task.FromResult(true);
    }

    public int Refresh()
    {
        if (_traderId is null)
        {
            return 0;
        }

        var traderId = _traderId.Value;

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
                    logger.Debug(
                        $"[PresetTrader]: Replacing modified build '{build.Name}' ({build.Id})");
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

                    logger.Debug(
                        $"[PresetTrader]: Added build '{build.Name}' ({build.Id}) for {price} roubles");

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
                logger.Debug(
                    $"[PresetTrader]: Removed deleted build ({kvp.Key}) from trader");
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

    private static string ComputeBuildSignature(List<Item> items)
    {
        var parts = items
            .Select(item => $"{item.Template}|{item.SlotId ?? string.Empty}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return string.Join(";", parts);
    }
}
