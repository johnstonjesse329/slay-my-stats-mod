using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// When the upload config can't be read (see <see cref="UploadSettings"/>), says so on the main menu: a single
/// line of red text along the bottom, added to the menu as it opens. It adds nothing when the config is fine, and
/// the menu is not otherwise touched.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class ConfigNoticePatch
{
    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        try
        {
            if (UploadSettings.Notice is not { } notice) return;

            Label label = new()
            {
                Name = "SlayMyStatsConfigNotice",
                Text = notice,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.4f));
            label.AddThemeFontSizeOverride("font_size", 20);
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
            label.OffsetTop = -40;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            __instance.AddChild(label);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Couldn't show the config notice: {e.Message}");
        }
    }
}
