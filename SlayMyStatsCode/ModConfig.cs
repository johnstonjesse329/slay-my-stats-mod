using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// SlayMyStats.config.json, next to the mod's .dll. Without it, or with "enabled": false, the mod sends nothing.
/// </summary>
public sealed class ModConfig
{
    public const string FileName = "SlayMyStats.config.json";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where every run goes, by name: each one an ingest function's mod address, e.g.
    /// "prod": "https://example.lambda-url.us-west-2.on.aws/mod". The name marks that site's lines in the log
    /// and names its queue folder, so it is kept to a-z, 0-9 and "-".
    /// </summary>
    [JsonPropertyName("endpoints")]
    public Dictionary<string, string> Endpoints { get; set; } = [];

    /// <summary>The config in this folder, or null (with the reason logged) if uploads should stay off.</summary>
    public static ModConfig? Load(string folder, Action<string> log)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            log($"No {FileName} in {folder}; uploads are off.");
            return null;
        }

        ModConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            log($"{FileName} isn't valid JSON ({e.Message}); uploads are off.");
            return null;
        }

        if (config is not { Enabled: true })
        {
            log($"Uploads are turned off in {FileName}.");
            return null;
        }

        // One bad entry leaves the others sending.
        foreach ((string name, string url) in config.Endpoints.ToList())
        {
            if (!Regex.IsMatch(name, "^[a-z0-9-]+$") ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("https" or "http"))
            {
                log($"Skipping endpoint \"{name}\" in {FileName}: the name or address isn't usable.");
                config.Endpoints.Remove(name);
            }
        }

        if (config.Endpoints.Count == 0)
        {
            log($"{FileName} has no usable \"endpoints\"; uploads are off.");
            return null;
        }

        return config;
    }
}
