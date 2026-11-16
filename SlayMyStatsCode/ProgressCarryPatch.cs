using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Validation;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// With any mod loaded the game switches to a separate save tree (steam\&lt;id&gt;\modded\profileN) that starts
/// empty, so a player who turns this mod on would lose their unlocks and the ascensions they had reached.
/// After the game loads that profile's progress, this lifts it to the standard profile with the same number: each
/// character's highest ascension, its win and loss counts when the profile has none (some unlocks depend on
/// having played) and the furthest state of each unlock (epoch).
///
/// It only ever raises values, copies the modded profile's progress file to a dated backup before changing it,
/// never writes to the standard profile, and can run on every launch. A failure
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
            // Slot N of the standard profiles is slot N of the modded ones: the game picks both by the same
            // number, so each person's modded profile is lifted by their own standard progress and nobody
            // else's.
            string standardPath = ProgressSaveManager.GetProgressPathForProfile(profile, false);
            string standardFile = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null).PathJoin(standardPath));
            if (ProgressSaveManager.GetProgressPathForProfile(profile, true) == standardPath || !File.Exists(standardFile))
            {
                MainFile.Logger.Info($"Carry-over: no standard progress for profile {profile} at {standardFile}.");
                return;
            }

            string[] standardFiles = [standardFile];
            // A dated copy of the profile as it is on disk, taken before the merge touches anything (the game
            // saves the merged state on its own from then on). If the copy can't be made, nothing is merged
            // this launch; if the merge turns out to change nothing, the copy is removed again.
            string? copy = null;
            string moddedFile = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null)
                .PathJoin(ProgressSaveManager.GetProgressPathForProfile(profile, true)));
            if (File.Exists(moddedFile))
            {
                string backups = Path.Combine(userDir, "backups", $"modded-profile{profile}");
                Directory.CreateDirectory(backups);
                copy = Path.Combine(backups, $"progress-{DateTime.Now:yyyyMMdd-HHmmss}.save");
                File.Copy(moddedFile, copy);
            }

            ProgressState progress = __instance.Progress;
            int raised = 0, unlocked = 0;

            // The aim is that turning a mod on never costs the player an ascension or an unlock.
            foreach (string file in standardFiles)
            {
                JsonNode? standard = JsonNode.Parse(File.ReadAllText(file));
                if (standard == null) continue;

                foreach (JsonNode? entry in standard["character_stats"]?.AsArray() ?? [])
                {
                    string? id = entry?["id"]?.GetValue<string>();
                    if (id == null) continue;

                    int max = entry?["max_ascension"]?.GetValue<int>() ?? 0;
                    int wins = entry?["total_wins"]?.GetValue<int>() ?? 0;
                    int losses = entry?["total_losses"]?.GetValue<int>() ?? 0;
                    if (max <= 0 && wins <= 0 && losses <= 0) continue;

                    CharacterStats stats = progress.GetOrCreateCharacterStats(ModelId.Deserialize(id));
                    bool changed = false;
                    if (stats.MaxAscension < max)
                    {
                        // The player's chosen ascension stays; a character the profile hasn't touched takes
                        // the standard one.
                        if (stats.MaxAscension == 0 && stats.PreferredAscension == 0)
                            stats.PreferredAscension = entry?["preferred_ascension"]?.GetValue<int>() ?? max;
                        stats.MaxAscension = max;
                        changed = true;
                    }

                    // Only a character with no runs on record here takes the standard counts. Some unlocks
                    // depend on having played (the game opens the Fool once Defect has a run), but counts a
                    // profile has already built up are its own and are left alone.
                    if (stats.TotalWins + stats.TotalLosses == 0 && wins + losses > 0)
                    {
                        stats.TotalWins = wins;
                        stats.TotalLosses = losses;
                        changed = true;
                    }
                    if (changed) raised++;
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
            }

            if (raised + unlocked == 0)
            {
                if (copy != null) File.Delete(copy);
                MainFile.Logger.Info($"Carry-over: nothing to add from standard profile {profile}.");
                return;
            }
            // The game closes gaps in its own timeline when it loads a save: an unlock whose parent is
            // revealed gets its slot. Carried epochs need the same, or the Fool is earned with no slot to
            // reveal it in. Loading the merged state back through the game's own code does exactly that.
            __instance.Progress = ProgressState.FromSerializable(progress.ToSerializable(), new DeserializationContext());
            __instance.SaveProgress();
            if (copy != null) MainFile.Logger.Info($"Carry-over: the profile before the change is kept at {copy}.");
            MainFile.Logger.Info($"Carried over from standard profile {profile} into modded profile {profile}: {raised} characters raised, {unlocked} unlocks.");
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
