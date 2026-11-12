using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// The map of each act, and the path the player took through it. The .run file keeps only the node types
/// in the order they were visited, so the map itself (which nodes existed, which joined which) can only be
/// taken while the run is live.
///
/// Taken each time a map node is entered, not when the map is generated: a run that was saved and resumed
/// never generates its map again, but it always enters nodes. Every entry replaces the act's map rather than
/// keeping the first, because the map can change mid-act (Golden Compass, picked at the act's Ancient, swaps
/// it for a single straight path). The game's own SerializableActMap is the shape written to its save file,
/// so it is read rather than the live grid.
///
/// Each act is one object: { act, width, height, boss, boss_encounter, points: [[row, col, type,
/// [[childRow, childCol], ...]]], path: [[row, col], ...] }, plus second_boss and second_boss_encounter on an
/// act with two bosses (the last act from Ascension 10). "type" is the game's MapPointType name. The bosses
/// are rolled when the run starts, so they are known before the player reaches them; the .run file only
/// names the ones that were fought.
/// </summary>
public static class MapLog
{
    private static readonly SortedDictionary<int, JsonObject> Acts = [];

    public static void Reset() => Acts.Clear();

    /// <summary>The acts seen so far as a JSON array, or null when none was.</summary>
    public static JsonArray? Take()
    {
        if (Acts.Count == 0) return null;
        JsonArray all = [];
        foreach (JsonObject act in Acts.Values) all.Add(act.DeepClone());
        return all;
    }

    public static void Visit(RunState state)
    {
        int act = state.CurrentActIndex;
        JsonObject entry = Describe(act, state.Map, state.Act);
        Acts[act] = entry;
        // The game keeps the visited coordinates of the current act only, so the path is rewritten from it.
        JsonArray path = [];
        foreach (MapCoord c in state.VisitedMapCoords) path.Add(new JsonArray(c.row, c.col));
        entry["path"] = path;
    }

    private static JsonObject Describe(int act, ActMap map, ActModel model)
    {
        SerializableActMap saved = SerializableActMap.FromActMap(map);
        JsonArray points = [];
        foreach (SerializableMapPoint p in saved.Points)
        {
            JsonArray children = [];
            foreach (MapCoord c in p.ChildCoords ?? []) children.Add(new JsonArray(c.row, c.col));
            points.Add(new JsonArray(p.Coord.row, p.Coord.col, p.PointType.ToString(), children));
        }
        JsonObject o = new()
        {
            ["act"] = act,
            ["width"] = saved.GridWidth,
            ["height"] = saved.GridHeight,
            ["points"] = points,
            ["path"] = new JsonArray(),
        };
        if (saved.BossPoint != null) o["boss"] = new JsonArray(saved.BossPoint.Coord.row, saved.BossPoint.Coord.col);
        if (saved.SecondBossPoint != null)
            o["second_boss"] = new JsonArray(saved.SecondBossPoint.Coord.row, saved.SecondBossPoint.Coord.col);
        o["boss_encounter"] = model.BossEncounter.Id.ToString();
        if (model.HasSecondBoss && model.SecondBossEncounter != null)
            o["second_boss_encounter"] = model.SecondBossEncounter.Id.ToString();
        return o;
    }
}

/// <summary>Runs after the game records a visited map node. A failure here never reaches the game.</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.AddVisitedMapCoord))]
public static class MapVisitPatch
{
    [HarmonyPostfix]
    public static void Postfix(RunState __instance)
    {
        try { MapLog.Visit(__instance); }
        catch (Exception e) { MainFile.Logger.Error($"Map capture failed: {e}"); }
    }
}
