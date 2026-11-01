using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// The game's own event points, translated into the log.
///
/// Each hook passes its arguments through unchanged: no sums, no judgements, no
/// schema decisions. If an argument is a game object, the identifier needed to look
/// it up later is what gets recorded. Nothing here may disturb the game -- a failure
/// stops the log for the run and is swallowed.
/// </summary>
public static class EventHooks
{
    private static bool _broken;

    public static void Reset() => _broken = false;

    /// <summary>Where the run so far is written after every fight, so a single fight
    /// is enough to check the recording.</summary>
    public static string DebugPath() =>
        Path.Combine(OS.GetUserDataDir(), "SlayMyStats", "telemetry", "live.ndjson");

    private static void Safe(Action body)
    {
        if (_broken) return;
        try
        {
            body();
        }
        catch (Exception e)
        {
            _broken = true;
            EventLog.Fail();
            MainFile.Logger.Error($"Event log off for this run: {e}");
        }
    }

    /// <summary>A creature as the log names it: the player, or the enemy's slot.</summary>
    internal static string Name(Creature? c) =>
        c == null ? "" : c.IsPlayer ? "P" : (string.IsNullOrEmpty(c.SlotName) ? c.Name ?? "" : c.SlotName);

    private static int Hp(ICombatState state)
    {
        int hp = 0;
        foreach (Player p in state.Players)
        {
            if (p.Creature != null) hp += p.Creature.CurrentHp;
        }
        return hp;
    }

    private static int Energy(ICombatState state)
    {
        int e = 0;
        foreach (Player p in state.Players) e += p.PlayerCombatState?.Energy ?? 0;
        return e;
    }

    private static int TurnOf(ICombatState state)
    {
        int turn = 0;
        foreach (Player p in state.Players)
        {
            int t = p.PlayerCombatState?.TurnNumber ?? 0;
            if (t > turn) turn = t;
        }
        EventLog.SetTurn(turn);
        return turn;
    }

    private static string CardId(CardModel? card) => card?.Id.ToString() ?? "";

    // ---- combat ---------------------------------------------------------

    public static void FightStart(IRunState runState, ICombatState state) => Safe(() =>
    {
        EventLog.NewFight();
        EventLog.Add("fight0", new JsonObject
        {
            ["enc"] = state.Encounter?.Id?.ToString() ?? "",
            ["hp"] = Hp(state),
            ["round"] = state.RoundNumber,
            ["deck"] = state.Players.Sum(p => p.PlayerCombatState?.AllCards?.Count() ?? 0)
        });
    });

    public static void FightVictory(ICombatState state) => Safe(() =>
        EventLog.Add("win", new JsonObject { ["hp"] = Hp(state), ["round"] = state.RoundNumber }));

    public static void FightEnd(ICombatState state) => Safe(() =>
        EventLog.Add("fight1", new JsonObject { ["hp"] = Hp(state), ["round"] = state.RoundNumber }));

    // ---- turn structure -------------------------------------------------

    public static void SideTurnStart(ICombatState state, CombatSide side) => Safe(() =>
        EventLog.Add("side0", new JsonObject
        {
            ["side"] = side.ToString(),
            ["round"] = state.RoundNumber,
            ["turn"] = TurnOf(state)
        }));

    public static void PlayerTurnStart(ICombatState state, Player player) => Safe(() =>
        EventLog.Add("turn0", new JsonObject
        {
            ["turn"] = player.PlayerCombatState?.TurnNumber ?? 0,
            ["energy"] = player.PlayerCombatState?.Energy ?? 0,
            ["stars"] = player.PlayerCombatState?.Stars ?? 0,
            ["round"] = state.RoundNumber
        }));

    public static void ExtraTurn(Player player) => Safe(() =>
        EventLog.Add("extra", new JsonObject
        {
            ["turn"] = player.PlayerCombatState?.TurnNumber ?? 0,
            ["energy"] = player.PlayerCombatState?.Energy ?? 0
        }));

    /// <summary>Before the hand exists: the earliest point at which a turn can be said
    /// to have started, and the one the hand draw follows.</summary>
    public static void BeforeHandDraw(ICombatState state, Player player) => Safe(() =>
        EventLog.Add("draw0", new JsonObject
        {
            ["turn"] = player.PlayerCombatState?.TurnNumber ?? 0,
            ["energy"] = player.PlayerCombatState?.Energy ?? 0,
            ["hand"] = player.PlayerCombatState?.Hand?.Cards?.Count ?? 0,
            ["max"] = CardPile.MaxCardsInHand
        }));

    public static void EnergyReset(Player player) => Safe(() =>
    {
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        EventLog.SetTurn(turn);
        EventLog.Add("erese", new JsonObject
        {
            ["turn"] = turn,
            ["energy"] = player.PlayerCombatState?.Energy ?? 0,
            ["max"] = player.PlayerCombatState?.MaxEnergy ?? 0
        });
    });

