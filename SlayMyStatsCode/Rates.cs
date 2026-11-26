namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// A card's or relic's win and pick rate across all players and characters, in whole percents. A figure resting
/// on fewer than <see cref="MinRuns"/> runs (or offers) is left out. No Godot or game code.
/// </summary>
public sealed record Rates(int? Win, int? Pick)
{
    public const int MinRuns = 20;

    /// <summary>Null when there is nothing to show.</summary>
    public static Rates? Of(PackData data, bool multiplayer, string id, bool relic)
    {
        if (data.Lanes?.GetValueOrDefault(multiplayer ? "multi" : "solo") is not { } lane) return null;

        int? win = (relic ? lane.Relics : lane.Cards).GetValueOrDefault(id) is [var runs, var wins, ..]
            ? Percent(wins, runs)
            : null;

        int picks = 0, offers = 0;
        foreach (CharacterPicks character in lane.Picks.Values)
        {
            if ((relic ? character.Relics : character.Cards).GetValueOrDefault(id) is not [var taken, _, var offered, ..])
                continue;
            picks += taken;
            offers += offered;
        }
        int? pick = Percent(picks, offers);

        return win == null && pick == null ? null : new Rates(win, pick);
    }

    internal static int? Percent(int part, int whole) =>
        whole < MinRuns ? null : (int)Math.Round(100.0 * part / whole, MidpointRounding.AwayFromZero);
}
