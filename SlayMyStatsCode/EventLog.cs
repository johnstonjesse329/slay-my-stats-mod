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
    private static long _seq;
    private static int _fight = -1;
    private static int _turn;
    private static bool _truncated;
    private static bool _broken;

    public static bool HasEvents => Entries.Count > 0;

    public static int Count => Entries.Count;

    /// <summary>The game's own turn number, stamped on every event so an event that
    /// arrives outside any call that names a turn still says which one it was in.</summary>
    public static void SetTurn(int turn)
    {
        if (turn > 0) _turn = turn;
    }

    public static void NewFight() => _fight++;

    public static void Add(string kind, JsonObject? args = null)
    {
        if (_broken || _truncated) return;
        if (Entries.Count >= MaxEvents)
        {
            _truncated = true;
            return;
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
    }

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

    /// <summary>The run so far, written over itself after every fight: one fight of
    /// play is then enough to check the recording, without finishing the run.</summary>
    public static void WriteDebug(string path)
    {
        if (Entries.Count == 0) return;
        try
        {
            StringBuilder sb = new();
            foreach (Entry e in Entries)
            {
                sb.Append("{\"q\":").Append(e.Seq)
                  .Append(",\"f\":").Append(e.Fight)
                  .Append(",\"t\":").Append(e.Turn)
                  .Append(",\"k\":\"").Append(e.Kind)
                  .Append("\",\"a\":").Append(e.Args.ToJsonString())
                  .AppendLine("}");
            }
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // A debug file that cannot be written is not worth a game error.
        }
    }
}
