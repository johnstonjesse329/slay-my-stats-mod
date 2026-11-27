using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SlayMyStats.SlayMyStatsCode;

internal sealed record RunChoices(int Elites, int Rested, int Smithed, int Removed, int SkippedRewardFloors);

/// <summary>
/// The run being played, for the in-game stats: the snapshot it holds on to, and the game's run state in the shape
/// <see cref="Ruler"/> reads. With debug on, the reading so far is also logged after each won fight.
/// Nothing here may disturb the game.
/// </summary>
public static class LiveRun
{
    private static PackStore? _store;
    private static Func<DisplaySettings>? _settings;
    private static StatsPack? _pack;
    private static CombatRecords? _finishedRecords;

    /// <summary>The snapshot for this run. One downloaded mid-run waits for the next, unless there was none.</summary>
    public static StatsPack? Pack => _pack ??= _store?.Current;

    /// <summary>The newest snapshot, for screens that belong to no run.</summary>
    public static StatsPack? LatestPack => _store?.Current;

    /// <summary>What the player wants shown, as their config says now; null before <see cref="Watch"/>.</summary>
    public static DisplaySettings? Settings => _settings?.Invoke();

    /// <summary>The player ticked or unticked a setting in the game.</summary>
    public static event Action? SettingsChanged;

    public static void ChangedSettings() => SettingsChanged?.Invoke();

    /// <summary>What the run strength mark has to show now; null if it's switched off, there is no run or
    /// snapshot, or no fight to read yet.</summary>
    public static Strength? Strength()
    {
        if (_settings?.Invoke().RunStrength != true || Pack?.Data.Ruler is not { } demand || Record() is not { } run)
            return null;
        RunState state = RunManager.Instance.DebugOnlyGetState()!;
        // The room after the last boss: the run is won.
        return SlayMyStatsCode.Strength.Of(run, demand, state.CurrentActIndex, LocalPlayer(),
            won: state.CurrentRoom?.IsVictoryRoom == true);
    }

    /// <summary>Read on hover so a record from the current fight shows without waiting for combat to end.</summary>
    public static CombatRecords? RecordsSoFar(int fights)
    {
        if (_finishedRecords is { } finished) return finished;
        RunCheckpoint.EnsureLoaded();       // after a quit, the log comes back from the checkpoint
        return EventLog.RecordsFor(LocalPlayer(), fights);
    }

