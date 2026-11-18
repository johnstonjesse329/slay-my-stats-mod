using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// What the player lets the mod upload, from two keys in their own config.json (in
/// %APPDATA%\SlayTheSpire2\SlayMyStats, beside the carry_progress key):
///
///   uploadsEnabled       true; false stops every upload (anything skipped goes once it's turned back on)
///   profiles             "all", or a list such as ["profile1"]; only these profiles' runs upload
///
/// The file is read again each time it is asked for, so an edit applies without restarting the game. A missing
/// file is created with the defaults. A file that doesn't parse (or has a key of the wrong kind) means nothing
/// uploads until it is fixed: the exact error goes to slay-my-stats-config-error.txt beside the file (deleted
/// once it parses), to the log, and, where <see cref="Notice"/> is wired up, onto the main menu.
/// </summary>
public sealed record UploadSettings(bool Enabled, HashSet<string>? Profiles, string? Error)
{
    public const string ErrorFileName = "slay-my-stats-config-error.txt";

    private const string Defaults = "{\n  \"uploadsEnabled\": true,\n  \"profiles\": \"all\"\n}\n";

    // The newest read, for the main-menu notice: null when the config is fine.
    private static string? _error;
    private static string? _loggedError;

    /// <summary>The config's problem, if its last read found one, with where to look. For the main-menu notice.</summary>
    public static string? Notice => _error is null ? null : $"Slay My Stats: config error, uploads paused – see {_errorPath}";

    private static string _errorPath = "";

    /// <summary>Nothing may upload: the file is broken, or the player turned uploads off.</summary>
    public bool Paused => Error != null || !Enabled;

    /// <summary>Whether a run played on <paramref name="profile"/> (e.g. "profile1"; null if unknown) may upload.</summary>
    public bool Allows(string? profile)
    {
        if (Paused) return false;
        return Profiles == null || (profile != null && Profiles.Contains(profile));
    }

    /// <summary>Read the player's config now. Never throws.</summary>
    public static UploadSettings Read(string userFolder, Action<string> log)
    {
        string path = Path.Combine(userFolder, ModConfig.UserFileName);
        string errorFile = Path.Combine(userFolder, ErrorFileName);
        _errorPath = errorFile;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(userFolder);
                File.WriteAllText(path, Defaults);
                log($"No {path}; created it with the defaults.");
            }

            JsonObject root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new JsonException("the file isn't a JSON object");

            bool enabled = Flag(root, "uploadsEnabled");
            HashSet<string>? profiles = Profile(root["profiles"]);

            Clear(errorFile);
            return new UploadSettings(enabled, profiles, null);
        }
        catch (Exception e)
        {
            string error = $"{path}: {e.Message}";
            _error = error;
            if (_loggedError != error)
            {
                _loggedError = error;
                log($"Config error, uploads paused until it is fixed. {error}");
            }
            try
            {
                File.WriteAllText(errorFile, $"Slay My Stats can't use its config, so nothing is uploading.\n\n{error}\n\n" +
                    "Fix the file (or delete it to get the defaults back). This note goes away once the file parses.\n");
            }
            catch (Exception)
            {
                // the log has it
            }
            return new UploadSettings(false, null, error);
        }
    }

    private static void Clear(string errorFile)
    {
        _error = null;
        _loggedError = null;
        try
        {
            if (File.Exists(errorFile)) File.Delete(errorFile);
        }
        catch (Exception)
        {
            // a note that can't be deleted is only a stale note
        }
    }

    /// <summary>A true/false key; absent means true.</summary>
    private static bool Flag(JsonObject root, string key)
    {
        if (root[key] is not { } node) return true;
        if (node is JsonValue v && v.TryGetValue(out bool b)) return b;
        throw new JsonException($"\"{key}\" must be true or false");
    }

    /// <summary>"all" (or absent) means every profile; otherwise a list of names like "profile1", or numbers like 1.</summary>
    private static HashSet<string>? Profile(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue s && s.TryGetValue(out string? text))
        {
            if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase)) return null;
            throw new JsonException("\"profiles\" must be \"all\" or a list such as [\"profile1\"]");
        }
        if (node is not JsonArray list) throw new JsonException("\"profiles\" must be \"all\" or a list such as [\"profile1\"]");

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in list)
        {
            if (item is JsonValue v && v.TryGetValue(out string? name) && name.Length > 0) names.Add(name);
            else if (item is JsonValue n && n.TryGetValue(out int number)) names.Add($"profile{number}");
            else throw new JsonException("\"profiles\" must list names like \"profile1\"");
        }
        return names;
    }
}
