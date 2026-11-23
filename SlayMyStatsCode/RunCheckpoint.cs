using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Steamworks;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Local recovery data for a run that can span multiple game sessions (the files are described on
/// <see cref="CheckpointFile"/>). Saving only takes what changed on the game's thread; the writing happens on
/// a background task, one write at a time and in order.
/// </summary>
internal static class RunCheckpoint
{
    private static readonly System.Reflection.FieldInfo? StartTime =
        AccessTools.Field(typeof(RunManager), "_startTime");

    internal static string Root => Path.Combine(OS.GetUserDataDir(), MainFile.ModId, "map-checkpoints");
    private static string? _path;
    private static string? _lastFight;
    private static bool _firstFight;
    private static Task _writes = Task.CompletedTask;
    private static int _writeFailed;
    /// <summary>The small file as last handed to a write, so a save that changes nothing writes nothing.</summary>
    private static string? _lastMeta;

    internal static void EnsureLoaded(long? runStart = null)
    {
        long start = runStart ?? (StartTime?.GetValue(RunManager.Instance) is long value ? value : 0);
        if (start <= 0 || MainFile.Uploaders.Count == 0) return;
        string path = Path.Combine(Root,
            SteamUser.GetSteamID().m_SteamID.ToString(), $"profile{SaveManager.Instance.CurrentProfileId}", $"{start}.json");
        if (path == _path) return;

        MapLog.Reset();
        EventLog.Reset();
        _path = path;
        _lastFight = null;
        _lastMeta = null;
        _firstFight = true;
        if (!File.Exists(path)) return;

        try
        {
            CheckpointFile.Saved saved = CheckpointFile.Read(path);
            MapLog.Restore(saved.Maps);
            EventLog.Restore(saved.Telemetry, saved.LogInOwnFile);
            _lastFight = saved.LastFight;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not load run checkpoint: {e}");
        }
    }

    internal static void FightStarted(IRunState state, string encounter)
    {
        try
        {
            EnsureLoaded();
            var coord = state.CurrentMapCoord;
            string key = $"{state.CurrentActIndex}:{state.TotalFloor}:{coord?.row}:{coord?.col}:{state.CurrentRoomCount}:{encounter}";
            if (_firstFight && _lastFight == key) EventLog.DropLastFight();
            _firstFight = false;
            _lastFight = key;
            if (state is RunState live && coord != null) MapLog.Visit(live);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not prepare combat checkpoint: {e}");
        }
    }

    internal static void Save(JsonArray? mods = null)
    {
        if (_path == null) return;
        try
        {
            // A write that failed may have left the log short, so the next one replaces it.
            (string lines, bool whole) = EventLog.TakeUnsaved(Interlocked.Exchange(ref _writeFailed, 0) == 1);
            string meta = new JsonObject
            {
                ["maps"] = MapLog.Take(),
                ["lastFight"] = _lastFight,
                ["truncated"] = EventLog.Truncated,
                ["mods"] = mods?.DeepClone()
            }.ToJsonString();
            if (lines.Length == 0 && !whole && meta == _lastMeta) return;
            _lastMeta = meta;
            string path = _path;
            _writes = _writes.ContinueWith(_ =>
            {
                try
                {
                    CheckpointFile.Write(path, meta, lines, whole);
                }
                catch (Exception e)
                {
                    Interlocked.Exchange(ref _writeFailed, 1);
                    MainFile.Logger.Error($"Could not write run checkpoint: {e}");
                }
            }, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not write run checkpoint: {e}");
        }
    }

    /// <summary>Stops saving to the run's checkpoint. Returns its path (when it is that run's) and the writes
    /// still under way, which must finish before the files are deleted.</summary>
    internal static (string? Path, Task Written) Detach(long start)
    {
        string? path = _path;
        _path = null;
        _lastFight = null;
        _lastMeta = null;
        return (Path.GetFileNameWithoutExtension(path) == start.ToString() ? path : null, _writes);
    }
}
