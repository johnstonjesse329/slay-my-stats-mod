using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace SlayMyStats.SlayMyStatsCode;

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
internal static class SupportHandDraw
{
    static void Postfix(Player player, decimal originalCardCount, IEnumerable<AbstractModel> modifiers,
        decimal __result) => EventHooks.Safe(() =>
    {
        // Each hand draw marks a batch. Only an isolated next-turn draw effect
        // gives the parser an unambiguous baseline for cards actually drawn.
        var effects = modifiers as List<AbstractModel> ?? modifiers.ToList();
        bool hasNextTurnDraw = effects.Any(effect => effect is DrawCardsNextTurnPower);
        EventLog.Add("handDraw", new JsonObject
        {
            ["targetSlot"] = EventHooks.Slot(player.Creature),
            ["before"] = (int)originalCardCount,
            ["after"] = (int)__result,
            ["nextTurnDraw"] = hasNextTurnDraw,
            ["nextTurnOnly"] = hasNextTurnDraw && effects.Count == 1
        });
    });
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GiveToAnotherPlayer))]
internal static class SupportCardTransfer
{
    static void Prefix(CardModel __instance, out Player? __state) => __state = __instance.Owner;

    static void Postfix(CardModel __instance, Player? __state) => EventHooks.Safe(() =>
    {
        if (ReferenceEquals(__state, __instance.Owner)) return;
        var sender = EventHooks.Slot(__state?.Creature);
        var recipient = EventHooks.Slot(__instance.Owner?.Creature);
        if (sender is null || recipient is null) return;
        EventLog.Add("cardTransfer", new JsonObject
        {
            ["card"] = __instance.Id.ToString(),
            ["sourceSlot"] = sender,
            ["targetSlot"] = recipient,
            ["instance"] = SupportTrace.ObjectId(__instance)
        });
    });
}
