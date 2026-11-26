namespace SlayMyStats.SlayMyStatsCode;

/// <summary>One fight of the current act against the average win on the same encounter.</summary>
public sealed record FightLine(string Encounter, int Turns, double WinTurns, double Taken, double WinTaken);

/// <summary>
/// What the run strength mark shows: the latest act's reading, the fights it rests on, and the readings of the
/// acts before it. Unlike the site, an act reads from its first fight. Once the run is won it also has the run as
/// a whole, read as the site reads it. No Godot or game code.
/// </summary>
public sealed record Strength(ActReading Current, IReadOnlyList<FightLine> Fights, IReadOnlyList<ActReading> Earlier,
    RunReading? Run = null)
{
    /// <summary>The number of fights the log must cover for the local player's run records.</summary>
    public int FightCount { get; init; }

    /// <summary>The reading of <paramref name="act"/>, or until it has a fight, of the act before. Null with no
    /// fight to read.</summary>
    /// <param name="player">Whose damage counts in a party: their place in the run's players.</param>
    /// <param name="won">The run is over and won: acts need the site's number of fights, and the run is read.</param>
    public static Strength? Of(RunRecord run, RulerData demand, int act, int player, bool won = false)
    {
        List<Fight> fights = Ruler.RunFights(run);
        double Taken(Fight f) => f.Coop && player < f.Each.Length ? f.Each[player] : f.Taken;
        List<ActReading> acts = Ruler.ActReadings(fights, demand, Taken, min: won ? Ruler.ActMin : 1);
        if (acts.LastOrDefault(a => won || a.Act <= act) is not { } current) return null;

        // The same fights the act's reading counts.
        List<FightLine> lines = [];
        foreach (Fight f in fights)
        {
            if (f.Act != current.Act || f.Died || f.PossibleFurCoat ||
                !demand.Encounters.TryGetValue(f.Encounter, out Encounter? d)) continue;
            if (Ruler.WinnersOf(f, d) is { } how)
                lines.Add(new FightLine(f.Encounter, f.Turns, how.Turns!.Value, Taken(f), how.Taken));
        }
        return new Strength(current, lines, acts.Where(a => a.Act < current.Act).ToList(),
            won ? Ruler.ReadRun(acts, demand.Cuts) : null) { FightCount = fights.Count };
    }
}
