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
/// After the game loads that profile's progress, this lifts it to the best of all the standard profiles on the
/// account: each character's highest ascension and the furthest state of each unlock (epoch).
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
            string accountDir = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null));
            string[] standardFiles = Directory.Exists(accountDir)
                ? Directory.GetDirectories(accountDir, "profile*")
                    .Select(dir => Path.Combine(dir, "saves", "progress.save")).Where(File.Exists).ToArray()
                : [];
            if (standardFiles.Length == 0)
            {
                MainFile.Logger.Info($"Carry-over: no standard progress under {accountDir}.");
                return;
            }

            ProgressState progress = __instance.Progress;
            int raised = 0, unlocked = 0;

            // Every standard profile counts, whichever slot the modded profile is: the aim is that turning
            // a mod on never costs the player an ascension or an unlock.
            foreach (string file in standardFiles)
            {
                JsonNode? standard = JsonNode.Parse(File.ReadAllText(file));
                if (standard == null) continue;

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
            }

            if (raised + unlocked == 0)
            {
                MainFile.Logger.Info($"Carry-over: nothing to add from {standardFiles.Length} standard profiles.");
                return;
            }
            __instance.SaveProgress();
            MainFile.Logger.Info($"Carried over from {standardFiles.Length} standard profiles into modded profile {profile}: {raised} character ascensions, {unlocked} unlocks.");
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
