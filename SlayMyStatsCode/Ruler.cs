using System.Text.Json.Serialization;

namespace SlayMyStats.SlayMyStatsCode;

// A run as a run file has it, as far as the ruler reads it. LiveRun builds the same from the game.

public sealed class RunRecord
{
    [JsonPropertyName("players")] public List<RunPlayer>? Players { get; init; }
    [JsonPropertyName("was_abandoned")] public bool WasAbandoned { get; init; }
    [JsonPropertyName("game_mode")] public string? GameMode { get; init; }
    [JsonPropertyName("ascension")] public int Ascension { get; init; }
    [JsonPropertyName("win")] public bool Win { get; init; }
    [JsonPropertyName("map_point_history")] public List<List<RunPoint>>? Acts { get; init; }

    /// <summary>The run is still going, so its last fight isn't the one that ended it. Not in a run file.</summary>
    [JsonIgnore] public bool InProgress { get; init; }
}

public sealed class RunPlayer
{
    [JsonPropertyName("relics")] public List<RunRelic>? Relics { get; init; }
}

public sealed class RunRelic
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("floor_added_to_deck")] public int? Floor { get; init; }
}

public sealed class RunPoint
{
    [JsonPropertyName("map_point_type")] public string? Type { get; init; }
    [JsonPropertyName("player_stats")] public List<PointStats>? Stats { get; init; }
    [JsonPropertyName("rooms")] public List<RunRoom>? Rooms { get; init; }
}

public sealed class RunRoom
{
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }
    [JsonPropertyName("monster_ids")] public List<string>? MonsterIds { get; init; }
    [JsonPropertyName("turns_taken")] public int Turns { get; init; }
}

public sealed class PointStats
{
    [JsonPropertyName("player_id")] public ulong PlayerId { get; init; }
    [JsonPropertyName("damage_taken")] public int DamageTaken { get; init; }
    [JsonPropertyName("hp_healed")] public int HpHealed { get; init; }
    [JsonPropertyName("relic_choices")] public List<RelicChoice>? RelicChoices { get; init; }
}

public sealed class RelicChoice
{
    [JsonPropertyName("choice")] public string? Choice { get; init; }
    [JsonPropertyName("was_picked")] public bool WasPicked { get; init; }
}

public sealed record Fight(string Encounter, int Turns, double Taken, double[] Each, int Ascension, string? Kind,
    int Act, bool Coop, double Share, bool PossibleFurCoat)
{
    public bool Died { get; init; }
}

public sealed record Reading(string Encounter, int Act, double Ask, double Doing, double Damage);

public record Totals(double Turns, double WinTurns, double Taken, double WinTaken, double Pace, double Guard);

public sealed record ActReading(int Act, int Fights, double Turns, double WinTurns, double Taken, double WinTaken,
    double Pace, double Guard, double Each, string Speed, string Held, string Verdict, string? Lacking)
    : Totals(Turns, WinTurns, Taken, WinTaken, Pace, Guard);

public sealed record RunReading(Totals Totals, string Verdict, bool Op);

/// <summary>
/// How a run is keeping up with runs that won, ported from the site's tools/power_curve/fight_demand.py; each
/// function here is the one of the same name there, and the two must agree (SlayMyStatsProbe ruler). No game or
/// Godot code.
/// </summary>
public static class Ruler
{
    public const int Tough = 8;
    public const int ActMin = 3;
    public const string FurCoat = "RELIC.FUR_COAT";
    public const string Ahead = "ahead", Meeting = "meeting", Behind = "behind";

    private static readonly int[] OpActs = [1, 2];

    public static double HpShare(int act, string? kind) =>
        act == 0 ? 1.1 : act == 2 && kind == "boss" ? 1.3 : 1.2;

    public static int NetTaken(PointStats? stats) =>
        stats == null ? 0 : Math.Max(0, stats.DamageTaken - stats.HpHealed);

    /// <summary>
    /// One run's fights; empty for an abandoned run or any mode but standard.
    /// </summary>
    public static List<Fight> RunFights(RunRecord run)
    {
        List<Fight> fights = [];
        int party = run.Players?.Count ?? 0;
        if (party == 0 || run.WasAbandoned || (run.GameMode ?? "standard") != "standard") return fights;

        int coatFloor = run.Players!.SelectMany(p => p.Relics ?? [])
            .Where(r => r.Id == FurCoat && r.Floor != null)
            .Select(r => r.Floor!.Value)
            .DefaultIfEmpty(int.MaxValue).Min();
        int floor = 0;
        List<List<RunPoint>> acts = run.Acts ?? [];
        for (int act = 0; act < acts.Count; act++)
        {
            foreach (RunPoint point in acts[act])
            {
                floor++;
                List<PointStats> stats = point.Stats ?? [];
                if (stats.Any(s => s.RelicChoices?.Any(c => c.Choice == FurCoat && c.WasPicked) == true))
                    coatFloor = Math.Min(coatFloor, floor);
                foreach (RunRoom room in point.Rooms ?? [])
                {
                    if (room.MonsterIds is not { Count: > 0 } || room.Turns == 0 || room.ModelId == null) continue;
                    // With no stats at all, one player who took nothing.
                    double[] each = stats.Count == 0 ? [0] : stats.Select(s => (double)NetTaken(s)).ToArray();
                    fights.Add(new Fight(room.ModelId, room.Turns, each.Sum() / each.Length, each, run.Ascension,
                        point.Type, act, party > 1, party > 1 ? HpShare(act, point.Type) : 1,
                        floor > coatFloor && room.Turns == 1));
                }
            }
        }

        if (fights.Count > 0) fights[^1] = fights[^1] with { Died = !run.InProgress && !run.Win };
        return fights;
    }

