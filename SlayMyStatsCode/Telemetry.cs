using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// What the deck actually did, fight by fight and turn by turn, so the site's
/// deck-strength lines can be checked against real play instead of inferred from
/// deck contents alone. The game's own Hook points carry everything: damage
/// dealt and blocked, block gained, energy spent, cards drawn and played, powers
/// in play, orbs channelled.
///
/// Kept as counters, never as an event log: one small record a turn, a few
/// hundred bytes compressed for a whole run. Nothing here may touch the game --
/// every hook body is wrapped, and a failure turns telemetry off for the run
/// rather than reaching the combat code.
/// </summary>
public static class Telemetry
{
    // v2: a turn now spans the enemy phase, and carries the hand it was dealt
    // and its closing energy. v1 dropped everything the enemies did.
    public const int SchemaVersion = 2;

    private sealed class Turn
    {
        public int Number;
        public int EnergyStart;
        public int EnergyEnd;
        public int EnergySpent;
        public int StarsStart;
        public int Dealt;
        public int Taken;
        public int Blocked;
        public int BlockGained;
        public int Orbs;
        public List<string> Hand = [];
        public List<string> Drawn = [];
        public List<string> Played = [];
        public Dictionary<string, int> Powers = [];
    }

    private sealed class Fight
    {
        public string Encounter = "";
        public int HpStart;
        public int HpEnd;
        public List<Turn> Turns = [];
    }

    private static readonly List<Fight> Fights = [];
    private static Fight? _fight;
    private static Turn? _turn;
    private static ICombatState? _state;  // the fight in progress: a turn can start from any of its events
    private static bool _broken;

