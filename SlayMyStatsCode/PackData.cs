using System.Text.Json.Serialization;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// The site's daily snapshot, mod/v1/pack.json.gz. The schema is written down in the site repo's
/// infra/lambda/ingest/mod_pack.py; a change this code can't read goes to mod/v2/.
/// </summary>
public sealed class PackData
{
    [JsonPropertyName("v")] public int Version { get; init; }

    /// <summary>When the site built it, in seconds since 1970.</summary>
    [JsonPropertyName("built")] public long Built { get; init; }

    /// <summary>The synergy file this pack goes with, or null.</summary>
    [JsonPropertyName("synergy")] public string? Synergy { get; init; }

    [JsonPropertyName("ruler")] public RulerData? Ruler { get; init; }

    /// <summary>"solo" and "multi".</summary>
    [JsonPropertyName("lanes")] public Dictionary<string, Lane>? Lanes { get; init; }
}

public sealed class RulerData
{
    [JsonPropertyName("encounters")] public Dictionary<string, Encounter> Encounters { get; init; } = [];
    [JsonPropertyName("cuts")] public Cuts Cuts { get; init; } = new();
}

public sealed class Encounter
{
    /// <summary>The HP to get through: [base, Tough Enemies].</summary>
    [JsonPropertyName("pool")] public double[] Pool { get; init; } = [];

    /// <summary>What winning runs did here: [under ascension 8, 8 and up].</summary>
    [JsonPropertyName("tiers")] public Line[] Tiers { get; init; } = [];
}

/// <summary>Empty (no Turns) when too few have won.</summary>
public sealed class Line
{
    [JsonPropertyName("turns")] public double? Turns { get; init; }
    [JsonPropertyName("taken")] public double Taken { get; init; }
    [JsonPropertyName("n")] public int Runs { get; init; }
    [JsonPropertyName("coop")] public Line? Coop { get; init; }
}

public sealed class Cuts
{
    [JsonPropertyName("acts")] public Cut[] Acts { get; init; } = [];
    [JsonPropertyName("run")] public Cut Run { get; init; } = new();
}

/// <summary>Where the worst and best quarter of winners start: [low, high].</summary>
public sealed class Cut
{
    [JsonPropertyName("guard")] public double[] Guard { get; init; } = [];
    [JsonPropertyName("pace")] public double[] Pace { get; init; } = [];
}

/// <summary>[runs, wins] by id, and picks by character.</summary>
public sealed class Lane
{
    [JsonPropertyName("chars")] public Dictionary<string, int[]> Characters { get; init; } = [];
    [JsonPropertyName("cards")] public Dictionary<string, int[]> Cards { get; init; } = [];
    [JsonPropertyName("relics")] public Dictionary<string, int[]> Relics { get; init; } = [];
    [JsonPropertyName("picks")] public Dictionary<string, CharacterPicks> Picks { get; init; } = [];

    /// <summary>First-page event choices by event id, all characters together.</summary>
    [JsonPropertyName("events")] public Dictionary<string, EventChoices> Events { get; init; } = [];
}

public sealed class EventChoices
{
    /// <summary>Visits to the event. Missing when some options aren't offered on every visit.</summary>
    [JsonPropertyName("v")] public int? Visits { get; init; }

    /// <summary>[times taken, wins] by option.</summary>
    [JsonPropertyName("o")] public Dictionary<string, int[]> Options { get; init; } = [];
}

/// <summary>[picks, wins when picked, offers, wins when skipped] by id.</summary>
public sealed class CharacterPicks
{
    [JsonPropertyName("cards")] public Dictionary<string, int[]> Cards { get; init; } = [];
    [JsonPropertyName("relics")] public Dictionary<string, int[]> Relics { get; init; } = [];
}
