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
    /// <summary>
    /// Sells key packs as empty containers: the client refuses to buy containers
    /// that already hold items, so the shelf shows only the SICC root while the
    /// price covers the full pack. The template tree is registered in
    /// _keyPackByRootId so TryInjectKeyPacks can deliver the contents after a
    /// successful purchase.
    /// </summary>
    private int PopulateKeyPacks(Trader traderData, string relativePath)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var presets = modHelper.GetJsonDataFromFile<Dictionary<string, List<Item>>>(pathToMod, relativePath);

        _keyPackByRootId.Clear();

        if (presets is null)
        {
            logger.Warning($"[PresetTrader]: {relativePath} not found or empty, skipping key packs");
            return 0;
        }

        var added = 0;

        foreach (var (name, packItems) in presets)
        {
            try
            {
                if (packItems is not { Count: > 0 })
                {
                    continue;
                }

                var templateRoot = packItems.FirstOrDefault(item =>
                    item.ParentId == TraderRootParentId || item.ParentId is null);
                if (templateRoot is null)
                {
                    logger.Warning($"[PresetTrader]: Skipping key pack '{name}' - root item not found");
                    continue;
                }

                if (!itemHelper.GetItem(templateRoot.Template).Key)
                {
                    logger.Warning(
                        $"[PresetTrader]: Skipping key pack '{name}' - root tpl {templateRoot.Template} not found in item DB");
                    continue;
                }

                var rootProps = itemHelper.GetItem(templateRoot.Template).Value?.Properties;
                string? invalidReason = null;
                foreach (var child in packItems)
                {
                    if (child.Id == templateRoot.Id)
                    {
                        continue;
                    }

                    if (child.ParentId != templateRoot.Id.ToString())
                    {
                        invalidReason = $"child '{child.Id}' is not attached to the pack root";
                        break;
                    }

                    if (!itemHelper.GetItem(child.Template).Key)
                    {
                        invalidReason = $"child tpl '{child.Template}' not found in item DB";
                        break;
                    }

                    var slotDeclared = rootProps?.Slots?.Any(slot => slot.Name == child.SlotId) == true
                        || rootProps?.Cartridges?.Any(slot => slot.Name == child.SlotId) == true
                        || rootProps?.Grids?.Any(grid => grid.Name == child.SlotId) == true;
                    if (!slotDeclared)
                    {
                        invalidReason =
                            $"slot '{child.SlotId}' is not declared on root template '{templateRoot.Template}'";
                        break;
                    }
                }

                if (invalidReason is not null)
                {
                    logger.Warning($"[PresetTrader]: Skipping key pack '{name}' - {invalidReason}");
                    continue;
                }

                var price = (int)packItems.Select(item => item.Template).Sum(itemHelper.GetItemMaxPrice);
                if (price <= 0)
                {
                    logger.Warning($"[PresetTrader]: Skipping key pack '{name}' - total price is 0");
                    continue;
                }

                // Fresh root id: the seven packs share the same SICC template,
                // and template ids must never leak into the live assort.
                var rootId = new MongoId();
                var shelfRoot = new Item
                {
                    Id = rootId,
                    Template = templateRoot.Template,
                    ParentId = TraderRootParentId,
                    SlotId = TraderRootParentId,
                    Upd = new Upd
                    {
                        StackObjectsCount = 999,
                        UnlimitedCount = true,
                        BuyRestrictionCurrent = 0,
                        SpawnedInSession = templateRoot.Upd?.SpawnedInSession,
                        Tag = templateRoot.Upd?.Tag is { } tag
                            ? new UpdTag { Color = tag.Color, Name = tag.Name }
                            : null
                    }
                };

                traderData.Assort.Items.Add(shelfRoot);

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

                _keyPackByRootId[rootId] = packItems;
                added++;
            }
            catch (Exception ex)
            {
                logger.Error($"[PresetTrader]: Failed to process key pack '{name}': {ex}");
            }
        }

        return added;
    }

    /// <summary>
    /// After a key pack purchase, spawn the pack's keys into the bought
    /// container: profile inventory plus the response envelope's `new` array.
    /// Returns the original output untouched on any failure.
    /// </summary>
    public string TryInjectKeyPacks(MongoId sessionId, ItemEventRouterRequest info, string? output)
    {
        if (string.IsNullOrEmpty(output) || info.Data is not { Count: > 0 })
        {
            return output ?? string.Empty;
        }

        try
        {
            var traderId = GetTraderIdString();
            if (string.IsNullOrEmpty(traderId))
            {
                return output;
            }

            var purchases = new List<(List<Item> Template, int Count)>();

            lock (_assortGate)
            {
                foreach (var evt in info.Data)
                {
                    if (evt.ValueKind != JsonValueKind.Object
                        || !evt.TryGetProperty("Action", out var actionEl)
                        || actionEl.GetString() != ItemEventActions.TRADING_CONFIRM
                        || !evt.TryGetProperty("type", out var typeEl)
                        || typeEl.GetString() != "buy_from_trader"
                        || !evt.TryGetProperty("tid", out var tidEl)
                        || tidEl.GetString() != traderId
                        || !evt.TryGetProperty("item_id", out var idEl)
                        || idEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var itemIdStr = idEl.GetString();
                    if (itemIdStr is null)
                    {
                        continue;
                    }

                    List<Item>? template = null;
                    foreach (var kvp in _keyPackByRootId)
                    {
                        if (kvp.Key.ToString() == itemIdStr)
                        {
                            template = kvp.Value;
                            break;
                        }
                    }

                    if (template is null)
                    {
                        continue;
                    }

                    var count = 1;
                    if (evt.TryGetProperty("count", out var countEl)
                        && countEl.ValueKind == JsonValueKind.Number
                        && countEl.TryGetInt32(out var parsedCount)
                        && parsedCount > 0)
                    {
                        count = parsedCount;
                    }

                    purchases.Add((template, count));
                }
            }

            if (purchases.Count == 0)
            {
                return output;
            }

            if (JsonNode.Parse(output) is not JsonObject envelope)
            {
                return output;
            }

            // Numeric non-zero err means the vanilla trade failed server-side.
            if (envelope["err"] is JsonValue errValue
                && errValue.TryGetValue<int>(out var errCode)
                && errCode != 0)
            {
                return output;
            }

            if (envelope["data"] is not JsonObject dataNode
                || dataNode["profileChanges"] is not JsonObject changesNode)
            {
                return output;
            }

            var profileChange = changesNode[sessionId.ToString()] as JsonObject
                ?? (changesNode.Count == 1 ? changesNode.First().Value as JsonObject : null);
            if (profileChange?["items"] is not JsonObject itemsNode
                || itemsNode["new"] is not JsonArray newArray)
            {
                return output;
            }

            if (purchases.Count == 0)
            {
                return output;
            }

            // Pair purchased packs with the container instances vanilla just
            // created, preserving event order (events process sequentially, so
            // the new[] array mirrors request order).
            var pending = purchases.Select(p => (p.Template, p.Count)).ToList();
            var deliveries = new List<(JsonObject Instance, List<Item> Template, Item TemplateRoot)>();
            var pendingIndex = 0;

            foreach (var node in newArray)
            {
                if (pendingIndex >= pending.Count)
                {
                    break;
                }

                if (node is not JsonObject instance
                    || instance["_tpl"] is not JsonValue tplValue
                    || !tplValue.TryGetValue<string>(out var tpl))
                {
                    continue;
                }

                var (template, remaining) = pending[pendingIndex];
                var templateRoot = template.FirstOrDefault(item =>
                    item.ParentId == TraderRootParentId || item.ParentId is null);
                if (templateRoot is null || tpl != templateRoot.Template.ToString())
                {
                    continue;
                }

                deliveries.Add((instance, template, templateRoot));
                pending[pendingIndex] = (template, remaining - 1);
                if (remaining <= 1)
                {
                    pendingIndex++;
                }
            }

            if (deliveries.Count == 0)
            {
                return output;
            }

            var expectedUnits = purchases.Sum(p => p.Count);
            if (deliveries.Count < expectedUnits)
            {
                logger.Warning(
                    $"[PresetTrader]: Matched {deliveries.Count}/{expectedUnits} purchased key pack instance(s), injecting only the matched ones");
            }

            // Build and serialize every child BEFORE touching profile or envelope.
            var newChildren = new List<Item>();
            var childNodes = new List<JsonNode>();

            foreach (var (instance, template, templateRoot) in deliveries)
            {
                if (instance["_id"] is not JsonValue idValue
                    || !idValue.TryGetValue<string>(out var containerId))
                {
                    logger.Error("[PresetTrader]: Key pack instance in response has no _id, aborting injection");
                    return output;
                }

                // Mirror the container's found-in-raid state onto its keys.
                bool? spawnedInSession = null;
                if (instance["upd"] is JsonObject instanceUpd
                    && instanceUpd["SpawnedInSession"] is JsonValue spawnedValue
                    && spawnedValue.TryGetValue<bool>(out var spawned))
                {
                    spawnedInSession = spawned;
                }

                foreach (var child in template)
                {
                    if (child.Id == templateRoot.Id)
                    {
                        continue;
                    }

                    var childUpd = child.Upd is null
                        ? new Upd()
                        : cloner.Clone(child.Upd) ?? new Upd();
                    childUpd.SpawnedInSession = spawnedInSession ?? childUpd.SpawnedInSession;

                    var newChild = new Item
                    {
                        Id = new MongoId(),
                        Template = child.Template,
                        ParentId = containerId,
                        SlotId = child.SlotId,
                        Location = child.Location,
                        Upd = childUpd
                    };

                    var childJson = jsonUtil.Serialize(newChild);
                    if (childJson is null || JsonNode.Parse(childJson) is not JsonObject childNode)
                    {
                        logger.Error("[PresetTrader]: Failed to serialize an injected key item, aborting");
                        return output;
                    }

                    newChildren.Add(newChild);
                    childNodes.Add(childNode);
                }
            }

            if (newChildren.Count == 0)
            {
                return output;
            }

            foreach (var childNode in childNodes)
            {
                newArray.Add(childNode);
            }

            // Round-trip with default options so no parsed entry (e.g. a null
            // errmsg) is dropped by jsonUtil's WhenWritingNull policy.
            var rewritten = envelope.ToJsonString();
            profileHelper.GetPmcProfile(sessionId)?.Inventory?.Items?.AddRange(newChildren);
            return rewritten;
        }
        catch (Exception ex)
        {
            logger.Error($"[PresetTrader]: Failed to inject purchased key pack: {ex}");
            return output ?? string.Empty;
        }
    }
}