    /// <summary>The run's telemetry as one object, or null when nothing was recorded.</summary>
    public static JsonObject? Take()
    {
        try
        {
            CloseTurn();
            if (Fights.Count == 0 || _broken) return null;
            JsonArray fights = [];
            foreach (Fight f in Fights)
            {
                JsonArray turns = [];
                foreach (Turn t in f.Turns)
                {
                    turns.Add(new JsonObject
                    {
                        ["n"] = t.Number,
                        ["e"] = t.EnergyStart,
                        ["ee"] = t.EnergyEnd,
                        ["es"] = t.EnergySpent,
                        ["star"] = t.StarsStart,
                        ["dmg"] = t.Dealt,
                        ["taken"] = t.Taken,
                        ["blk"] = t.Blocked,
                        ["bgn"] = t.BlockGained,
                        ["orbs"] = t.Orbs,
                        ["drawn"] = new JsonArray([.. t.Drawn.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()]),
                        ["hand"] = new JsonArray([.. t.Hand.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()]),
                        ["played"] = new JsonArray([.. t.Played.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()]),
                        ["powers"] = new JsonObject(t.Powers.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))))
                    });
                }
                fights.Add(new JsonObject
                {
                    ["enc"] = f.Encounter,
                    ["hp0"] = f.HpStart,
                    ["hp1"] = f.HpEnd,
                    ["turns"] = turns
                });
            }
            return new JsonObject { ["v"] = SchemaVersion, ["fights"] = fights };
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Telemetry couldn't be written: {e}");
            return null;
        }
    }

    /// <summary>A new run starts: forget the last one.</summary>
    public static void Reset()
    {
        Fights.Clear();
        _fight = null;
        _turn = null;
        _broken = false;
    }

    private static void Guard(Action body)
    {
        if (_broken) return;
        try
        {
            body();
        }
        catch (Exception e)
        {
            _broken = true;  // one bad hook turns telemetry off, never the game
            MainFile.Logger.Error($"Telemetry off for this run: {e}");
        }
    }

    private static bool IsPlayer(Creature creature) => creature is { IsPlayer: true };

    private static int PlayerHp(ICombatState state) =>
        state.Players.Where(p => p.Creature != null).Sum(p => p.Creature.CurrentHp);

    private static int Energy(ICombatState state) =>
        state.Players.Sum(p => p.PlayerCombatState?.Energy ?? 0);

    private static int Stars(ICombatState state) =>
        state.Players.Sum(p => p.PlayerCombatState?.Stars ?? 0);

    private static void CloseTurn()
    {
        if (_fight != null && _turn != null) _fight.Turns.Add(_turn);
        _turn = null;
    }

    /// <summary>
    /// The turn events belong to. Opened by whichever of the turn's first events
    /// arrives first -- the hand is drawn before the turn-start hook, and the
    /// enemy's attacks land after the player's side has ended -- and closed only
    /// when the next player turn begins. Closing on the player's own turn end
    /// loses everything the enemies did, which is most of the damage taken.
    /// </summary>
    private static void EnsureTurn()
    {
        if (_state == null) return;
        int turn = TurnNumber(_state);
        if (_turn != null && _turn.Number == turn) return;
        CloseTurn();
        _turn = new Turn
        {
            Number = turn,
            EnergyStart = Energy(_state),
            StarsStart = Stars(_state)
        };
        SnapshotPowers(_state);
    }

    /// <summary>The player's own turn count: it moves once a player turn, where the
    /// round number would also move mid-round and split a turn in two.</summary>
    private static int TurnNumber(ICombatState state)
    {
        int n = 0;
        foreach (Player p in state.Players)
        {
            int t = p.PlayerCombatState?.TurnNumber ?? 0;
            if (t > n) n = t;
        }
        return n > 0 ? n : state.RoundNumber;
    }

    /// <summary>The powers standing at the turn's start: uptime, not just changes.</summary>
    private static void SnapshotPowers(ICombatState state)
    {
        if (_turn == null) return;
        foreach (Player p in state.Players)
        {
            if (p.Creature == null) continue;
            foreach (PowerModel power in p.Creature.Powers)
            {
                if (power.Amount != 0) _turn.Powers[power.Id.ToString()] = power.Amount;
            }
        }
    }

    /// <summary>The hand the turn was dealt. The draw happens before the turn-start
    /// hook, so the per-card draw hook never sees it.</summary>
    private static void SnapshotHand(ICombatState state)
    {
        if (_turn == null) return;
        _turn.Hand.Clear();
        foreach (Player p in state.Players)
        {
            foreach (CardModel card in p.PlayerCombatState?.Hand?.Cards ?? [])
            {
                _turn.Hand.Add(card.Id.ToString());
            }
        }
    }

    // ---- the hooks -------------------------------------------------------

    public static void BeginFight(ICombatState state) => Guard(() =>
    {
        CloseTurn();
        _state = state;
        _fight = new Fight
        {
            Encounter = state.Encounter?.Id?.ToString() ?? "",
            HpStart = PlayerHp(state)
        };
        Fights.Add(_fight);
    });

    public static void EndFight(ICombatState state) => Guard(() =>
    {
        CloseTurn();
        if (_fight != null) _fight.HpEnd = PlayerHp(state);
        _fight = null;
        _state = null;
    });

    /// <summary>The turn really starts here: the hand it was dealt, and the moment
    /// the turn's own draws begin, so anything drawn before this was the hand.</summary>
    public static void BeginTurn() => Guard(() =>
    {
        EnsureTurn();
        if (_state == null || _turn == null) return;
        SnapshotHand(_state);
        _turn.Drawn.Clear();
    });

    /// <summary>The turn's energy is granted after the turn begins, so take it here:
    /// this is the only point where the amount is settled.</summary>
    public static void EnergyReset(Player player) => Guard(() =>
    {
        EnsureTurn();
        if (_turn != null) _turn.EnergyStart = player.PlayerCombatState?.Energy ?? 0;
    });

    /// <summary>Where the player's turn ended, for what the turn gained or banked.</summary>
    public static void EnergyEnd(ICombatState state, CombatSide side) => Guard(() =>
    {
        if (_turn == null || side != CombatSide.Player) return;
        _turn.EnergyEnd = Energy(state);
    });

    public static void CardDrawn(CardModel card) => Guard(() =>
    {
        EnsureTurn();
        _turn?.Drawn.Add(card.Id.ToString());
    });

    public static void CardPlayed(CardPlay play) => Guard(() =>
    {
        EnsureTurn();
        _turn?.Played.Add(play.Card.Id.ToString());
    });

    public static void EnergySpent(int amount) => Guard(() =>
    {
        EnsureTurn();
        if (_turn != null) _turn.EnergySpent += amount;
    });

    public static void DamageGiven(Creature dealer, DamageResult result) => Guard(() =>
    {
        if (!IsPlayer(dealer)) return;
        EnsureTurn();
        if (_turn != null) _turn.Dealt += result.TotalDamage;
    });

    public static void DamageReceived(Creature target, DamageResult result) => Guard(() =>
    {
        if (!IsPlayer(target)) return;
        EnsureTurn();
        if (_turn == null) return;
        _turn.Taken += result.UnblockedDamage;
        _turn.Blocked += result.BlockedDamage;
    });

    public static void BlockGained(Creature creature, decimal amount) => Guard(() =>
    {
        if (!IsPlayer(creature)) return;
        EnsureTurn();
        if (_turn != null) _turn.BlockGained += (int)amount;
    });

    public static void PowerChanged(PowerModel power, Creature applier) => Guard(() =>
    {
        if (applier == null || !IsPlayer(applier)) return;
        EnsureTurn();
        if (_turn == null) return;
        _turn.Powers[power.Id.ToString()] = power.Amount;
    });

    public static void OrbChanneled() => Guard(() =>
    {
        EnsureTurn();
        if (_turn != null) _turn.Orbs++;
    });
}

