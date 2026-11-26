using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Self-checks for the event log, for the mod's author and testers only. Off unless the config sets
/// "debug": true; with it off nothing here runs and nothing extra is uploaded. With it on, a problem is
/// logged once as "[health] ..." and the whole list rides along in the run's telemetry, so a run uploaded by
/// a tester shows what went wrong without anyone opening their log.
///
/// No game types appear here, like <see cref="EventLog"/>, so a console project can link it.
/// </summary>
public static class RunHealth
{
    public static bool Enabled { get; set; }

    /// <summary>Where a new problem is reported; the mod points it at its log.</summary>
    public static Action<string> Log { get; set; } = _ => { };

    private const int MaxProblems = 50;
    private const double SlowHookMs = 10;

    private static readonly List<(string Code, string Detail)> Problems = [];
    private static long _hooks;
    private static long _ticks;
    private static long _maxTicks;

    /// <summary>Record a problem once; the same code and detail again is ignored.</summary>
    public static void Note(string code, string detail)
    {
        if (!Enabled || Problems.Count >= MaxProblems || Problems.Contains((code, detail))) return;
        Problems.Add((code, detail));
        Log($"[health] {code}: {detail}");
    }

    /// <summary>One hook's cost, in <see cref="Stopwatch"/> ticks.</summary>
    public static void Hook(long ticks)
    {
        _hooks++;
        _ticks += ticks;
        if (ticks > _maxTicks) _maxTicks = ticks;
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        if (ms > SlowHookMs) Note("slow-hook", $"{ms:F0} ms");
    }

    public static void Reset()
    {
        Problems.Clear();
        _hooks = _ticks = _maxTicks = 0;
    }

    /// <summary>Bring back what an earlier session of the same run found.</summary>
    internal static void Restore(JsonObject? health)
    {
        if (!Enabled || health?["problems"] is not JsonArray list) return;
        foreach (JsonObject p in list.OfType<JsonObject>())
        {
            if (Problems.Count < MaxProblems && p["code"] is JsonValue c && p["detail"] is JsonValue d)
                Problems.Add((c.ToString(), d.ToString()));
        }
    }

    /// <summary>The findings for the upload and the checkpoint; null when debug is off.</summary>
    internal static JsonObject? Take(int events)
    {
        if (!Enabled) return null;
        JsonArray problems = [];
        foreach ((string code, string detail) in Problems)
            problems.Add(new JsonObject { ["code"] = code, ["detail"] = detail });
        return new JsonObject
        {
            ["events"] = events,
            ["problems"] = problems,
            ["hooks"] = _hooks,
            ["hookMs"] = Math.Round(_ticks * 1000.0 / Stopwatch.Frequency, 1),
            ["hookMaxMs"] = Math.Round(_maxTicks * 1000.0 / Stopwatch.Frequency, 1)
        };
    }
}
