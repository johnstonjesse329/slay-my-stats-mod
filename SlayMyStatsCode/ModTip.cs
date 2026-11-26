using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>The game's hover tip with the mod's own words in it.</summary>
public static class ModTip
{
    public static IHoverTip Create(string title, string description)
    {
        // A tip's title can only come from the game's text tables, so it is replaced on a tip made from one of them.
        object tip = new HoverTip(new LocString("settings_ui", "FASTMODE"), description);
        AccessTools.PropertySetter(typeof(HoverTip), nameof(HoverTip.Title)).Invoke(tip, [title]);
        return (IHoverTip)tip;
    }
}
