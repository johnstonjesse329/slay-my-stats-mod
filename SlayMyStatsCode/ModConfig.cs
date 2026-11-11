using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// SlayMyStats.config.json, shipped next to the mod's .dll, with the player's own config.json in
/// %APPDATA%\SlayTheSpire2\SlayMyStats laid over it: each key the player sets replaces the shipped one, so
/// {"enabled": false} on its own turns uploads off and keeps the shipped endpoints. The player's file lives
/// apart because Steam replaces the mod's folder on every Workshop update. Without either, or with
/// "enabled": false, the mod sends nothing.
/// </summary>
public sealed class ModConfig
{
    public const string FileName = "SlayMyStats.config.json";
    public const string UserFileName = "config.json";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where every run goes, by name: each one an ingest function's mod address, e.g.
    /// "prod": "https://example.lambda-url.us-west-2.on.aws/mod". The name marks that site's lines in the log
    /// and names its queue folder, so it is kept to a-z, 0-9 and "-".
    /// </summary>
    [JsonPropertyName("endpoints")]
    public Dictionary<string, string> Endpoints { get; set; } = [];

    /// <summary>
    /// The shipped config with the player's laid over it; null (with the reason logged) if uploads should stay
    /// off.
    /// </summary>
    public static ModConfig? Load(string userFolder, string modFolder, Action<string> log)
    {
        string[] paths = [Path.Combine(modFolder, FileName), Path.Combine(userFolder, UserFileName)];
        string[] found = paths.Where(File.Exists).ToArray();
        if (found.Length == 0)
        {
            log($"No {string.Join(" or ", paths)}; uploads are off.");
            return null;
        }
        string path = string.Join(" + ", found);
        log($"Reading {path}.");

        ModConfig? config;
        try
        {
            JsonObject merged = [];
            foreach (string file in found)
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject layer)
                    throw new JsonException($"{file} isn't a JSON object");
                foreach ((string key, JsonNode? value) in layer.ToList())
                {
                    layer.Remove(key);
                    merged[key] = value;
                }
            }
            config = merged.Deserialize<ModConfig>();
        }
        catch (JsonException e)
        {
            log($"{path} isn't valid JSON ({e.Message}); uploads are off.");
            return null;
        }

        if (config is not { Enabled: true })
        {
            log($"Uploads are turned off in {path}.");
            return null;
        }

        // One bad entry leaves the others sending.
        foreach ((string name, string url) in config.Endpoints.ToList())
        {
            if (!Regex.IsMatch(name, "^[a-z0-9-]+$") ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("https" or "http"))
            {
                log($"Skipping endpoint \"{name}\" in {path}: the name or address isn't usable.");
                config.Endpoints.Remove(name);
            }
        }

        if (config.Endpoints.Count == 0)
        {
            log($"{path} has no usable \"endpoints\"; uploads are off.");
            return null;
        }

        return config;
    }
}
