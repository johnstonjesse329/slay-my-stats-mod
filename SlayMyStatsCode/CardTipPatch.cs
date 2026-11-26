using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Adds a tip with a card's or relic's win and pick rate, and how it connects to the player's deck and relics, to
/// the game's own tips, during a run, wherever one is chosen or looked through: rewards, shop, relic choices and
/// card grids such as the deck view, and on owned relics in the top bar and the relic inspect screen. Cards in
/// hand are left alone. With no run, the compendium's card library, relic collection and relic inspect screen
/// get the rates alone, solo and multiplayer both.
/// </summary>
[HarmonyPatch(typeof(NHoverTipSet), "Init")]
public static class CardTipPatch
{
    private const int MaxNames = 3;

    [HarmonyPrefix]
    public static void Prefix(Control owner, ref IEnumerable<IHoverTip> hoverTips)
    {
        try
        {
            if (Collected(owner) is { } collected)
            {
                if (CompendiumWords(collected) is { } words)
                    hoverTips = hoverTips.Append(ModTip.Create("Slay My Stats", words)).ToList();
                return;
            }
            if (!RunManager.Instance.IsInProgress || Offered(owner) is not { } model) return;
            if (LiveRun.Settings is not { } settings || LiveRun.Pack is not { } pack) return;
            List<string> blocks = [];
            bool multiplayer = RunManager.Instance.DebugOnlyGetState()?.Players.Count > 1;
            if (settings.CardStats &&
                Rates.Of(pack.Data, multiplayer, model.Id.ToString(), model is RelicModel) is { } rates)
                blocks.Add(Words(rates));
            if (settings.SynergyHints && pack.Synergy is { } table && LiveRun.Synergy(table, model) is { } synergy)
                blocks.Add(Words(synergy));
            if (blocks.Count == 0) return;
            hoverTips = hoverTips.Append(ModTip.Create("Slay My Stats", string.Join("\n\n", blocks))).ToList();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Stats: couldn't add a card or relic tip: {e}");
        }
    }

    /// <summary>The card or relic a tip's owner is showing in the compendium. A relic not yet unlocked or seen
    /// stays a mystery; the game makes no tip for such a card.</summary>
    private static AbstractModel? Collected(Control owner) => owner switch
    {
        NGridCardHolder holder when InCardLibrary(holder) => holder.CardModel,
        NRelicCollectionEntry { ModelVisibility: ModelVisibility.Visible } entry => entry.relic,
        NInspectRelicScreen screen when !RunManager.Instance.IsInProgress => Inspected(screen),
        _ => null,
    };

    private static bool InCardLibrary(Node node)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent is NCardLibraryGrid) return true;
        return false;
    }

    /// <summary>With no run to say which applies, both. Null when there is nothing to show.</summary>
    private static string? CompendiumWords(AbstractModel model)
    {
        if (LiveRun.Settings is not { CardStats: true } || LiveRun.LatestPack is not { } pack) return null;
        List<string> blocks = [];
        foreach ((string heading, bool multiplayer) in new[] { ("Solo", false), ("Multiplayer", true) })
        {
            if (Rates.Of(pack.Data, multiplayer, model.Id.ToString(), model is RelicModel) is { } rates)
                blocks.Add($"[gold]{heading}[/gold]\n{Words(rates)}");
        }
        return blocks.Count == 0 ? null : string.Join("\n\n", blocks);
    }

    /// <summary>The card or relic a tip's owner is showing, for the owners that get stats during a run.</summary>
    private static AbstractModel? Offered(Control owner) => owner switch
    {
        NGridCardHolder holder => holder.CardModel,
        NMerchantCard card => (card.Entry as MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry)?.CreationResult?.Card,
        NMerchantRelic relic => (relic.Entry as MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry)?.Model,
        NRelicBasicHolder holder => holder.Relic?.Model,
        NRelicInventoryHolder holder => holder.Relic?.Model,
        NInspectRelicScreen screen => Inspected(screen),
        NTreasureRoomRelicHolder holder => holder.Relic?.Model,
        NRewardButton { Reward: RelicReward reward } => reward.Relic,
        NEventOptionButton button => button.Option?.Relic,
        _ => null,
    };

    /// <summary>The relic the inspect screen is on.</summary>
    private static AbstractModel? Inspected(NInspectRelicScreen screen)
    {
        var relics = Traverse.Create(screen).Field("_relics").GetValue<IReadOnlyList<RelicModel>>();
        int index = Traverse.Create(screen).Field("_index").GetValue<int>();
        return relics != null && index >= 0 && index < relics.Count ? relics[index] : null;
    }

    private static string Words(Rates rates)
    {
        List<string> lines = [];
        if (rates.Win is { } win) lines.Add($"Win rate: [blue]{win}%[/blue]");
        if (rates.Pick is { } pick) lines.Add($"Pick rate: [blue]{pick}%[/blue]");
        return string.Join("\n", lines);
    }

    private static string Words(Synergy synergy)
    {
        List<string> lines =
        [
            .. synergy.Wants.Select(l => $"Wants {l.Key} from: {Names(l.Names)}"),
            .. synergy.Gives.Select(l => $"Gives {l.Key} to: {Names(l.Names)}"),
        ];
        lines.AddRange(synergy.Archetypes.Select(InDeck));
        return string.Join("\n", lines);
    }

    /// <summary>"Shiv cards in your deck: 3 (Accuracy, Blade Dance x2)".</summary>
    private static string InDeck(SynergyLink archetype)
    {
        string line = $"{archetype.Key} cards in your deck: ";
        if (archetype.Names.Count == 0) return line + "none";
        List<string> names = archetype.Names.GroupBy(n => n)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}").ToList();
        return $"{line}{archetype.Names.Count} ({Names(names)})";
    }

    private static string Names(IReadOnlyList<string> names) => names.Count <= MaxNames
        ? string.Join(", ", names)
        : $"{string.Join(", ", names.Take(MaxNames))}, +{names.Count - MaxNames} more";
}