    public static void EnergySpent(CardModel? card, int amount) => Safe(() =>
        EventLog.Add("eps", new JsonObject { ["n"] = amount, ["card"] = CardId(card) }));

    public static void SideTurnEnd(ICombatState state, CombatSide side) => Safe(() =>
        EventLog.Add("side1", new JsonObject
        {
            ["side"] = side.ToString(),
            ["energy"] = Energy(state),
            ["round"] = state.RoundNumber
        }));

    // ---- cards ----------------------------------------------------------

    public static void CardDrawn(CardModel? card, bool fromHandDraw) => Safe(() =>
        EventLog.Add("draw1", new JsonObject { ["card"] = CardId(card), ["hand"] = fromHandDraw }));

    public static void CardPlayed(CardPlay? play) => Safe(() =>
        EventLog.Add("play1", new JsonObject { ["card"] = CardId(play?.Card) }));

    public static void CardDiscarded(CardModel? card) => Safe(() =>
        EventLog.Add("disc", new JsonObject { ["card"] = CardId(card) }));

    public static void CardExhausted(CardModel? card, bool ethereal) => Safe(() =>
        EventLog.Add("exh", new JsonObject { ["card"] = CardId(card), ["eth"] = ethereal }));

    public static void CardGenerated(CardModel? card) => Safe(() =>
        EventLog.Add("gen", new JsonObject { ["card"] = CardId(card) }));

    // ---- damage, HP, block ----------------------------------------------

    /// <summary>The spine: HP changed for any reason, with the signed amount. Damage
    /// taken and healing are these numbers, so nothing has to be inferred from block
    /// arithmetic.</summary>
    public static void HpChanged(Creature? creature, decimal delta) => Safe(() =>
        EventLog.Add("hp", new JsonObject
        {
            ["c"] = Name(creature),
            ["d"] = (int)delta,
            ["hp"] = creature?.CurrentHp ?? 0
        }));

    public static void DamageAboutToBeReceived(Creature? target, decimal amount) => Safe(() =>
        EventLog.Add("recv0", new JsonObject { ["t"] = Name(target), ["n"] = (int)amount }));

    public static void DamageReceived(Creature? target, DamageResult? result, Creature? dealer) => Safe(() =>
        EventLog.Add("recv", new JsonObject
        {
            ["t"] = Name(target),
            ["by"] = Name(dealer),
            ["total"] = result?.TotalDamage ?? 0,
            ["blk"] = result?.BlockedDamage ?? 0,
            ["unb"] = result?.UnblockedDamage ?? 0,
            ["ovk"] = result?.OverkillDamage ?? 0,
            ["broke"] = result?.WasBlockBroken ?? false,
            ["full"] = result?.WasFullyBlocked ?? false,
            ["kill"] = result?.WasTargetKilled ?? false
        }));

    public static void DamageGiven(Creature? dealer, DamageResult? result, Creature? target) => Safe(() =>
        EventLog.Add("hit", new JsonObject
        {
            ["by"] = Name(dealer),
            ["t"] = Name(target),
            ["total"] = result?.TotalDamage ?? 0,
            ["blk"] = result?.BlockedDamage ?? 0,
            ["unb"] = result?.UnblockedDamage ?? 0,
            ["ovk"] = result?.OverkillDamage ?? 0
        }));

    public static void BlockGained(Creature? creature, decimal amount) => Safe(() =>
        EventLog.Add("blk", new JsonObject { ["c"] = Name(creature), ["n"] = (int)amount }));

    public static void BlockBroken(Creature? target, Creature? breaker) => Safe(() =>
        EventLog.Add("blkb", new JsonObject { ["c"] = Name(target), ["by"] = Name(breaker) }));

    // ---- powers, orbs, stars, death -------------------------------------

    public static void PowerChanged(PowerModel? power, decimal amount, Creature? applier) => Safe(() =>
        EventLog.Add("pow", new JsonObject
        {
            ["c"] = Name(power?.Owner),
            ["p"] = power?.Id.ToString() ?? "",
            ["n"] = power?.Amount ?? 0,
            ["d"] = (int)amount,
            ["by"] = Name(applier)
        }));

    public static void OrbChanneled(OrbModel? orb) => Safe(() =>
        EventLog.Add("orb", new JsonObject { ["o"] = orb?.Id.ToString() ?? "" }));

    public static void OrbEvoked(OrbModel? orb) => Safe(() =>
        EventLog.Add("evoke", new JsonObject { ["o"] = orb?.Id.ToString() ?? "" }));

    public static void StarsGained(int amount) => Safe(() =>
        EventLog.Add("star", new JsonObject { ["n"] = amount }));

    public static void StarsSpent(int amount) => Safe(() =>
        EventLog.Add("star", new JsonObject { ["n"] = -amount }));

