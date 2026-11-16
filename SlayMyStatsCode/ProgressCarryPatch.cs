using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// With any mod loaded the game switches to a separate save tree (steam\&lt;id&gt;\modded\profileN) that starts
/// empty, so a player who turns this mod on would lose their unlocks and the ascensions they had reached.
/// After the game loads that profile's progress, this lifts it to whatever the standard profile of the same
/// slot has: each character's highest ascension and the furthest state of each unlock (epoch).
///
/// It only ever raises values, never writes to the standard profile, and can run on every launch. A failure
/// is logged and leaves the game's own progress as loaded. Turn it off with "carry_progress": false in the
/// player's config.json.
/// </summary>
[HarmonyPatch(typeof(ProgressSaveManager), nameof(ProgressSaveManager.LoadProgress))]
public static class ProgressCarryPatch
{
    [HarmonyPostfix]
    public static void Postfix(ProgressSaveManager __instance)
    {
        MainFile.Logger.Info("Carry-over: progress loaded, checking the standard profile.");
        try
        {
            string userDir = Path.Combine(OS.GetUserDataDir(), MainFile.ModId);
            if (!ModConfig.CarryProgressEnabled(userDir, MainFile.ModDir()))
            {
                MainFile.Logger.Info("Carry-over is turned off in the config.");
                return;
            }

            int profile = Traverse.Create(__instance).Field("_profileIdProvider").GetValue<IProfileIdProvider>().CurrentProfileId;
            string moddedPath = ProgressSaveManager.GetProgressPathForProfile(profile, true);
            string standardPath = ProgressSaveManager.GetProgressPathForProfile(profile, false);
            if (moddedPath == standardPath) return;

            string standardFile = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null).PathJoin(standardPath));
            if (!File.Exists(standardFile))
            {
                MainFile.Logger.Info($"Carry-over: no standard progress at {standardFile}.");
                return;
            }
            JsonNode? standard = JsonNode.Parse(File.ReadAllText(standardFile));
            if (standard == null) return;

            ProgressState progress = __instance.Progress;
            int raised = 0, unlocked = 0;

            foreach (JsonNode? entry in standard["character_stats"]?.AsArray() ?? [])
            {
                string? id = entry?["id"]?.GetValue<string>();
                int max = entry?["max_ascension"]?.GetValue<int>() ?? 0;
                if (id == null || max <= 0) continue;

                CharacterStats stats = progress.GetOrCreateCharacterStats(ModelId.Deserialize(id));
                if (stats.MaxAscension >= max) continue;
                stats.MaxAscension = max;
                stats.PreferredAscension = entry?["preferred_ascension"]?.GetValue<int>() ?? max;
                raised++;
            }

            foreach (JsonNode? entry in standard["epochs"]?.AsArray() ?? [])
            {
                string? id = entry?["id"]?.GetValue<string>();
                EpochState state = Rank(entry?["state"]?.GetValue<string>());
                if (id == null || state == EpochState.None) continue;

                EpochState current = progress.Epochs.FirstOrDefault(e => e.Id == id)?.State ?? EpochState.None;
                if (current >= state) continue;
                progress.ObtainEpochOverride(id, state);
                unlocked++;
            }

            if (raised + unlocked == 0)
            {
                MainFile.Logger.Info($"Carry-over: nothing to add from standard profile {profile}.");
                return;
            }
            __instance.SaveProgress();
            MainFile.Logger.Info($"Carried over from the standard profile {profile}: {raised} character ascensions, {unlocked} unlocks.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Carrying progress over failed: {e}");
        }
    }

    private static EpochState Rank(string? state) => state switch
    {
        "not_obtained" => EpochState.NotObtained,
        "obtained_no_slot" => EpochState.ObtainedNoSlot,
        "obtained" => EpochState.Obtained,
        "revealed" => EpochState.Revealed,
        _ => EpochState.None,
    };
}
