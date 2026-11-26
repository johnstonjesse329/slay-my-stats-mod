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
    /// Whether to lift a modded profile to the standard one's progress (see ProgressCarryPatch). On unless a
    /// config file sets "carry_progress": false; the player's file wins over the shipped one.
    /// </summary>
    public static bool CarryProgressEnabled(string userFolder, string modFolder) =>
        Layered("carry_progress", true, userFolder, modFolder);

    /// <summary>
    /// For the author and testers: "debug": true turns on RunHealth's checks and sends their findings with each
    /// run. Off unless set, and left out of the generated config so players don't see it.
    /// </summary>
    public static bool DebugEnabled(string userFolder, string modFolder) =>
        Layered("debug", false, userFolder, modFolder);

    /// <summary>
    /// Where the in-game stats snapshot is downloaded from (see PackStore); null if no config file names one. The
    /// player's file wins over the shipped one, and "enabled": false, which stops uploads, doesn't affect it.
    /// </summary>
    public static Uri? StatsUrl(string userFolder, string modFolder) =>
        Uri.TryCreate(Layered("statsUrl", "", userFolder, modFolder), UriKind.Absolute, out Uri? url) &&
        url.Scheme is "https" or "http"
            ? url
            : null;

    /// <summary>The same leniency UploadSettings reads the player's file with.</summary>
    private static readonly JsonDocumentOptions Lenient = new()
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static T Layered<T>(string key, T setting, string userFolder, string modFolder)
    {
        foreach (string file in new[] { Path.Combine(modFolder, FileName), Path.Combine(userFolder, UserFileName) })
        {
            try
            {
                if (File.Exists(file) &&
                    JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) is JsonObject layer &&
                    layer[key] is JsonValue value && value.TryGetValue(out T? found) && found != null)
                    setting = found;
            }
            catch (Exception)
            {
                // an unreadable file leaves the default
            }
        }

        return setting;
    }

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
                JsonObject? layer;
                try
                {
                    layer = JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) as JsonObject;
                    if (layer == null) throw new JsonException($"{file} isn't a JSON object");
                }
                catch (JsonException e) when (file == paths[1])
                {
                    // The player's own file is checked by UploadSettings, which holds uploads and reports it. Skipping
                    // it here keeps the shipped endpoints, so uploads resume as soon as the file is fixed.
                    log($"Ignoring {file} ({e.Message}); UploadSettings reports it.");
                    continue;
                }
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
