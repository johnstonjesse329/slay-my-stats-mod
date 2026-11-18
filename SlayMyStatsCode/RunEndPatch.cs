using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// The game writes a run's history file exactly once, when the run ends (win, death or abandon), through
/// RunHistorySaveManager.SaveHistory. Running after it means the .run file is already on disk, and the same
/// text is what gets queued for upload.
/// </summary>
[HarmonyPatch(typeof(RunHistorySaveManager), nameof(RunHistorySaveManager.SaveHistory))]
public static class RunEndPatch
{
    [HarmonyPostfix]
    public static void Postfix(RunHistory history)
    {
        // A failure here must never reach the game's save code.
        try
        {
            if (MainFile.Uploaders.Count == 0) return;

            // Only turning the run into text happens here, on the game's thread, while the run can't change.
            // Writing it to the queue and sending it are left to a background task.
            string json = JsonSerializationUtility.ToJson(history);
            JsonArray mods = LoadedMods();
            JsonObject? telemetry = EventLog.Take();
            JsonArray? maps = MapLog.Take();
            MainFile.Logger.Info($"Run ended: {history.StartTime}.run win={history.Win} abandoned={history.WasAbandoned}. Queuing it for upload.");

            string steamDir = Path.Combine(Godot.OS.GetUserDataDir(), "steam");
            long start = history.StartTime;
            foreach (Uploader uploader in MainFile.Uploaders)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // The game has just written the history file; its folder says which profile played it.
                        string? profile = ProfileOf(steamDir, start);
                        if (!uploader.Wants(profile))
                        {
                            MainFile.Logger.Info($"Run {start} (profile {profile ?? "unknown"}) isn't on the config's profile list; not uploading it.");
                            return;
                        }
                        uploader.Enqueue(json, mods, telemetry, maps, profile);
                        await uploader.DrainAsync();
                    }
                    catch (Exception e)
                    {
                        MainFile.Logger.Error($"Couldn't queue the run: {e}");
                    }
                });
            }
            MapLog.Reset();
            EventLog.Reset();       // the next run starts with an empty log
            EventHooks.Reset();     // and a hook failure is retried on the next one
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Run-end hook failed: {e}");
        }
    }

    /// <summary>The profile ("profile1") whose history folder holds <paramref name="start"/>.run, standard or modded.</summary>
    private static string? ProfileOf(string steamDir, long start)
    {
        try
        {
            foreach (string file in Directory.GetFiles(steamDir, $"{start}.run", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(Path.GetDirectoryName(file)) != "history") continue;
                // …\<profile>\saves\history\<start>.run
                string? profile = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file))));
                if (profile != null && profile.StartsWith("profile", StringComparison.OrdinalIgnoreCase)) return profile;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Couldn't tell which profile run {start} was played on: {e.Message}");
        }

        return null;
    }

    /// <summary>
    /// Every mod the game has loaded, this one included, as its manifest describes it. The site shows the run
    /// as modded and lists these; "affects_gameplay" is each mod's own word for whether it changes the game.
    /// </summary>
    private static JsonArray LoadedMods()
    {
        JsonArray mods = [];
        foreach (Mod mod in ModManager.GetLoadedMods())
        {
            ModManifest? manifest = mod.manifest;
            if (manifest?.id == null) continue;

            mods.Add(new JsonObject
            {
                ["id"] = manifest.id,
                ["name"] = manifest.name,
                ["version"] = manifest.version,
                ["affects_gameplay"] = manifest.affectsGameplay,
            });
        }

        return mods;
    }
}
