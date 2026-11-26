using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>A card or relic, with the name its tips show.</summary>
public sealed record SynergyItem(string Id, string Name, bool Relic = false, bool Upgraded = false);

/// <summary>Something one item gives and another uses, with the names of the items at the other end.</summary>
public sealed record SynergyLink(string Key, IReadOnlyList<string> Names);

/// <summary>
/// How a card or relic connects to what the player owns, read from the site's synergy file the way its deck view
/// reads it: what the item wants that the deck and relics supply, what it gives that they use, and for a card the
/// archetypes it belongs to. No Godot or game code.
/// </summary>
public sealed record Synergy(
    IReadOnlyList<SynergyLink> Wants, IReadOnlyList<SynergyLink> Gives, IReadOnlyList<string> Archetypes)
{
    // A synergy row is [what the item makes, what it needs].
    private const int Makes = 0, Needs = 1;

    /// <summary>Null when there is no connection and no archetype to show.</summary>
    /// <param name="character">The player's character, as "IRONCLAD".</param>
    /// <param name="owned">The player's deck and relics. The item itself never counts.</param>
    public static Synergy? Of(JsonObject data, string character, SynergyItem item, IReadOnlyList<SynergyItem> owned)
    {
        if (data["synergy"] is not { } table) return null;
        HashSet<string> basic = Strings(data["basic"]).ToHashSet();
        List<(SynergyItem Item, JsonArray? Row)> others =
            owned.Where(o => o.Id != item.Id).Select(o => (o, Row(table, o))).ToList();

        // Every deck starts with its starter cards, so a link between starters only says nothing.
        List<SynergyLink> Links(JsonArray? row, int mine, int theirs) => Strings(row?[mine])
            .Order(StringComparer.Ordinal)
            .Select(key => (Key: key, Matches: others.Where(o => Strings(o.Row?[theirs]).Contains(key))
                .Select(o => o.Item).DistinctBy(o => o.Id).ToList()))
            .Where(l => l.Matches.Count > 0 &&
                        !(basic.Contains(item.Id) && l.Matches.All(m => basic.Contains(m.Id))))
            .Select(l => new SynergyLink(Label(l.Key), l.Matches.Select(m => m.Name).ToList()))
            .ToList();

        JsonArray? own = Row(table, item);
        List<SynergyLink> wants = Links(own, Needs, Makes), gives = Links(own, Makes, Needs);
        List<string> archetypes = !item.Relic && data["archetypes"]?[character] is JsonObject all
            ? all.Where(a => Strings(a.Value?["cards"]).Contains(item.Id)).Select(a => a.Key).ToList()
            : [];
        return wants.Count + gives.Count + archetypes.Count == 0 ? null : new Synergy(wants, gives, archetypes);
    }

    private static JsonArray? Row(JsonNode table, SynergyItem item) => (item.Relic
        ? table["relics"]?[item.Id]
        : table["cardVariants"]?[item.Id]?[item.Upgraded ? 1 : 0] ?? table["cards"]?[item.Id]) as JsonArray;

    private static IEnumerable<string> Strings(JsonNode? list) =>
        (list as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [];

    /// <summary>"power:Strength" reads as "Strength", "zero-cost" as "Zero-cost".</summary>
    private static string Label(string key)
    {
        string word = key.StartsWith("power:", StringComparison.Ordinal) ? key["power:".Length..] : key;
        return word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
    }
}
