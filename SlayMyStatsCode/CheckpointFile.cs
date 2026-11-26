using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// A run's checkpoint on disk, so a run that is quit and continued later keeps its log. Two files, named by
/// the run's start time:
///
/// - <c>&lt;start&gt;.ndjson</c>, the event log, one event per line. It only ever grows: a save adds the
///   events since the last one to the end, so saving after a fight costs that fight, not the whole run.
/// - <c>&lt;start&gt;.json</c>, the maps, the mods and which fight came last. Small, so it is written whole.
///
/// Checkpoints from before the split keep the log inside the .json as "telemetry"; they still read, and the
/// next save moves the log out. No game types here, so the probe can link it.
/// </summary>
public static class CheckpointFile
{
    public sealed record Saved(JsonArray? Maps, JsonObject? Telemetry, string? LastFight, JsonArray? Mods, bool LogInOwnFile);

    public static string LogPath(string path) => Path.ChangeExtension(path, ".ndjson");

    public static Saved Read(string path)
    {
        JsonNode? meta = JsonNode.Parse(File.ReadAllText(path));
        string log = LogPath(path);
        bool own = File.Exists(log);
        JsonObject? telemetry = own
            ? ReadLog(log, (bool?)meta?["truncated"] ?? false)
            : meta?["telemetry"]?.DeepClone() as JsonObject;
        // Debug only: what the health checks had found, kept in the small file.
        if (telemetry != null && meta?["health"] is JsonObject health) telemetry["health"] = health.DeepClone();
        return new Saved(meta?["maps"]?.DeepClone() as JsonArray, telemetry, (string?)meta?["lastFight"],
            meta?["mods"]?.DeepClone() as JsonArray, own);
    }

    /// <summary>The log in the upload's shape. A line a crash cut short is skipped, as is any line that
    /// doesn't come after the one before it.</summary>
    private static JsonObject? ReadLog(string log, bool truncated)
    {
        JsonArray entries = [];
        long last = long.MinValue;
        foreach (string line in File.ReadLines(log))
        {
            if (line.Length == 0) continue;
            JsonObject? e;
            try { e = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (e?["q"] is not JsonValue q || !q.TryGetValue(out long seq) || seq <= last) continue;
            last = seq;
            entries.Add(e);
        }
        return entries.Count == 0 ? null : new JsonObject { ["v"] = EventLog.SchemaVersion, ["log"] = entries, ["truncated"] = truncated };
    }

    /// <summary>Adds <paramref name="lines"/> to the end of the log, or replaces it with them when
    /// <paramref name="whole"/>, then writes the small file. An empty log is no file at all.</summary>
    public static void Write(string path, string meta, string lines, bool whole)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string log = LogPath(path);
        if (whole && lines.Length == 0)
        {
            File.Delete(log);
        }
        else if (whole)
        {
            File.WriteAllText(log + ".tmp", lines);
            File.Move(log + ".tmp", log, overwrite: true);
        }
        else if (lines.Length > 0)
        {
            using FileStream f = new(log, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            // A write a crash cut short leaves a line with no end; start on a fresh line so it costs only itself.
            if (f.Length > 0)
            {
                f.Seek(-1, SeekOrigin.End);
                if (f.ReadByte() != '\n') f.WriteByte((byte)'\n');
            }
            f.Seek(0, SeekOrigin.End);
            f.Write(Encoding.UTF8.GetBytes(lines));
        }

        File.WriteAllText(path + ".tmp", meta);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Both files, once the run they hold is queued or sent.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(LogPath(path));
    }
}
