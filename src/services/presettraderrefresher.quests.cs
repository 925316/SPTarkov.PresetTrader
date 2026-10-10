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
    private static readonly Dictionary<string, string[]> QuestPresetNames = new(StringComparer.Ordinal)
    {
        ["5ac23c6186f7741247042bad"] = ["Gunsmith 01"],
        ["5ac2426c86f774138762edfe"] = ["Gunsmith 02"],
        ["5ac2428686f77412450b42bf"] = ["Gunsmith 03"],
        ["639872f9decada40426d3447"] = ["Gunsmith 04"],
        ["5ae3267986f7742a413592fe"] = ["Gunsmith 05"],
        ["5ae3270f86f77445ba41d4dd"] = ["Gunsmith 06"],
        ["5ac244eb86f7741356335af1"] = ["Gunsmith 07"],
        ["5ae3277186f7745973054106"] = ["Gunsmith 08"],
        ["639872fa9b4fb827b200d8e5"] = ["Gunsmith 09"],
        ["5ae327c886f7745c7b3f2f3f"] = ["Gunsmith 10"],
        ["639872fc93ae507d5858c3a6"] = ["Gunsmith 11"],
        ["5b47799d86f7746c5d6a5fd8"] = ["Gunsmith 12"],
        ["5ac244c486f77413e12cf945"] = ["Gunsmith 13"],
        ["639872fe8871e1272b10ccf6"] = ["Gunsmith 14"],
        ["5ae3280386f7742a41359364"] = ["Gunsmith 15"],
        ["5ac242ab86f77412464f68b4"] = ["Gunsmith 16"],
        ["5b47749f86f7746c5d6a5fd4"] = ["Gunsmith 17"],
        ["5b477b6f86f7747290681823"] = ["Gunsmith 18"],
        ["639873003693c63d86328f25"] = ["Gunsmith 19"],
        ["5b477f7686f7744d1b23c4d2"] = ["Gunsmith 20"],
        ["63987301e11ec11ff5504036"] = ["Gunsmith 21 p1", "Gunsmith 21 p2"],
        ["5b47825886f77468074618d3"] = ["Gunsmith 22"],
        ["64f83bb69878a0569d6ecfbe"] = ["Gunsmith 23"],
        ["64f83bcdde58fc437700d8fa"] = ["Gunsmith 24"],
        ["64f83bd983cfca080a362c82"] = ["Gunsmith 25"],
        ["66a74c628410476dd65543be"] = ["Gunsmith Special Order"],
        ["676529af9c90953d090882e7"] = ["Gunsmith Friend 01 p1", "Gunsmith Friend 01 p2", "Gunsmith Friend 01 p3"],
        ["5c0bd94186f7747a727f09b2"] = ["Test Drive 1"],
        ["63a5cf262964a7488f5243ce"] = ["Test Drive 2"],
        ["64f5deac39e45b527a7c4232"] = ["Test Drive 3"],
        ["6574e0dedc0d635f633a5805"] = ["Test Drive 4"],
        ["669fa3a40c828825de06d6a1"] = ["Test Drive 5"],
        ["675c1ff1a757ddd00404f0aa"] = ["Test Drive 6"],
        ["666314b0acf8442f8b0531a1"] = ["Hell on Earth"],
        ["666314b2a9290f9e0806cca3"] = ["Hell on Earth"],
    };

    private List<string> GetActiveQuestPresetNames(MongoId sessionId)
    {
        var names = new List<string>();
        var quests = profileHelper.GetPmcProfile(sessionId)?.Quests;
        if (quests is null)
        {
            return names;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var questStatus in quests)
        {
            if (questStatus is null)
            {
                continue;
            }

            if (questStatus.Status != QuestStatusEnum.Started
                && questStatus.Status != QuestStatusEnum.AvailableForFinish)
            {
                continue;
            }

            if (!QuestPresetNames.TryGetValue(questStatus.QId.ToString(), out var presetNames))
            {
                continue;
            }

            foreach (var name in presetNames)
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private void ValidateQuestPresetMapping()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var presets = modHelper.GetJsonDataFromFile<Dictionary<string, List<Item>>>(pathToMod, "db/weaponPresets.json");
        if (presets is null)
        {
            logger.Warning("[PresetTrader]: db/weaponPresets.json not found or empty, skipping quest mapping check");
            return;
        }

        foreach (var (questIdHex, presetNames) in QuestPresetNames)
        {
            if (!templateTable.Quests.TryGetValue(new MongoId(questIdHex), out var quest) || quest is null)
            {
                logger.Warning($"[PresetTrader]: Quest {questIdHex} not found in quest DB, skipping mapping check");
                continue;
            }

            var expectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var finishConditions = quest.Conditions?.AvailableForFinish;
            if (finishConditions is not null)
            {
                foreach (var condition in finishConditions)
                {
                    if (condition is null
                        || !string.Equals(condition.ConditionType, "WeaponAssembly", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var target = condition.Target;
                    if (target is null)
                    {
                        continue;
                    }

                    if (target.IsList && target.List is not null)
                    {
                        foreach (var tpl in target.List)
                        {
                            if (!string.IsNullOrEmpty(tpl))
                            {
                                expectedRoots.Add(tpl);
                            }
                        }
                    }
                    else if (target.IsItem && target.Item is not null)
                    {
                        expectedRoots.Add(target.Item);
                    }
                }
            }

            if (quest.Rewards is not null
                && quest.Rewards.TryGetValue("Started", out var startedRewards)
                && startedRewards is not null)
            {
                foreach (var reward in startedRewards)
                {
                    if (reward is null || reward.Type != RewardType.Item || reward.Items is null)
                    {
                        continue;
                    }

                    foreach (var rewardItem in reward.Items)
                    {
                        if (rewardItem is not null && string.IsNullOrEmpty(rewardItem.ParentId))
                        {
                            expectedRoots.Add(rewardItem.Template.ToString());
                        }
                    }
                }
            }

            foreach (var name in presetNames)
            {
                if (!presets.TryGetValue(name, out var presetItems) || presetItems is not { Count: > 0 })
                {
                    logger.Warning($"[PresetTrader]: Quest {questIdHex} maps to missing preset '{name}'");
                    continue;
                }

                var root = presetItems[0];
                if (expectedRoots.Count > 0 && !expectedRoots.Contains(root.Template.ToString()))
                {
                    logger.Warning(
                        $"[PresetTrader]: Preset '{name}' root {root.Template} does not match quest {questIdHex} targets [{string.Join(",", expectedRoots)}]");
                }
            }
        }
    }
}
