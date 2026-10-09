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
    private int PopulatePresets(Trader traderData, string relativePath)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var sets = modHelper.GetJsonDataFromFile<List<MatchedSet>>(pathToMod, relativePath);

        if (sets is null)
        {
            logger.Warning($"[PresetTrader]: {relativePath} not found or empty, skipping presets");
            return 0;
        }

        var added = 0;

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

    private int PopulateWeaponPresets(Trader traderData, string relativePath)
    {
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

                var itemsById = presetItems.ToDictionary(item => item.Id.ToString());
                string? invalidReason = null;
                foreach (var child in presetItems)
                {
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
                        || parentProps?.Cartridges?.Any(slot => slot.Name == child.SlotId) == true
                        || parentProps?.Grids?.Any(grid => grid.Name == child.SlotId) == true;
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

                foreach (var part in presetItems)
                {
                    part.Upd ??= new Upd();
                    part.Upd.SpawnedInSession = itemHelper.IsOfBaseclass(part.Template, BaseClasses.AMMO)
                        ? null
                        : true;
                    NormalizeDurability(part);
                }

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
