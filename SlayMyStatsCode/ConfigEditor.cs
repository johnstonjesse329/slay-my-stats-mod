using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Reads and changes the true/false keys of the player's config.json. A change rewrites only that key's value in
/// the file text (or adds the key), so other keys, comments and layout stay. A file that doesn't parse is never
/// written.
/// </summary>
public static class ConfigEditor
{
    private static readonly JsonDocumentOptions Lenient = new()
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The file's keys, or null if it can't be read or isn't a JSON object. A missing file has none.</summary>
    public static JsonObject? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Set a top-level true/false key. False, with the file untouched, if it can't be done safely.</summary>
    public static bool SetFlag(string path, string key, bool value)
    {
        try
        {
            if (Read(path) == null) return false;
            byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : "{\n}\n"u8.ToArray();
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            byte[] json = bom ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;
            byte[] edited = Edit(json, key, value);
            if (JsonNode.Parse(Encoding.UTF8.GetString(edited), documentOptions: Lenient) is not JsonObject check
                || check[key]?.GetValue<bool>() != value) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bom ? [.. Encoding.UTF8.Preamble, .. edited] : edited);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Edit(byte[] json, string key, bool value)
    {
        Utf8JsonReader reader = new(json, new JsonReaderOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string text = value ? "true" : "false";
        int found = -1, length = 0;
        int lastValueEnd = -1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            bool match = reader.ValueTextEquals(key);
            reader.Read();
            int start = (int)reader.TokenStartIndex;
            reader.Skip();
            lastValueEnd = (int)reader.BytesConsumed;
            // The parser keeps the last of two keys with one name, so that one is the one to change.
            if (match) (found, length) = (start, lastValueEnd - start);
        }

        if (found >= 0) return Splice(json, found, length, text);

        string newline = json.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        string entry = $"{newline}  {JsonSerializer.Serialize(key)}: {text}";
        // A new key goes first, so no existing line or trailing comment has to change.
        int open = OpeningBrace(json);
        return Splice(json, open + 1, 0, lastValueEnd >= 0 ? entry + "," : entry);
    }

    private static int OpeningBrace(byte[] json)
    {
        Utf8JsonReader reader = new(json, new JsonReaderOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        reader.Read();
        return (int)reader.TokenStartIndex;
    }

    private static byte[] Splice(byte[] json, int at, int remove, string insert) =>
        [.. json.AsSpan(0, at), .. Encoding.UTF8.GetBytes(insert), .. json.AsSpan(at + remove)];
}