/// <summary>Combat boundaries: one telemetry fight per combat.</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
public static class TelemetryCombatStart
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState)
    {
        try { Telemetry.BeginFight(combatState); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
public static class TelemetryCombatEnd
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, CombatRoom room)
    {
        try { Telemetry.EndFight(combatState); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
public static class TelemetryTurnStart
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { Telemetry.BeginTurn(); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnEnd))]
public static class TelemetryTurnEnd
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, CombatSide side)
    {
        try { Telemetry.EnergyEnd(combatState, side); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergyReset))]
public static class TelemetryEnergyReset
{
    [HarmonyPostfix]
    public static void Postfix(Player player)
    {
        try { Telemetry.EnergyReset(player); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
public static class TelemetryCardDrawn
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card)
    {
        try { Telemetry.CardDrawn(card); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
public static class TelemetryCardPlayed
{
    [HarmonyPostfix]
    public static void Postfix(CardPlay cardPlay)
    {
        try { Telemetry.CardPlayed(cardPlay); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergySpent))]
public static class TelemetryEnergySpent
{
    [HarmonyPostfix]
    public static void Postfix(int amount)
    {
        try { Telemetry.EnergySpent(amount); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
public static class TelemetryDamageGiven
{
    [HarmonyPostfix]
    public static void Postfix(Creature dealer, DamageResult results)
    {
        try { Telemetry.DamageGiven(dealer, results); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
public static class TelemetryDamageReceived
{
    [HarmonyPostfix]
    public static void Postfix(Creature target, DamageResult result)
    {
        try { Telemetry.DamageReceived(target, result); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
public static class TelemetryBlockGained
{
    [HarmonyPostfix]
    public static void Postfix(Creature creature, decimal amount)
    {
        try { Telemetry.BlockGained(creature, amount); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
public static class TelemetryPowerChanged
{
    [HarmonyPostfix]
    public static void Postfix(PowerModel power, Creature applier)
    {
        try { Telemetry.PowerChanged(power, applier); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterOrbChanneled))]
public static class TelemetryOrbChanneled
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { Telemetry.OrbChanneled(); } catch (Exception e) { MainFile.Logger.Error($"{e}"); }
    }
}
