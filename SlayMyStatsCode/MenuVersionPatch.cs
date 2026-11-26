using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Puts the loaded copy's version after the mod's name in the main menu's "Mods loaded" popup, so a local
/// install and a Workshop one can be told apart. The popup is left alone when it lists errors instead.
/// </summary>
[HarmonyPatch(typeof(NDebugInfoLabelManager), "UpdateText")]
public static class MenuVersionPatch
{
    private const string Name = "Slay My Stats";

    [HarmonyPostfix]
    public static void Postfix(NDebugInfoLabelManager __instance)
    {
        try
        {
            if (!__instance.isMainMenu) return;
            if (__instance.GetNodeOrNull<MegaRichTextLabel>("%ModWarningLabel") is not { } label) return;

            string version = MainFile.Version(MainFile.ModDir());
            if (version == "unknown" || label.Text.Contains($"{Name} {version}")) return;
            label.Text = label.Text.Replace(Name, $"{Name} {version}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Couldn't show the version in the main menu: {e.Message}");
        }
    }
}
