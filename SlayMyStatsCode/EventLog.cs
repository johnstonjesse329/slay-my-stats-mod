using System.Text;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

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
        if (Entries.Count == 0) return;
        _saved = inFile ? Entries.Count : 0;
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

    /// <summary>Called when a hook itself fails: the log stops for this run rather
    /// than repeating the failure every event, and the game never sees it.</summary>
    public static void Fail() => _broken = true;

    public static void Reset()
    {
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
        return new JsonObject { ["v"] = SchemaVersion, ["log"] = log, ["truncated"] = _truncated };
    }
}
