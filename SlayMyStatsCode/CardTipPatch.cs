using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Adds a tip with a card's or relic's win and pick rate, and how it connects to the player's deck and relics, to
/// the game's own tips, during a run, wherever one is chosen or looked through: rewards, shop, relic choices and
/// card grids such as the deck view. Cards in hand and relics already owned in the top bar are left alone.
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

    /// <summary>The card or relic a tip's owner is showing, for the owners that get stats.</summary>
    private static AbstractModel? Offered(Control owner) => owner switch
    {
        NGridCardHolder holder => holder.CardModel,
        NMerchantCard card => (card.Entry as MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry)?.CreationResult?.Card,
        NMerchantRelic relic => (relic.Entry as MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry)?.Model,
        NRelicBasicHolder holder => holder.Relic?.Model,
        NTreasureRoomRelicHolder holder => holder.Relic?.Model,
        NRewardButton { Reward: RelicReward reward } => reward.Relic,
        NEventOptionButton button => button.Option?.Relic,
        _ => null,
    };

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
        if (synergy.Archetypes.Count > 0)
        {
            string heading = synergy.Archetypes.Count == 1 ? "Archetype" : "Archetypes";
            lines.Add($"{heading}: {string.Join(", ", synergy.Archetypes)}");
        }
        return string.Join("\n", lines);
    }

    private static string Names(IReadOnlyList<string> names) => names.Count <= MaxNames
        ? string.Join(", ", names)
        : $"{string.Join(", ", names.Take(MaxNames))}, +{names.Count - MaxNames} more";
}
