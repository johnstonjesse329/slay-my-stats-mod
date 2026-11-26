using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>Correlates effects with the enclosing card or potion action. The action
/// provides context. An explicit source or modifier supplied by the game is separate.</summary>
internal static class SupportTrace
{
    private static int _nextAction;
    private static int _nextObject;
    private sealed record CaptureId(int Value);
    private static readonly ConditionalWeakTable<object, CaptureId> Ids = new();
    internal static readonly AsyncLocal<CardModel?> PayingFor = new();

    internal static int? CurrentAction => EventLog.CurrentAction.Value;
    internal static JsonNode? ObjectId(object? value) => value is null ? null
        : JsonValue.Create(Ids.GetValue(value, _ => new CaptureId(Interlocked.Increment(ref _nextObject))).Value);

    internal static void Begin(string kind, AbstractModel source, Player player, Creature? target)
    {
        int? parent = CurrentAction;
        int action = Interlocked.Increment(ref _nextAction);
        EventLog.Add("action0", new JsonObject
        {
            ["id"] = action,
            ["parent"] = JsonValue.Create(parent),
            ["kind"] = kind,
            ["model"] = source.Id.ToString(),
            ["sourceSlot"] = EventHooks.Slot(player.Creature),
            ["targetSlot"] = EventHooks.Slot(target),
            ["targetId"] = JsonValue.Create(target?.CombatId)
        });
        EventLog.CurrentAction.Value = action;
    }

    internal static void Restore(int? parent) => EventLog.CurrentAction.Value = parent;

    internal static JsonArray Modifiers(IEnumerable<AbstractModel> models)
    {
        JsonArray rows = [];
        foreach (AbstractModel model in models)
        {
            Creature? source = model switch
            {
                PowerModel power => power.Applier,
                RelicModel relic => relic.Owner.Creature,
                CardModel card => card.Owner.Creature,
                _ => null
            };
            rows.Add(new JsonObject { ["model"] = model.Id.ToString(), ["sourceSlot"] = EventHooks.Slot(source) });
        }
        return rows;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
internal static class SupportCardAction
{
    static void Prefix(CardModel __instance, Creature? target, out int? __state)
    {
        __state = SupportTrace.CurrentAction;
        EventHooks.Safe(() => SupportTrace.Begin("card", __instance, __instance.Owner, target));
    }

    static void Finalizer(int? __state) => SupportTrace.Restore(__state);
}

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
internal static class SupportPotionAction
{
    static void Prefix(PotionModel __instance, Creature? target, out int? __state)
    {
        __state = SupportTrace.CurrentAction;
        EventHooks.Safe(() => SupportTrace.Begin("potion", __instance, __instance.Owner, target));
    }

    static void Finalizer(int? __state) => SupportTrace.Restore(__state);
}

[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.GainEnergy))]
internal static class SupportEnergyGained
{
    static void Prefix(PlayerCombatState __instance, out int __state) => __state = __instance.Energy;

