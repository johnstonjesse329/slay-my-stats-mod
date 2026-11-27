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
/// arrow for the latest act fought in, with a tip giving the numbers behind it. Neutral until stats are available.
/// Once the run is won both show the whole run, with a double up arrow for OP.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
public static class StrengthMarkPatch
{
    // The site's --win, --gold, --loss and --bg-deep.
    private static readonly Color Win = new("5cba7d"), Mid = new("e0c468"), Loss = new("e05c5c"), Ink = new("0d0c1a");
    private static readonly Color Pending = new("a5b2ba");

    private const float Radius = 22;
    private const float TipTextWidth = 660;
    private const float TipScreenMargin = 16;
    private const int TableColumnGap = 26;
    private const string ComparisonLegend = "[color=#c1c8cc][font_size=16]Each pair: you / avg win[/font_size][/color]";
    private const string SectionGap = "\n[font_size=8]\u00a0[/font_size]\n";

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
                bool hadStats = strength != null;
                mark.Visible = LiveRun.Settings?.RunStrength == true &&
                    RunManager.Instance.DebugOnlyGetState() != null;
                strength = LiveRun.Strength();
                if (!mark.Visible || hadStats != (strength != null)) NHoverTipSet.Remove(mark);
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
            else
            {
                Vector2 middle = mark.Size / 2;
                mark.DrawCircle(middle, Radius, Pending, antialiased: true);
                mark.DrawLine(middle - new Vector2(8, 0), middle + new Vector2(8, 0), Ink, 4, antialiased: true);
            }
        };
        mark.MouseEntered += () =>
        {
            try
            {
                string description = strength == null ? "No fight data yet."
                    : Words(strength) + RecordWords(LiveRun.RecordsSoFar(strength.FightCount)) +
                      ChoiceWords(LiveRun.ChoicesSoFar());
                NHoverTipSet? tips =
                    NHoverTipSet.CreateAndShow(mark, ModTip.Create("Slay My Stats", description));
                if (tips == null) return;
                SizeAndPlace(tips, mark, strength == null ? 280 : TipTextWidth);
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"Stats: couldn't show the run strength tip: {e}");
            }
        };
        mark.MouseExited += () => NHoverTipSet.Remove(mark);
        mark.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
            mark.AcceptEvent();
            NHoverTipSet.Remove(mark);
            ModFeedback.Open(mark, fromRun: true, lastEncounter: strength?.Fights.LastOrDefault()?.Encounter);
        };

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

    /// <summary>Give the table room and shift the tooltip left when it would run off the right edge.</summary>
    private static void SizeAndPlace(NHoverTipSet tips, Control mark, float preferredWidth)
    {
        Control tip = tips.GetNode("textHoverTipContainer").GetChild<Control>(0);
        RichTextLabel text = tip.GetNode<RichTextLabel>("%Description");
        text.AddThemeConstantOverride("table_h_separation", TableColumnGap);
        // RichTextLabel adds one more column gap after sizing the table to the available width.
        StyleBox style = (StyleBox)text.GetThemeStylebox("normal").Duplicate();
        style.ContentMarginRight = style.GetMargin(Side.Right) + TableColumnGap;
        text.AddThemeStyleboxOverride("normal", style);
        text.AutowrapMode = TextServer.AutowrapMode.Word;
        Rect2 viewport = mark.GetViewportRect();
        float boxPadding = Math.Max(0, tip.GetCombinedMinimumSize().X - text.GetCombinedMinimumSize().X);
        float available = viewport.Size.X - 2 * TipScreenMargin;
        float textWidth = Math.Clamp(available - boxPadding, 0, preferredWidth);
        text.CustomMinimumSize = new Vector2(textWidth, 0);
        tip.ResetSize();
        float tipWidth = Math.Max(tip.Size.X, textWidth + boxPadding);
        float left = viewport.Position.X + TipScreenMargin;
        float right = Math.Max(left, viewport.End.X - TipScreenMargin - tipWidth);
        tips.SetGlobalPosition(new Vector2(Math.Clamp(mark.GlobalPosition.X, left, right),
            mark.GlobalPosition.Y + mark.Size.Y + 20));
    }

    private static string Words(Strength strength)
    {
        if (strength.Run is { } run) return RunWords(strength, run);
        ActReading act = strength.Current;
        List<string> cells = Header("Fight");
        foreach (FightLine f in strength.Fights)
        {
            cells.AddRange(ComparisonRow(Name(f.Encounter), f.Taken, f.WinTaken, f.Turns, f.WinTurns));
        }
        cells.AddRange(TotalRow("Act total", act));

        IEnumerable<string> acts = strength.Earlier.Append(act).Select(a => $"Act {a.Act + 1}: {Word(a.Verdict)}");
        return $"{string.Join("\n", acts)}\n{ComparisonLegend}\n[table=3]{string.Concat(cells)}[/table]";
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
        cells.AddRange(TotalRow("Run total", run.Totals));
        return $"{string.Join("\n", lines)}\n{ComparisonLegend}\n[table=3]{string.Concat(cells)}[/table]";
    }

    private static string RecordWords(CombatRecords? records)
    {
        if (records == null) return "";
        string hit = records.BiggestHit > 0 ? $" · Max card dmg {Blue(Whole(records.BiggestHit))}" : "";
        return SectionGap + "[gold]Run damage[/gold]\n" +
            $"Total dmg {Blue(Whole(records.Damage))}{hit}" +
            SectionGap + "[gold]Turn bests[/gold]\n" +
            $"Dmg {Blue(Whole(records.MaxTurnDamage))} · Plays {Blue(Whole(records.MostCardsPlayed))} · " +
            $"Draws {Blue(Whole(records.MostCardsDrawn))} · Block {Blue(Whole(records.MostBlockGained))} · " +
            $"Energy +{Blue(Whole(records.MostBonusEnergy))}";
    }

    private static List<string> Sums(string name, Totals sums) =>
        ComparisonRow(name, sums.Taken, sums.WinTaken, sums.Turns, sums.WinTurns);

    private static List<string> TotalRow(string name, Totals sums) =>
        Row(TotalText($"[gold]{name}[/gold]"), TotalText(Comparison(sums.Taken, sums.WinTaken)),
            TotalText(Comparison(sums.Turns, sums.WinTurns)));

    private static string TotalText(string value) => $"[b][font_size=24]{value}[/font_size][/b]";

    private static string ChoiceWords(RunChoices? choices) => choices == null ? "" :
        SectionGap + $"[gold]Run choices[/gold]\nElites {Blue(Whole(choices.Elites))} · " +
        $"Rest {Blue(Whole(choices.Rested))} · Smith {Blue(Whole(choices.Smithed))} · " +
        $"Remove {Blue(Whole(choices.Removed))} · Rewards skipped {Blue(Whole(choices.SkippedRewardFloors))}";

    private static List<string> ComparisonRow(string name, double taken, double avgTaken, double turns, double avgTurns) =>
        Row(name, Comparison(taken, avgTaken), Comparison(turns, avgTurns));

    // Compare the displayed amounts so equally displayed values stay neutral.
    private static string Comparison(double actual, double average)
    {
        double value = Math.Round(actual, MidpointRounding.AwayFromZero);
        double reference = Math.Round(average, MidpointRounding.AwayFromZero);
        double delta = value - reference;
        string player = Whole(value);
        string difference = "";
        if (delta != 0)
        {
            string color = delta < 0 ? "green" : "red";
            string sign = delta < 0 ? "−" : "+";
            player = $"[{color}]{player}[/{color}]";
            difference = $"\u00a0[{color}]({sign}{Whole(Math.Abs(delta))})[/{color}]";
        }
        return $"{player}\u00a0/\u00a0{Whole(reference)}{difference}";
    }

    private static List<string> Header(string name) =>
        Row(name, "Dmg\u00a0taken", "Turns");

    /// <summary>Give the name column most of the extra width and keep the numbers right-aligned.</summary>
    private static List<string> Row(string name, params string[] numbers) =>
        [$"[cell=4]{name}[/cell]", .. numbers.Select(n => $"[cell][right]{n}[/right][/cell]")];

    private static string Blue(string value) => $"[blue]{value}[/blue]";

    private static string Word(string verdict) => verdict == Ruler.Ahead ? "[green]Hype[/green]"
        : verdict == Ruler.Behind ? "[red]Rough[/red]" : "[gold]Mid[/gold]";

    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

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
