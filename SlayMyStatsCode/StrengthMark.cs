using System.Globalization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// The run strength mark in the top bar, after the floor and boss icons: the site's up arrow, bullseye or down
/// arrow for the latest act fought in, with a tip giving the numbers behind it. Hidden before the first fight.
/// Once the run is won both show the whole run, with a double up arrow for OP.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
public static class StrengthMarkPatch
{
    // The site's --win, --gold, --loss and --bg-deep.
    private static readonly Color Win = new("5cba7d"), Mid = new("e0c468"), Loss = new("e05c5c"), Ink = new("0d0c1a");

    private const float Radius = 22;
    private const float TipTextWidth = 560;
    private const int TableColumnGap = 12;

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
            if (strength != null)
                Sign(mark, strength.Run?.Verdict ?? strength.Current.Verdict, strength.Run?.Op == true);
        };
        mark.MouseEntered += () =>
        {
            try
            {
                if (strength == null) return;
                NHoverTipSet? tips =
                    NHoverTipSet.CreateAndShow(mark, ModTip.Create("Slay My Stats",
                        Words(strength) + RecordWords(LiveRun.RecordsSoFar(strength.FightCount))));
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
    private static void Sign(Control mark, string verdict, bool op)
    {
        Vector2 middle = mark.Size / 2;
        if (op)
        {
            mark.DrawCircle(middle, Radius, Win, antialiased: true);
            Arrow(mark, middle + new Vector2(-0.40f * Radius, 0), 0.70f, 1);
            Arrow(mark, middle + new Vector2(0.40f * Radius, 0), 0.70f, 1);
            return;
        }
        Color disc = verdict == Ruler.Ahead ? Win : verdict == Ruler.Behind ? Loss : Mid;
        mark.DrawCircle(middle, Radius, disc, antialiased: true);
        if (verdict == Ruler.Meeting)
        {
            mark.DrawCircle(middle, Radius * 0.66f, Ink, antialiased: true);
            mark.DrawCircle(middle, Radius * 0.38f, disc, antialiased: true);
            mark.DrawCircle(middle, Radius * 0.18f, Ink, antialiased: true);
            return;
        }
        Arrow(mark, middle, 1, verdict == Ruler.Ahead ? 1 : -1);
    }

    /// <param name="up">1 points up, -1 down.</param>
    private static void Arrow(Control mark, Vector2 middle, float scale, float up)
    {
        float size = Radius * scale;
        Vector2[] arrow =
        [
            middle + new Vector2(0, -0.52f * size * up),
            middle + new Vector2(0.56f * size, 0.40f * size * up),
            middle + new Vector2(-0.56f * size, 0.40f * size * up),
        ];
        mark.DrawColoredPolygon(arrow, Ink);
        mark.DrawPolyline([.. arrow, arrow[0]], Ink, 1, antialiased: true);
    }

    /// <summary>The game's tip is too narrow for the fight table, so this one is widened.</summary>
    private static void Widen(NHoverTipSet tips)
    {
        Control tip = tips.GetNode("textHoverTipContainer").GetChild<Control>(0);
        RichTextLabel text = tip.GetNode<RichTextLabel>("%Description");
        text.AddThemeConstantOverride("table_h_separation", TableColumnGap);
        // RichTextLabel adds one more column gap after sizing the table to the available width.
        StyleBox style = (StyleBox)text.GetThemeStylebox("normal").Duplicate();
        style.ContentMarginRight = style.GetMargin(Side.Right) + TableColumnGap;
        text.AddThemeStyleboxOverride("normal", style);
        text.AutowrapMode = TextServer.AutowrapMode.Word;
        text.CustomMinimumSize = new Vector2(TipTextWidth, 0);
        tip.ResetSize();
    }

    private static string Words(Strength strength)
    {
        if (strength.Run is { } run) return RunWords(strength, run);
        ActReading act = strength.Current;
        List<string> cells = Header("Fight");
        foreach (FightLine f in strength.Fights)
        {
            cells.AddRange(Row(Name(f.Encounter), Blue(Whole(f.Taken)), Whole(f.WinTaken),
                Blue(f.Turns.ToString(CultureInfo.InvariantCulture)), Tenth(f.WinTurns)));
        }
        cells.AddRange(Sums("[b]Act total[/b]", act));

        IEnumerable<string> acts = strength.Earlier.Append(act).Select(a => $"Act {a.Act + 1}: {Word(a.Verdict)}");
        return $"{string.Join("\n", acts)}\n[table=5]{string.Concat(cells)}[/table]";
    }

    /// <summary>The tip of a won run: the run and each act, then their damage and turns.</summary>
    private static string RunWords(Strength strength, RunReading run)
    {
        List<ActReading> acts = [.. strength.Earlier, strength.Current];
        List<string> lines = [$"Run: {(run.Op ? "[green]OP[/green]" : Word(run.Verdict))}"];
        List<string> cells = Header("Act");
        foreach (ActReading act in acts)
        {
            lines.Add($"Act {act.Act + 1}: {Word(act.Verdict)}");
            cells.AddRange(Sums($"Act {act.Act + 1}", act));
        }
        cells.AddRange(Sums("[b]Run total[/b]", run.Totals));
        return $"{string.Join("\n", lines)}\n[table=5]{string.Concat(cells)}[/table]";
    }

    private static string RecordWords(CombatRecords? records)
    {
        if (records == null) return "";
        string hit = records.BiggestHit > 0 ? $"  •  Biggest hit: {Blue(Whole(records.BiggestHit))}" : "";
        return $"\nTotal damage: {Blue(Whole(records.Damage))}{hit}\n" +
            "[gold]Best single turns[/gold]\n" +
            $"Cards played: {Blue(Whole(records.MostCardsPlayed))}  •  Bonus energy gained: {Blue(Whole(records.MostBonusEnergy))}";
    }

    private static List<string> Sums(string name, Totals sums) =>
        Row(name, Blue(Whole(sums.Taken)), Whole(sums.WinTaken), Blue(Whole(sums.Turns)), Tenth(sums.WinTurns));

    // Keep the metric headings on one line so they don't compete with fight names for expansion.
    private static List<string> Header(string name) =>
        Row(name, "Dmg\u00a0taken", "Avg\u00a0win", "Turns", "Avg\u00a0win");

    /// <summary>Give the name column most of the extra width and keep the numbers right-aligned.</summary>
    private static List<string> Row(string name, params string[] numbers) =>
        [$"[cell=4]{name}[/cell]", .. numbers.Select(n => $"[cell][right]{n}[/right][/cell]")];

    private static string Blue(string value) => $"[blue]{value}[/blue]";

    private static string Word(string verdict) => verdict == Ruler.Ahead ? "[green]Hype[/green]"
        : verdict == Ruler.Behind ? "[red]Rough[/red]" : "[gold]Mid[/gold]";

    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Tenth(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Name(string encounter)
    {
        if (ModelDb.GetByIdOrNull<EncounterModel>(ModelId.Deserialize(encounter)) is not { } model) return encounter;
        string name = model.Title.GetFormattedText();
        MapPointType point = model.RoomType switch
        {
            RoomType.Elite => MapPointType.Elite,
            RoomType.Boss => MapPointType.Boss,
            _ => MapPointType.Monster,
        };
        string? icon = ImageHelper.GetRoomIconPath(point, model.RoomType, point == MapPointType.Boss ? model.Id : null);
        return string.IsNullOrEmpty(icon) || !ResourceLoader.Exists(icon) ? name : $"[img=20x20]{icon}[/img]\u00a0{name}";
    }
}
