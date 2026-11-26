using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// SPIKE: adds a "Slay My Stats" tab to the game's Settings screen, built from the game's own tab, label, divider
/// and tickbox pieces. Each tickbox shows and writes one key of the player's config.json; nothing here touches the
/// game's preferences.
/// </summary>
[HarmonyPatch(typeof(NSettingsTabManager), nameof(NSettingsTabManager._Ready))]
public static class SettingsTabPatch
{
    private static readonly (string Key, string Label, string Hint)[] Rows =
    [
        ("uploadsEnabled", "Upload runs",
            "When on, your runs are uploaded to Slay My Stats. Runs skipped while it's off are sent once it's back on."),
        ("showCardStats", "Card and relic stats",
            "When on, card and relic tips show pick rates and win rates."),
        ("showRunStrength", "Run strength",
            "When on, projects your current strength by floor vs winning runs on average."),
        ("showSynergyHints", "Synergy hints",
            "When on, card tips show what a card gives your deck or wants from it."),
    ];

    [HarmonyPostfix]
    public static void Postfix(NSettingsTabManager __instance)
    {
        NSettingsTab? tab = null;
        NSettingsPanel? panel = null;
        try
        {
            var tabs = (Dictionary<NSettingsTab, NSettingsPanel>)AccessTools
                .Field(typeof(NSettingsTabManager), "_tabs").GetValue(__instance)!;
            var switchTo = AccessTools.Method(typeof(NSettingsTabManager), "SwitchTabTo");
            NSettingsPanel general = tabs[__instance.GetNode<NSettingsTab>("General")];

            panel = BuildPanel(general);
            general.GetParent().AddChild(panel);

            tab = GD.Load<PackedScene>("res://scenes/screens/settings_tab.tscn").Instantiate<NSettingsTab>();
            tab.Name = "SlayMyStats";
            __instance.AddChild(tab);
            __instance.MoveChild(tab, __instance.GetNode("RightTabIcon").GetIndex());
            tab.SetLabel("Slay My Stats");
            tab.Connect(NSettingsTab.SignalName.Released,
                Callable.From<NSettingsTab>(selected => switchTo.Invoke(__instance, [selected])));
            tabs.Add(tab, panel);
            MainFile.Logger.Info("Settings tab added.");
        }
        catch (Exception e)
        {
            // Half a tab is worse than none.
            tab?.QueueFree();
            panel?.QueueFree();
            MainFile.Logger.Error($"Couldn't add the settings tab: {e}");
        }
    }

    private static NSettingsPanel BuildPanel(NSettingsPanel general)
    {
        Control generalContent = general.GetNode<Control>("VBoxContainer");
        NSettingsPanel panel = new()
        {
            Name = "SlayMyStatsSettings",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = general.AnchorLeft,
            AnchorRight = general.AnchorRight,
            OffsetLeft = general.OffsetLeft,
            OffsetRight = general.OffsetRight,
            OffsetTop = general.OffsetTop,
            OffsetBottom = general.OffsetBottom,
            GrowHorizontal = general.GrowHorizontal,
        };
        VBoxContainer content = new()
        {
            Name = "VBoxContainer",
            CustomMinimumSize = generalContent.CustomMinimumSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        content.AddThemeConstantOverride("separation", 8);
        content.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        panel.AddChild(content);

        PackedScene tickboxScene = GD.Load<PackedScene>("res://scenes/screens/settings_tickbox.tscn");
        Node label = generalContent.GetNode("Screenshake/Label");
        Node divider = generalContent.GetNode("ScreenshakeDivider");
        string path = Path.Combine(OS.GetUserDataDir(), MainFile.ModId, ModConfig.UserFileName);
        List<ModTickbox> boxes = [];
        foreach ((string key, string text, string hint) in Rows)
        {
            if (boxes.Count > 0) content.AddChild(divider.Duplicate());

            MarginContainer row = new() { Name = key, CustomMinimumSize = new Vector2(0, 64) };
            row.AddThemeConstantOverride("margin_left", 12);
            row.AddThemeConstantOverride("margin_right", 12);
            row.AddThemeConstantOverride("margin_top", 0);
            row.AddThemeConstantOverride("margin_bottom", 0);

            RichTextLabel name = (RichTextLabel)label.Duplicate();
            name.Text = text;
            row.AddChild(name);

            ModTickbox box = ModTickbox.Create(tickboxScene, path, key);
            row.AddChild(box);
            boxes.Add(box);
            AddHint(row, text, hint);
            content.AddChild(row);
        }

        // Each time the tab opens it shows what the file says now, hand edits included.
        panel.VisibilityChanged += () =>
        {
            if (panel.Visible) boxes.ForEach(box => box.Show(path));
        };
        return panel;
    }

    /// <summary>The game's own setting hint: hovering the row shows it beside the screen, as Fast Mode's does.</summary>
    private static void AddHint(Control row, string title, string description)
    {
        // A tip's title can only come from the game's text tables, so it is replaced on a tip made from one of them.
        object tip = new HoverTip(new LocString("settings_ui", "FASTMODE"), description);
        AccessTools.PropertySetter(typeof(HoverTip), nameof(HoverTip.Title)).Invoke(tip, [title]);
        row.MouseEntered += () => NHoverTipSet.CreateAndShow(row, (IHoverTip)tip)
            ?.SetGlobalPosition(NSettingsScreen.GetSettingTipPosition(row));
        row.MouseExited += () => NHoverTipSet.Remove(row);
    }
}