    public static void Died(Creature? creature, bool prevented) => Safe(() =>
        EventLog.Add("die", new JsonObject { ["c"] = Name(creature), ["stop"] = prevented }));

    public static void Shuffled() => Safe(() => EventLog.Add("shuffle"));
}

// ---- one patch per event point ------------------------------------------
// Parameter names must match the target's exactly: Harmony binds by name.

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
public static class EventCombatStart
{
    [HarmonyPostfix]
    public static void Postfix(IRunState runState, ICombatState combatState)
    {
        EventHooks.FightStart(runState, combatState);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
public static class EventCombatVictory
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState)
    {
        EventHooks.FightVictory(combatState);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
public static class EventCombatEnd
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState)
    {
        EventHooks.FightEnd(combatState);
        EventLog.WriteDebug(EventHooks.DebugPath());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
public static class EventSideTurnStart
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, CombatSide side)
    {
        EventHooks.SideTurnStart(combatState, side);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class EventPlayerTurnStart
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, Player player)
    {
        EventHooks.PlayerTurnStart(combatState, player);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterTakingExtraTurn))]
public static class EventExtraTurn
{
    [HarmonyPostfix]
    public static void Postfix(Player player)
    {
        EventHooks.ExtraTurn(player);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeHandDraw))]
public static class EventBeforeHandDraw
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, Player player)
    {
        EventHooks.BeforeHandDraw(combatState, player);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergyReset))]
public static class EventEnergyReset
{
    [HarmonyPostfix]
    public static void Postfix(Player player)
    {
        EventHooks.EnergyReset(player);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergySpent))]
public static class EventEnergySpent
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, int amount)
    {
        EventHooks.EnergySpent(card, amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnEnd))]
public static class EventSideTurnEnd
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, CombatSide side)
    {
        EventHooks.SideTurnEnd(combatState, side);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
public static class EventCardDrawn
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, bool fromHandDraw)
    {
        EventHooks.CardDrawn(card, fromHandDraw);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
public static class EventCardPlayed
{
    [HarmonyPostfix]
    public static void Postfix(CardPlay cardPlay)
    {
        EventHooks.CardPlayed(cardPlay);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
public static class EventCardDiscarded
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card)
    {
        EventHooks.CardDiscarded(card);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
public static class EventCardExhausted
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, bool causedByEthereal)
    {
        EventHooks.CardExhausted(card, causedByEthereal);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
public static class EventCardGenerated
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card)
    {
        EventHooks.CardGenerated(card);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCurrentHpChanged))]
public static class EventHpChanged
{
    [HarmonyPostfix]
    public static void Postfix(Creature creature, decimal delta)
    {
        EventHooks.HpChanged(creature, delta);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeDamageReceived))]
public static class EventDamageIncoming
{
    [HarmonyPostfix]
    public static void Postfix(Creature target, decimal amount)
    {
        EventHooks.DamageAboutToBeReceived(target, amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
public static class EventDamageReceived
{
    [HarmonyPostfix]
    public static void Postfix(Creature target, DamageResult result, Creature dealer)
    {
        EventHooks.DamageReceived(target, result, dealer);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
public static class EventDamageGiven
{
    [HarmonyPostfix]
    public static void Postfix(Creature dealer, DamageResult results, Creature target)
    {
        EventHooks.DamageGiven(dealer, results, target);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
public static class EventBlockGained
{
    [HarmonyPostfix]
    public static void Postfix(Creature creature, decimal amount)
    {
        EventHooks.BlockGained(creature, amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockBroken))]
public static class EventBlockBroken
{
    [HarmonyPostfix]
    public static void Postfix(Creature target, Creature breaker)
    {
        EventHooks.BlockBroken(target, breaker);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
public static class EventPowerChanged
{
    [HarmonyPostfix]
    public static void Postfix(PowerModel power, decimal amount, Creature applier)
    {
        EventHooks.PowerChanged(power, amount, applier);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterOrbChanneled))]
public static class EventOrbChanneled
{
    [HarmonyPostfix]
    public static void Postfix(OrbModel orb)
    {
        EventHooks.OrbChanneled(orb);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterOrbEvoked))]
public static class EventOrbEvoked
{
    [HarmonyPostfix]
    public static void Postfix(OrbModel orb)
    {
        EventHooks.OrbEvoked(orb);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterStarsGained))]
public static class EventStarsGained
{
    [HarmonyPostfix]
    public static void Postfix(int amount)
    {
        EventHooks.StarsGained(amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterStarsSpent))]
public static class EventStarsSpent
{
    [HarmonyPostfix]
    public static void Postfix(int amount)
    {
        EventHooks.StarsSpent(amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
public static class EventDeath
{
    [HarmonyPostfix]
    public static void Postfix(Creature creature, bool wasRemovalPrevented)
    {
        EventHooks.Died(creature, wasRemovalPrevented);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterShuffle))]
public static class EventShuffle
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        EventHooks.Shuffled();
    }
}
