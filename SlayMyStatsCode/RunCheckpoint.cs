using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Steamworks;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>Local recovery data for a run that can span multiple game sessions.</summary>
internal static class RunCheckpoint
{
    private static readonly System.Reflection.FieldInfo? StartTime =
        AccessTools.Field(typeof(RunManager), "_startTime");

    internal static string Root => Path.Combine(OS.GetUserDataDir(), MainFile.ModId, "map-checkpoints");
    private static string? _path;
    private static string? _lastFight;
    private static bool _firstFight;

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
        _firstFight = true;
        if (!File.Exists(path)) return;

        try
        {
            var saved = JsonNode.Parse(File.ReadAllText(path));
            MapLog.Restore(saved?["maps"] as JsonArray);
            EventLog.Restore(saved?["telemetry"] as JsonObject);
            _lastFight = (string?)saved?["lastFight"];
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
            var saved = new JsonObject
            {
                ["maps"] = MapLog.Take(),
                ["telemetry"] = EventLog.Take(),
                ["lastFight"] = _lastFight,
                ["mods"] = mods?.DeepClone()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", saved.ToJsonString());
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not write run checkpoint: {e}");
        }
    }

    internal static string? Detach(long start)
    {
        string? path = _path;
        _path = null;
        _lastFight = null;
        return Path.GetFileNameWithoutExtension(path) == start.ToString() ? path : null;
    }
}
