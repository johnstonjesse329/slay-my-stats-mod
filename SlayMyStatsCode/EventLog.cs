using System.Text;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>One player's damage to enemies over a run.</summary>
public sealed record Dealt(int Total, int BiggestHit);

/// <summary>
/// Everything the game does, one line per event, recorded as it happens.
///
/// This is deliberately dumb: it stores events and nothing else. Every judgement
/// about what an event means (which turn a hit belongs to, what counts as damage
/// taken, how a Power's uptime is derived) happens later, outside the game, from
/// these lines. That way a wrong reading is a parser fix plus a re-derive of logs
/// already uploaded, never another played run.
///
/// No game types appear here: the hooks translate, this records. That keeps it
/// linkable by a plain console project and free of the game's assembly.
/// </summary>
public static class EventLog
{
    /// <summary>Schema of the upload's telemetry object. 3 = this event log; 1 and
    /// 2 were earlier per-turn summaries, kept readable but not written any more.</summary>
    public const int SchemaVersion = 3;

    /// <summary>A long run is a few thousand events. The cap is only a backstop
    /// against a pathological run filling the upload.</summary>
    private const int MaxEvents = 200_000;

    private sealed class Entry
    {
        public long Seq;
        public int Fight;
        public int Turn;
        public string Kind = "";
        public JsonObject Args = [];
    }

    private static readonly List<Entry> Entries = [];
    internal static readonly AsyncLocal<int?> CurrentAction = new();
    private static long _seq;
    private static int _fight = -1;
    private static int _turn;
    private static bool _truncated;
    private static bool _broken;
    /// <summary>How many entries, from the start, the checkpoint file already holds.</summary>
    private static int _saved;
    /// <summary>The file holds entries the log no longer has, so the next save writes it whole.</summary>
    private static bool _rewrite;

    public static bool HasEvents => Entries.Count > 0;

    public static int Count => Entries.Count;

    public static bool Truncated => _truncated;

    /// <summary>The game's own turn number, stamped on every event so an event that
    /// arrives outside any call that names a turn still says which one it was in.</summary>
    public static void SetTurn(int turn)
    {
        if (turn > 0) _turn = turn;
    }

    public static void NewFight()
    {
        _fight++;
        _turn = 0;
    }

    internal static void DropLastFight()
    {
        if (_fight < 0) return;
        int start = Entries.FindLastIndex(e => e.Fight == _fight && e.Kind == "fight0");
        if (start < 0) return;
        if (start < _saved) _rewrite = true;
        RunHealth.Note("resume-dropped-fight", $"fight {_fight} was recorded before the save and again after; kept once");
        Entries.RemoveRange(start, Entries.Count - start);
        _fight--;
        _truncated = false;
    }

    /// <param name="inFile">The checkpoint's log file already holds these entries; when false (a checkpoint
    /// from before the log had its own file) the next save writes it whole.</param>
    internal static void Restore(JsonObject? telemetry, bool inFile = true)
    {
        if (telemetry?["log"] is not JsonArray log) return;
        foreach (JsonObject e in log.OfType<JsonObject>().Take(MaxEvents))
        {
            Entries.Add(new Entry
            {
                Seq = (long)e["q"]!,
                Fight = (int)e["f"]!,
                Turn = (int)e["t"]!,
                Kind = (string)e["k"]!,
                Args = (JsonObject)e["a"]!.DeepClone()
            });
        }
        RunHealth.Restore(telemetry["health"] as JsonObject);
        if (Entries.Count == 0) return;
        _saved = inFile ? Entries.Count : 0;
        RunHealth.Note("resumed", $"{Entries.Count} events restored from the checkpoint");
        _seq = Entries[^1].Seq;
        _fight = Entries.Any(e => e.Kind == "fight0") ? Entries[^1].Fight : -1;
        _truncated = (bool?)telemetry["truncated"] ?? false;
    }

    public static void Add(string kind, JsonObject? args = null)
    {
        if (_broken || _truncated) return;
        if (Entries.Count >= MaxEvents)
        {
            _truncated = true;
            RunHealth.Note("truncated", $"stopped recording at {MaxEvents} events");
            return;
        }
        if (CurrentAction.Value is int action)
        {
            args ??= [];
            args["action"] = action;
        }
        Entries.Add(new Entry
        {
            Seq = ++_seq,
            Fight = Math.Max(_fight, 0),
            Turn = _turn,
            Kind = kind,
            Args = args ?? []
        });
    }