    public static int Tier(int ascension) => ascension >= Tough ? 1 : 0;

    /// <summary>The line a fight reads against, or null if too few have won it at its tier.</summary>
    public static Line? WinnersOf(Fight fight, Encounter encounter)
    {
        int tier = Tier(fight.Ascension);
        if (tier >= encounter.Tiers.Length) return null;
        Line? line = encounter.Tiers[tier];
        Line? how = fight.Coop ? line?.Coop ?? line : line;
        return how?.Turns != null ? how : null;
    }

    /// <summary>One row per fight: the HP it took a turn, over what clearing it in winners' turns asks.</summary>
    public static List<Reading> Readings(IEnumerable<Fight> fights, RulerData demand)
    {
        List<Reading> rows = [];
        foreach (Fight f in fights)
        {
            if (f.PossibleFurCoat || !demand.Encounters.TryGetValue(f.Encounter, out Encounter? d)) continue;
            int tough = Tier(f.Ascension);
            double pool = tough < d.Pool.Length ? d.Pool[tough] * f.Share : 0;
            if (pool == 0 || WinnersOf(f, d) is not { } how) continue;
            double ask = pool / how.Turns!.Value;
            double doing = pool / f.Turns;
            rows.Add(new Reading(f.Encounter, f.Act, ask, doing, doing / ask));
        }
        return rows;
    }

    /// <summary>One row per act with enough fights. <paramref name="taken"/> picks whose damage counts:
    /// <see cref="Fight.Each"/>[local player] in a party. <paramref name="min"/> is how many fights are enough.</summary>
    public static List<(int Act, int Fights, Totals Totals, double Each)> ActTotals(IReadOnlyList<Fight> fights,
        RulerData demand, Func<Fight, double>? taken = null, int min = ActMin)
    {
        taken ??= f => f.Taken;
        List<(int, int, Totals, double)> rows = [];
        foreach (int act in fights.Select(f => f.Act).Distinct().Order())
        {
            List<(Fight Fight, Line How)> part = [];
            foreach (Fight f in fights)
            {
                if (f.Act != act || f.Died || f.PossibleFurCoat ||
                    !demand.Encounters.TryGetValue(f.Encounter, out Encounter? d)) continue;
                if (WinnersOf(f, d) is { } how) part.Add((f, how));
            }
            if (part.Count < min) continue;
            Totals totals = Total(Sum(part.Select(p => (double)p.Fight.Turns)), Sum(part.Select(p => p.How.Turns!.Value)),
                Sum(part.Select(p => taken(p.Fight))), Sum(part.Select(p => p.How.Taken)));
            rows.Add((act, part.Count, totals, totals.Guard / totals.Pace));
        }
        return rows;
    }

    public static List<ActReading> ActReadings(IReadOnlyList<Fight> fights, RulerData demand,
        Func<Fight, double>? taken = null, int min = ActMin)
    {
        List<ActReading> rows = [];
        foreach ((int act, int count, Totals t, double each) in ActTotals(fights, demand, taken, min))
        {
            Cut cut = ActCut(demand.Cuts, act);
            string place = Verdict(t.Pace, t.Guard, cut);
            rows.Add(new ActReading(act, count, t.Turns, t.WinTurns, t.Taken, t.WinTaken, t.Pace, t.Guard, each,
                Mark(t.Pace, cut.Pace), Mark(t.Guard, cut.Guard), place,
                place == Behind ? Lacking(t.Pace, each) : null));
        }
        return rows;
    }

    public static Totals? RunTotals(IReadOnlyList<ActReading> acts) => acts.Count == 0
        ? null
        : Total(Sum(acts.Select(a => a.Turns)), Sum(acts.Select(a => a.WinTurns)),
            Sum(acts.Select(a => a.Taken)), Sum(acts.Select(a => a.WinTaken)));

    /// <summary>The run as a whole; null with no act to read.</summary>
    public static RunReading? ReadRun(IReadOnlyList<ActReading> acts, Cuts cuts)
    {
        if (RunTotals(acts) is not { } whole) return null;
        bool op = OpActs.All(act => acts.Any(a => a.Act == act && a.Verdict == Ahead));
        return new RunReading(whole, Verdict(whole.Pace, whole.Guard, cuts.Run), op);
    }

    public static Cut ActCut(Cuts cuts, int act) => act < cuts.Acts.Length ? cuts.Acts[act] : cuts.Run;

    public static string Mark(double value, double[] edges) =>
        value < edges[0] ? Behind : value > edges[1] ? Ahead : Meeting;

    public static string Verdict(double pace, double guard, Cut cut)
    {
        string held = Mark(guard, cut.Guard);
        return held == Meeting && pace > cut.Pace[1] && guard >= 1 ? Ahead : held;
    }

    public static string Lacking(double pace, double each) => pace < each ? "damage" : "block";

    private static Totals Total(double turns, double winTurns, double taken, double winTaken) =>
        new(turns, winTurns, taken, winTaken, winTurns / turns, (winTaken + 1) / (taken + 1));

    /// <summary>Compensated, as Python's sum of floats is, so a total on a cut lands on the same side.</summary>
    private static double Sum(IEnumerable<double> values)
    {
        double sum = 0, lost = 0;
        foreach (double value in values)
        {
            double next = sum + value;
            lost += Math.Abs(sum) >= Math.Abs(value) ? sum - next + value : value - next + sum;
            sum = next;
        }
        return sum + lost;
    }
}
