using System.Globalization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// The run strength mark in the top bar, after the floor and boss icons: the site's up arrow, bullseye or down
/// arrow for the current act, with a tip giving the numbers behind it. Hidden until the act can be read.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
public static class StrengthMarkPatch
{
    // The site's --win, --gold, --loss and --bg-deep.
    private static readonly Color Win = new("5cba7d"), Mid = new("e0c468"), Loss = new("e05c5c"), Ink = new("0d0c1a");

    private const float Radius = 22;
    private const float TipTextWidth = 560;

    [HarmonyPostfix]
    public static void Postfix(NTopBar __instance)
    {
        try
        {
            Add(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Stats: couldn't add the run strength mark: {e}");
        }
    }

    private static void Add(NTopBar bar)
    {
        Control mark = new()
        {
            Name = "SlayMyStatsStrength",
            CustomMinimumSize = new Vector2(Radius * 2 + 12, 80),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        Strength? strength = null;

        void Refresh()
        {
            try
            {
                strength = LiveRun.Strength();
                mark.Visible = strength != null;
                if (strength == null) NHoverTipSet.Remove(mark);
                mark.QueueRedraw();
            }
            catch (Exception e)
            {
                mark.Visible = false;
                MainFile.Logger.Error($"Stats: couldn't rate the run: {e}");
            }
        }

        mark.Draw += () =>
        {
            if (strength != null) Sign(mark, strength.Current.Verdict);
        };
        mark.MouseEntered += () =>
        {
            try
            {
                if (strength == null) return;
                NHoverTipSet? tips =
                    NHoverTipSet.CreateAndShow(mark, ModTip.Create("Slay My Stats", Words(strength)));
                if (tips == null) return;
                Widen(tips);
                tips.SetGlobalPosition(mark.GlobalPosition + new Vector2(0, mark.Size.Y + 20));
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"Stats: couldn't show the run strength tip: {e}");
            }
        };
        mark.MouseExited += () => NHoverTipSet.Remove(mark);

        Action<CombatRoom> won = _ => Refresh();
        Action entered = Refresh;
        CombatManager.Instance.CombatWon += won;
        RunManager.Instance.RoomEntered += entered;
        LiveRun.SettingsChanged += entered;
        mark.TreeExiting += () =>
        {
            CombatManager.Instance.CombatWon -= won;
            RunManager.Instance.RoomEntered -= entered;
            LiveRun.SettingsChanged -= entered;
        };

        bar.LeftContainer.AddChild(mark);
        bar.LeftContainer.MoveChild(mark, bar.RoomIconsContainer.GetIndex() + 1);
        // A loaded run isn't complete until after the top bar is set up.
        Callable.From(Refresh).CallDeferred();
    }

    /// <summary>The site's mark: a coloured disc with an up arrow, a bullseye or a down arrow cut into it.</summary>
    private static void Sign(Control mark, string verdict)
    {
        Vector2 middle = mark.Size / 2;
        Color disc = verdict == Ruler.Ahead ? Win : verdict == Ruler.Behind ? Loss : Mid;
        mark.DrawCircle(middle, Radius, disc, antialiased: true);
        if (verdict == Ruler.Meeting)
        {
            mark.DrawCircle(middle, Radius * 0.66f, Ink, antialiased: true);
            mark.DrawCircle(middle, Radius * 0.38f, disc, antialiased: true);
            mark.DrawCircle(middle, Radius * 0.18f, Ink, antialiased: true);
            return;
        }
        float up = verdict == Ruler.Ahead ? 1 : -1;
        Vector2[] arrow =
        [
            middle + new Vector2(0, -0.52f * Radius * up),
            middle + new Vector2(0.56f * Radius, 0.40f * Radius * up),
            middle + new Vector2(-0.56f * Radius, 0.40f * Radius * up),
        ];
        mark.DrawColoredPolygon(arrow, Ink);
        mark.DrawPolyline([.. arrow, arrow[0]], Ink, 1, antialiased: true);
    }

    /// <summary>The game's tip is too narrow for the fight table, so this one is widened.</summary>
    private static void Widen(NHoverTipSet tips)
    {
        Control tip = tips.GetNode("textHoverTipContainer").GetChild<Control>(0);
        RichTextLabel text = tip.GetNode<RichTextLabel>("%Description");
        text.AddThemeConstantOverride("table_h_separation", 18);
        text.CustomMinimumSize = new Vector2(TipTextWidth, 0);
        tip.ResetSize();
    }

    private static string Words(Strength strength)
    {
        ActReading act = strength.Current;
        List<string> cells = Row("Fight", "Damage", "Avg win", "Turns", "Avg win");
        foreach (FightLine f in strength.Fights)
        {
            cells.AddRange(Row(Name(f.Encounter), Blue(Whole(f.Taken)), Whole(f.WinTaken),
                Blue(f.Turns.ToString(CultureInfo.InvariantCulture)), Tenth(f.WinTurns)));
        }
        cells.AddRange(Row("[b]Act total[/b]", Blue(Whole(act.Taken)), Whole(act.WinTaken),
            Blue(Whole(act.Turns)), Tenth(act.WinTurns)));

        string words = $"Act {act.Act + 1}: {Word(act.Verdict)}
[table=5]{string.Concat(cells)}[/table]";
        if (strength.Earlier.Count > 0)
        {
            words += "

" + string.Join("
", strength.Earlier.Select(a => $"Act {a.Act + 1}: {Word(a.Verdict)}"));
        }
        return words;
    }

    /// <summary>One table row: the name on the left, the numbers on the right.</summary>
    private static List<string> Row(string name, params string[] numbers) =>
        [$"[cell]{name}[/cell]", .. numbers.Select(n => $"[cell][right]{n}[/right][/cell]")];

    private static string Blue(string value) => $"[blue]{value}[/blue]";

    private static string Word(string verdict) => verdict == Ruler.Ahead ? "[green]Hype[/green]"
        : verdict == Ruler.Behind ? "[red]Rough[/red]" : "[gold]Mid[/gold]";

    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Tenth(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Name(string encounter) =>
        ModelDb.GetByIdOrNull<EncounterModel>(ModelId.Deserialize(encounter))?.Title.GetFormattedText() ?? encounter;
}
