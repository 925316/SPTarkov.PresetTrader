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
    public bool HasPrepatcher { get; init; } = false;
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class Main(
    ISptLogger<Main> logger,
    ModHelper modHelper,
    ImageRouter imageRouter,
    TraderConfig traderConfig,
    RagfairConfig ragfairConfig,
    TimeUtil timeUtil,
    ICloner cloner,
    ProfileHelper profileHelper,
    ItemHelper itemHelper,
    TradersTable tradersTable,
    LocaleTable localeTable
    )
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        // Read trader base settings (id, name, loyalty levels, etc)
        var traderBase = modHelper.GetJsonDataFromFile<TraderBase>(pathToMod, "db/base.json");
        if (traderBase is null)
        {
            logger.Error("[PresetTrader]: db/base.json not found, aborting trader registration");
            return Task.CompletedTask;
        }

        // Register trader avatar + stock refresh time (1-2 hours)
        var traderImagePath = Path.Combine(pathToMod, "db/headshot.jpg");
        imageRouter.AddRoute(traderBase.Avatar!.Replace(".jpg", ""), traderImagePath);
        AddTraderUpdateTime(traderConfig, traderBase, timeUtil.GetHoursAsSeconds(1), timeUtil.GetHoursAsSeconds(2));

        // Make trader visible on the flea market
        ragfairConfig.Traders.TryAdd(traderBase.Id, true);

        // Add trader with empty assort to the server database
        AddTraderWithEmptyAssortToDb(traderBase);

        // Add localisation text so the trader shows up in every language
        AddTraderToLocales(traderBase, "PresetTrader", "Sells weapon presets built and saved in your stash.");

        // Populate the assort with every weapon build saved in player profiles
        var addedCount = PopulateWeaponBuilds(traderBase.Id);

        logger.Success($"[PresetTrader]: Added {addedCount} weapon build(s) to trader {traderBase.Id}");

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Iterate every profile on the server, collect all saved weapon builds, clone + remap their
    ///     item ids, price them from the handbook and write them into the trader assort.
    /// </summary>
    /// <param name="traderId">Trader id to write assorts into</param>
    /// <returns>Number of weapon builds added</returns>
    protected int PopulateWeaponBuilds(MongoId traderId)
    {
        var traderData = tradersTable[traderId];
        var seenBuildNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;

        foreach (var (profileId, profile) in profileHelper.GetProfiles())
        {
            var weaponBuilds = profile?.UserBuildData?.WeaponBuilds;
            if (weaponBuilds is null || weaponBuilds.Count == 0)
            {
                continue;
            }

            foreach (var build in weaponBuilds)
            {
                // Skip builds with no items, and skip duplicate names across profiles
                if (build?.Items is null || build.Items.Count == 0)
                {
                    continue;
                }

                if (!seenBuildNames.Add(build.Name ?? build.Id.ToString()))
                {
                    continue;
                }

                // Clone before mutating, otherwise we alter the players profile data
                var itemList = cloner.Clone(build.Items)!;

                // Generate a fresh unique id for the root item + reparent its direct children
                var newRootId = itemList.RemapRootItemId();

                // Assort root items must point at the hideout (like a trader selling an item)
                var rootItem = itemList.FirstOrDefault(item => item.Id == newRootId);
                if (rootItem is null)
                {
                    continue;
                }

                rootItem.ParentId = "hideout";
                rootItem.SlotId = "hideout";
                rootItem.Upd ??= new Upd();
                rootItem.Upd.StackObjectsCount ??= 100;

                // Price the whole build (all items + attachments) from the handbook
                var price = (int)itemHelper.GetItemAndChildrenPrice(itemList.Select(item => item.Template));
                if (price <= 0)
                {
                    logger.Warning($"[PresetTrader]: Skipping build '{build.Name}' ({build.Id}) - total price is 0");
                    continue;
                }

                // Write items + barter scheme (rouble price) + loyalty level (always 1) into assort
                traderData.Assort.Items.AddRange(itemList);
                traderData.Assort.BarterScheme[newRootId] =
                [
                    [new BarterScheme { Count = price, Template = Money.ROUBLES }]
                ];
                traderData.Assort.LoyalLevelItems[newRootId] = 1;

                logger.Debug($"[PresetTrader]: Added build '{build.Name}' ({build.Id}) for {price} roubles");
                addedCount++;
            }
        }

        return addedCount;
    }

    /// <summary>
    ///     Add the traders update time for when their offers refresh
    /// </summary>
    protected void AddTraderUpdateTime(TraderConfig traderConfig, TraderBase baseJson, int refreshTimeSecondsMin, int refreshTimeSecondsMax)
    {
        var traderRefreshRecord = new UpdateTime
        {
            TraderId = baseJson.Id,
            Seconds = new MinMax<int>(refreshTimeSecondsMin, refreshTimeSecondsMax)
        };

        traderConfig.UpdateTime.Add(traderRefreshRecord);
    }

    /// <summary>
    ///     Add a traders base data to the server, no assort items
    /// </summary>
    protected void AddTraderWithEmptyAssortToDb(TraderBase traderDetailsToAdd)
    {
        var emptyTraderItemAssortObject = new TraderAssort
        {
            Items = [],
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
            LoyalLevelItems = new Dictionary<MongoId, int>()
        };

        var traderDataToAdd = new Trader
        {
            Assort = emptyTraderItemAssortObject,
            Base = cloner.Clone(traderDetailsToAdd)!,
            QuestAssort = new()
            {
                { "Started", new() },
                { "Success", new() },
                { "Fail", new() }
            },
            Dialogue = []
        };

        if (!tradersTable.TryAdd(traderDetailsToAdd.Id, traderDataToAdd))
        {
            logger.Error($"[PresetTrader]: Failed to add trader {traderDetailsToAdd.Id}, id already exists");
        }
    }

    /// <summary>
    ///     Add traders name/location/description to all locales
    /// </summary>
    protected void AddTraderToLocales(TraderBase baseJson, string firstName, string description)
    {
        var locales = localeTable.Global;
        var newTraderId = baseJson.Id;
        var fullName = baseJson.Name;
        var nickName = baseJson.Nickname;
        var location = baseJson.Location;

        foreach (var (localeKey, localeKvP) in locales)
        {
            localeKvP.AddTransformer(lazyloadedLocaleData =>
            {
                lazyloadedLocaleData!.Add($"{newTraderId} FullName", fullName);
                lazyloadedLocaleData.Add($"{newTraderId} FirstName", firstName);
                lazyloadedLocaleData.Add($"{newTraderId} Nickname", nickName ?? fullName);
                lazyloadedLocaleData.Add($"{newTraderId} Location", location ?? "");
                lazyloadedLocaleData.Add($"{newTraderId} Description", description);
                return lazyloadedLocaleData;
            });
        }
    }
}