    internal static RunChoices? ChoicesSoFar()
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } state || state.Players.Count == 0)
            return null;

        ulong playerId = state.Players[LocalPlayer()].NetId;
        int elites = 0, rested = 0, smithed = 0, removed = 0, skippedRewardFloors = 0;
        foreach (var point in state.MapPointHistory.SelectMany(act => act))
        {
            elites += point.Rooms.Count(room => room.RoomType == RoomType.Elite && room.TurnsTaken > 0);
            if (point.PlayerStats.FirstOrDefault(stats => stats.PlayerId == playerId) is not { } player) continue;
            removed += player.CardsRemoved.Count;
            rested += player.RestSiteChoices.Count(choice => choice == "HEAL");
            smithed += player.RestSiteChoices.Count(choice => choice == "SMITH");

            // History stores the individual cards without grouping them into reward screens.
            // Match the website: a non-shop floor with a card offer and no pick.
            if (point.MapPointType != MapPointType.Shop &&
                player.CardChoices.Count > 0 && player.CardChoices.All(choice => !choice.wasPicked))
                skippedRewardFloors++;
        }
        return new RunChoices(elites, rested, smithed, removed, skippedRewardFloors);
    }

    /// <summary>Keep the records available on the victory screen after the upload takes the event log.</summary>
    internal static void KeepFinishedRecords()
    {
        try
        {
            if (Record() is { } run)
                _finishedRecords = EventLog.RecordsFor(LocalPlayer(), Ruler.RunFights(run).Count);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Stats: couldn't save the finished combat records: {e}");
        }
    }

    /// <summary>How a card or relic connects to the local player's deck and relics; null outside a run or with
    /// nothing to show.</summary>
    public static Synergy? Synergy(JsonObject data, AbstractModel model)
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } state || Item(model) is not { } item) return null;
        var player = state.Players[LocalPlayer()];
        List<SynergyItem> owned = player.Deck.Cards.Cast<AbstractModel>().Concat(player.Relics)
            .Select(Item).OfType<SynergyItem>().ToList();
        return SlayMyStatsCode.Synergy.Of(data, player.Character.Id.Entry, item, owned);
    }

    private static SynergyItem? Item(AbstractModel model) => model switch
    {
        CardModel card => new(card.Id.ToString(), card.Title, Upgraded: card.IsUpgraded),
        RelicModel relic => new(relic.Id.ToString(), relic.Title.GetFormattedText(), Relic: true),
        _ => null,
    };

    /// <summary>In a party, the damage is the local player's; each point's rows are in the players' order.</summary>
    private static int LocalPlayer() => Math.Max(0, RunManager.Instance.DebugOnlyGetState()!.Players.ToList()
        .FindIndex(p => p.NetId == LocalContext.NetId));

    public static void Watch(PackStore store, Func<DisplaySettings> settings)
    {
        _store = store;
        _settings = settings;
        RunManager.Instance.RunStarted += _ =>
        {
            _pack = store.Current;
            _finishedRecords = null;
            if (MainFile.Uploaders.Count != 0) return;
            EventHooks.Reset();
            EventLog.Reset();
        };
        CombatManager.Instance.CombatWon += OnCombatWon;
    }

    /// <summary>The run so far, as its run file would have it; null outside a run.</summary>
    public static RunRecord? Record()
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } state) return null;
        return new RunRecord
        {
            InProgress = true,
            GameMode = state.GameMode.ToString().ToLowerInvariant(),
            Ascension = state.AscensionLevel,
            Players = state.Players.Select(p => new RunPlayer
            {
                Relics = p.Relics.Select(r => new RunRelic
                {
                    Id = r.Id.ToString(),
                    Floor = r.FloorAddedToDeck,
                }).ToList(),
            }).ToList(),
            Acts = state.MapPointHistory.Select(act => act.Select(point => new RunPoint
            {
                Type = point.MapPointType.ToString().ToLowerInvariant(),
                Stats = point.PlayerStats.Select(s => new PointStats
                {
                    PlayerId = s.PlayerId,
                    DamageTaken = s.DamageTaken,
                    HpHealed = s.HpHealed,
                    RelicChoices = s.RelicChoices
                        .Select(c => new RelicChoice { Choice = c.choice.ToString(), WasPicked = c.wasPicked })
                        .ToList(),
                }).ToList(),
                Rooms = point.Rooms.Select(room => new RunRoom
                {
                    ModelId = room.ModelId?.ToString(),
                    MonsterIds = room.MonsterIds.Select(m => m.ToString()).ToList(),
                    Turns = room.TurnsTaken,
                }).ToList(),
            }).ToList()).ToList(),
        };
    }

    private static void OnCombatWon(CombatRoom room)
    {
        if (!RunHealth.Enabled) return;
        try
        {
            if (_settings?.Invoke().RunStrength != true || Pack?.Data.Ruler is not { } demand || Record() is not { } run)
                return;
            List<Fight> fights = Ruler.RunFights(run);
            int who = LocalPlayer();
            List<ActReading> acts = Ruler.ActReadings(fights, demand,
                f => f.Coop && who < f.Each.Length ? f.Each[who] : f.Taken);
            string reading = acts.Count == 0
                ? "no act has enough fights yet"
                : string.Join("; ", acts.Select(a =>
                    $"act {a.Act + 1} {a.Verdict} (speed {a.Pace:0.00}, held {a.Guard:0.00}, {a.Fights} fights)"));
            if (Ruler.ReadRun(acts, demand.Cuts) is { } whole) reading += $"; run {whole.Verdict}";
            MainFile.Logger.Info($"Stats: after {fights.Count} fights, {reading}.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Stats: couldn't read the run: {e}");
        }
    }
}
