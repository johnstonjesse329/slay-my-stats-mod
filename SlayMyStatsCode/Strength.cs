namespace SlayMyStats.SlayMyStatsCode;

/// <summary>One fight of the current act against the average win on the same encounter.</summary>
public sealed record FightLine(string Encounter, int Turns, double WinTurns, double Taken, double WinTaken);

/// <summary>
/// What the run strength mark shows: the latest act's reading, the fights it rests on, and the readings of the
/// acts before it. Unlike the site, an act reads from its first fight. No Godot or game code.
/// </summary>
public sealed record Strength(ActReading Current, IReadOnlyList<FightLine> Fights, IReadOnlyList<ActReading> Earlier)
{
    /// <summary>The reading of <paramref name="act"/>, or until it has a fight, of the act before. Null with no
    /// fight to read.</summary>
    /// <param name="player">Whose damage counts in a party: their place in the run's players.</param>
    public static Strength? Of(RunRecord run, RulerData demand, int act, int player)
    {
        List<Fight> fights = Ruler.RunFights(run);
        double Taken(Fight f) => f.Coop && player < f.Each.Length ? f.Each[player] : f.Taken;
        List<ActReading> acts = Ruler.ActReadings(fights, demand, Taken, min: 1);
        if (acts.LastOrDefault(a => a.Act <= act) is not { } current) return null;

        // The same fights the act's reading counts.
        List<FightLine> lines = [];
        foreach (Fight f in fights)
        {
            if (f.Act != current.Act || f.Died || f.PossibleFurCoat ||
                !demand.Encounters.TryGetValue(f.Encounter, out Encounter? d)) continue;
            if (Ruler.WinnersOf(f, d) is { } how)
                lines.Add(new FightLine(f.Encounter, f.Turns, how.Turns!.Value, Taken(f), how.Taken));
        }
        return new Strength(current, lines, acts.Where(a => a.Act < current.Act).ToList());
    }
}