    static void Postfix(PlayerCombatState __instance, Player ____player, int __state) => EventHooks.Safe(() =>
    {
        int gained = __instance.Energy - __state;
        if (gained <= 0) return;
        EventLog.Add("energy+", new JsonObject
        {
            ["targetSlot"] = EventHooks.Slot(____player.Creature),
            ["n"] = gained,
            ["playerTurn"] = __instance.TurnNumber,
            ["energy"] = __instance.Energy
        });
    });
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
internal static class SupportDamageCalculation
{
    static void Postfix(Creature? target, Creature? dealer, decimal damage, CardModel? cardSource,
        CardPreviewMode previewMode, IEnumerable<AbstractModel> modifiers, decimal __result) => EventHooks.Safe(() =>
    {
        if (previewMode != CardPreviewMode.None || target is null || damage == __result) return;
        EventLog.Add("damageCalc", new JsonObject
        {
            ["sourceSlot"] = EventHooks.Slot(dealer),
            ["targetSlot"] = EventHooks.Slot(target),
            ["targetId"] = JsonValue.Create(target.CombatId),
            ["card"] = cardSource?.Id.ToString(),
            ["before"] = damage,
            ["after"] = __result,
            ["modifiers"] = SupportTrace.Modifiers(modifiers)
        });
    });
}

[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.DamageReceived))]
internal static class SupportDamageResolved
{
    static void Postfix(Creature receiver, Creature? dealer, DamageResult result, CardModel? cardSource) =>
        EventHooks.Safe(() => EventLog.Add("damageResolved", new JsonObject
        {
            ["damageId"] = SupportTrace.ObjectId(result),
            ["sourceSlot"] = EventHooks.Slot(dealer),
            ["petOwnerSlot"] = EventHooks.Slot(dealer?.PetOwner?.Creature),
            ["targetSlot"] = EventHooks.Slot(receiver),
            ["originalTargetSlot"] = EventHooks.Slot(result.OriginalTarget),
            ["targetId"] = JsonValue.Create(receiver.CombatId),
            ["originalTargetId"] = JsonValue.Create(result.OriginalTarget.CombatId),
            ["card"] = cardSource?.Id.ToString(),
            ["blocked"] = result.BlockedDamage,
            ["intendedHpLoss"] = result.IntendedHpLoss,
            ["hpLost"] = result.UnblockedDamage,
            ["overkill"] = result.OverkillDamage
        }));
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
internal static class SupportCardPayment
{
    static void Prefix(CardModel __instance, out CardModel? __state)
    {
        __state = SupportTrace.PayingFor.Value;
        SupportTrace.PayingFor.Value = __instance;
    }

    static void Finalizer(CardModel? __state) => SupportTrace.PayingFor.Value = __state;
}

[HarmonyPatch(typeof(EnsemblePower), nameof(EnsemblePower.TryModifyEnergyCostInCombatLate))]
internal static class SupportEnsembleDiscount
{
    static void Postfix(EnsemblePower __instance, CardModel card, decimal originalCost,
        decimal modifiedCost, bool __result) => EventHooks.Safe(() =>
    {
        if (!__result || originalCost <= modifiedCost || !ReferenceEquals(SupportTrace.PayingFor.Value, card)) return;
        EventLog.Add("costReduction", new JsonObject
        {
            ["power"] = __instance.Id.ToString(),
            ["sourceSlot"] = EventHooks.Slot(__instance.Owner),
            ["targetSlot"] = EventHooks.Slot(card.Owner.Creature),
            ["card"] = card.Id.ToString(),
            ["instance"] = SupportTrace.ObjectId(card),
            ["before"] = originalCost,
            ["after"] = modifiedCost
        });
    });
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
internal static class SupportExtraPlays
{
    static void Postfix(CardModel card, int playCount, Creature? target, List<AbstractModel> modifyingModels,
        int __result) => EventHooks.Safe(() =>
    {
        if (SupportTrace.CurrentAction is null || __result == playCount) return;
        EventLog.Add("playCount", new JsonObject
        {
            ["targetSlot"] = EventHooks.Slot(card.Owner.Creature),
            ["targetId"] = JsonValue.Create(target?.CombatId),
            ["card"] = card.Id.ToString(),
            ["before"] = playCount,
            ["after"] = __result,
            ["modifiers"] = SupportTrace.Modifiers(modifyingModels)
        });
    });
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Flash), typeof(IEnumerable<Creature>))]
internal static class SupportRelicFlash
{
    static void Postfix(RelicModel __instance, IEnumerable<Creature> targets) => EventHooks.Safe(() =>
    {
        // Avoid walking a lazy sequence a second time. The owner is still known.
        JsonArray slots = [];
        if (targets is IReadOnlyCollection<Creature> recipients)
            foreach (Creature target in recipients) slots.Add(EventHooks.Slot(target));
        EventLog.Add("relicFlash", new JsonObject
        {
            ["relic"] = __instance.Id.ToString(),
            ["sourceSlot"] = EventHooks.Slot(__instance.Owner.Creature),
            ["targetSlots"] = slots
        });
    });
}
