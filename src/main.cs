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
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.2");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
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

        logger.Success(
            $"[PresetTrader]: Added {addedCount} weapon build(s), {presetCount} gear preset(s) and {armorCount} armor preset(s) to trader {traderBase.Id}");

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
                                StackObjectsCount = 1,
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

                    var price = (int)itemHelper.GetItemAndChildrenPrice(
                        items.Select(x => x.Template));

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

[Injectable(TypePriority = OnUpdateOrder.InsuranceCallbacks)]
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
    private readonly HashSet<MongoId> _seenBuildIds = new();

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
            return Task.FromResult(true);
        }

        var added = Refresh();
        if (added > 0)
        {
            logger.Info(
                $"[PresetTrader]: Live refresh added {added} new weapon build(s) to trader {traderId}");
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

        var addedCount = 0;

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

                if (_seenBuildIds.Contains(build.Id))
                {
                    continue;
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
                    rootItem.Upd.StackObjectsCount = 1;

                    var price = (int)itemHelper.GetItemAndChildrenPrice(
                        itemList.Select(item => item.Template));

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

                    _seenBuildIds.Add(build.Id);

                    logger.Debug(
                        $"[PresetTrader]: Added build '{build.Name}' ({build.Id}) for {price} roubles");

                    addedCount++;
                }
                catch (Exception ex)
                {
                    logger.Error(
                        $"[PresetTrader]: Failed to process build '{build.Name}' ({build.Id}): {ex}");
                }
            }
        }

        return addedCount;
    }
}
