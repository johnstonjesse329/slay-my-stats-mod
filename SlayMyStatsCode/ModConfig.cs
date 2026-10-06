using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// SlayMyStats.config.json, next to the mod's .dll. Without it, or with "enabled": false, the mod sends nothing.
/// </summary>
public sealed class ModConfig
{
    public const string FileName = "SlayMyStats.config.json";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>The ingest function's mod address, e.g. https://example.lambda-url.us-west-2.on.aws/mod</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

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

        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("https" or "http"))
        {
            log($"{FileName} has no usable \"endpoint\"; uploads are off.");
            return null;
        }

        return config;
    }
}
