using Godot;
using HarmonyLib;
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
    // Labels are placeholders until approved.
    private static readonly (string Key, string Label)[] Rows =
    [
        ("uploadsEnabled", "Upload runs"),
        ("showCardStats", "Card and relic stats"),
        ("showRunStrength", "Run strength"),
        ("showSynergyHints", "Synergy hints"),
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
        foreach ((string key, string text) in Rows)
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
            content.AddChild(row);
        }

        // Each time the tab opens it shows what the file says now, hand edits included.
        panel.VisibilityChanged += () =>
        {
            if (panel.Visible) boxes.ForEach(box => box.Show(path));
        };
        return panel;
    }
}