    /// <summary>
    /// What the player in <paramref name="slot"/> has dealt to enemies this run, counted as the site's run records
    /// count it: the total includes damage to Block and never overkill; the biggest hit is one card's, overkill
    /// included (0 with no card hit). A pet's hits aren't the player's. Null unless the log has all
    /// <paramref name="fights"/> fights of the run, as a total missing some would mislead.
    /// </summary>
    public static Dealt? DealtBy(int slot, int fights)
    {
        if (_broken || _truncated || Entries.Count(e => e.Kind == "fight0") < fights) return null;
        int total = 0, biggest = 0;
        foreach (Entry e in Entries)
        {
            JsonObject a = e.Args;
            if (e.Kind != "hit" || (string?)a["by"] != "P" || (int?)a["sourceSlot"] != slot ||
                (bool?)a["enemy"] == false || (string?)a["t"] is "P" or "Osty" or "Byrdpip") continue;
            int dealt = (int?)a["total"] ?? 0;
            total += dealt;
            if (a["card"] is JsonValue card && card.ToString().Length > 0)
                biggest = Math.Max(biggest, dealt + ((int?)a["ovk"] ?? 0));
        }
        return new Dealt(total, biggest);
    }

    /// <summary>Called when a hook itself fails: the log stops for this run rather
    /// than repeating the failure every event, and the game never sees it.</summary>
    public static void Fail()
    {
        _broken = true;
        RunHealth.Note("hook-failed", $"recording stopped after {Entries.Count} events");
    }

    /// <summary>Debug only: looks the log over for the faults a save and resume can leave (a fight recorded twice,
    /// a fight that never closed, events out of order) and hands each to <see cref="RunHealth"/>. Safe to run
    /// after every fight: a finding is reported once.</summary>
    public static void Verify(bool runEnded)
    {
        if (!RunHealth.Enabled || Entries.Count == 0) return;
        HashSet<string> keys = [];
        long previous = 0;
        bool open = false, started = false;
        int fight = -1;
        foreach (Entry e in Entries)
        {
            if (e.Seq <= previous) RunHealth.Note("seq-order", $"event {e.Seq} follows {previous}");
            previous = e.Seq;
            switch (e.Kind)
            {
                case "fight0":
                    if (open) RunHealth.Note("fight-unclosed", $"fight {fight} never ended before the next began");
                    if (e.Args["key"] is JsonValue k && !keys.Add(k.ToString()))
                        RunHealth.Note("duplicate-fight", $"fight {e.Fight} repeats {k}");
                    open = started = true;
                    fight = e.Fight;
                    break;
                case "fight1":
                    if (!open) RunHealth.Note("fight-end-orphan", $"fight {e.Fight} ended without a start");
                    open = false;
                    break;
                default:
                    if (!started) RunHealth.Note("event-before-fight", e.Kind);
                    break;
            }
        }
        if (runEnded && open) RunHealth.Note("fight-unclosed", $"fight {fight} never ended");
    }

    public static void Reset()
    {
        RunHealth.Reset();
        Entries.Clear();
        _seq = 0;
        _fight = -1;
        _turn = 0;
        _truncated = false;
        _broken = false;
        _saved = 0;
        _rewrite = false;
    }

    /// <summary>
    /// The entries the checkpoint file doesn't hold yet, as its lines, and whether they replace the file
    /// rather than go on its end: they do for a new file, after <see cref="DropLastFight"/> took back lines it
    /// holds, or when <paramref name="whole"/> says the last write failed. From then on they count as saved.
    /// </summary>
    public static (string Lines, bool Whole) TakeUnsaved(bool whole = false)
    {
        whole |= _rewrite || _saved > Entries.Count || (_saved == 0 && Entries.Count > 0);
        StringBuilder sb = new();
        for (int i = whole ? 0 : _saved; i < Entries.Count; i++) AppendLine(sb, Entries[i]);
        _saved = Entries.Count;
        _rewrite = false;
        return (sb.ToString(), whole);
    }

    private static void AppendLine(StringBuilder sb, Entry e) =>
        sb.Append("{\"q\":").Append(e.Seq)
          .Append(",\"f\":").Append(e.Fight)
          .Append(",\"t\":").Append(e.Turn)
          .Append(",\"k\":").Append(JsonValue.Create(e.Kind).ToJsonString())
          .Append(",\"a\":").Append(e.Args.ToJsonString())
          .Append("}\n");

    /// <summary>The whole run as one object, for the upload, or null when nothing
    /// was recorded.</summary>
    public static JsonObject? Take()
    {
        if (Entries.Count == 0) return null;
        JsonArray log = [];
        foreach (Entry e in Entries)
        {
            log.Add(new JsonObject
            {
                ["q"] = e.Seq,
                ["f"] = e.Fight,
                ["t"] = e.Turn,
                ["k"] = e.Kind,
                ["a"] = e.Args.DeepClone()
            });
        }
        JsonObject run = new() { ["v"] = SchemaVersion, ["log"] = log, ["truncated"] = _truncated };
        if (RunHealth.Take(Entries.Count) is { } health) run["health"] = health;
        return run;
    }
}
