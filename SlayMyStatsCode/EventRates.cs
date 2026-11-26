using System.Text.RegularExpressions;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// How often all players take an event option, and their win rate after taking it, in whole percents. Nothing
/// under <see cref="Rates.MinRuns"/> takes; no pick rate where the option isn't offered on every visit. Only an
/// event's first page is counted. No Godot or game code.
/// </summary>
public sealed record EventRates(int? Pick, int Win, int Taken)
{
    // The key a run file records a choice by; the site's event_visit (run.py) reads it the same way.
    private static readonly Regex ChoiceKey = new(@"^[A-Z0-9_]+\.pages\.INITIAL\.options\.([A-Z0-9_]+)\.title$");

    /// <summary>Null when there is nothing to show.</summary>
    public static EventRates? Of(PackData data, bool multiplayer, string eventId, string historyKey)
    {
        if (ChoiceKey.Match(historyKey) is not { Success: true } key) return null;
        if (data.Lanes?.GetValueOrDefault(multiplayer ? "multi" : "solo") is not { } lane ||
            lane.Events.GetValueOrDefault(eventId) is not { } choices ||
            choices.Options.GetValueOrDefault(key.Groups[1].Value) is not [var taken, var wins, ..] ||
            taken < Rates.MinRuns)
            return null;
        int? pick = choices.Visits is { } visits ? Rates.Percent(taken, visits) : null;
        return new EventRates(pick, Rates.Percent(wins, taken)!.Value, taken);
    }
}
