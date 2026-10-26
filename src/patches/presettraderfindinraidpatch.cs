using System.Reflection;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;

namespace PresetTrader;

[Injectable]
public class PresetTraderFindInRaidPatch : AbstractPatch
{
    private static PresetTraderRefresher? _refresher;

    public static void Attach(PresetTraderRefresher refresher)
    {
        _refresher = refresher;
    }

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(TradeHelper), nameof(TradeHelper.BuyItem));
    }

    [PatchPrefix]
    private static void PatchPrefix(ProcessBuyTradeRequestData buyRequestData, ref bool foundInRaid)
    {
        if (buyRequestData.Type != ItemEventActions.BUY_FROM_TRADER)
        {
            return;
        }

        var traderId = _refresher?.GetTraderIdString();
        if (string.IsNullOrEmpty(traderId) || buyRequestData.TransactionId.ToString() != traderId)
        {
            return;
        }

        foundInRaid = true;
    }
}
