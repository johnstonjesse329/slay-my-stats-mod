using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>The snapshot in use: the pack, its synergy data if any could be had, and where it came from.</summary>
public sealed record StatsPack(PackData Data, JsonObject? Synergy, string Source);

/// <summary>
/// The in-game stats snapshot on this computer. <see cref="Load"/> takes the newer of the copy downloaded before
/// (in the cache folder) and the one shipped inside the mod; <see cref="Refresh"/> asks the site once, at launch,
/// for a newer one. Anything wrong with a download leaves what was already there. Nothing here throws to the
/// caller, and no Godot or game code.
/// </summary>
public sealed class PackStore
{
    public const string PackFile = "pack.json.gz";
    public const string EtagFile = "pack.etag";
    public const int MaxDownload = 5 * 1024 * 1024;
    public const int MaxUnzipped = 20 * 1024 * 1024;

    // Only a name like this is ever turned into a file path or an address.
    private static readonly Regex SynergyName = new(@"^synergy\.([0-9a-f]{10})\.json\z");

    private readonly string _folder;
    private readonly Func<string, byte[]?> _bundled;
    private readonly Action<string> _log;
    private volatile StatsPack? _current;

    /// <param name="bundled">A file shipped inside the mod, by name (<see cref="PackFile"/> or "synergy.json"),
    /// or null.</param>
    public PackStore(string cacheFolder, Func<string, byte[]?> bundled, Action<string> log)
    {
        _folder = cacheFolder;
        _bundled = bundled;
        _log = log;
    }

    /// <summary>The newest good snapshot, or null with none. A run holds on to the one it started with.</summary>
    public StatsPack? Current => _current;

    public StatsPack? Load()
    {
        PackData? cached = Parse(ReadCached(PackFile), "the downloaded snapshot");
        PackData? shipped = Parse(_bundled(PackFile), "the shipped snapshot");
        PackData? pack = cached != null && (shipped == null || cached.Built >= shipped.Built) ? cached : shipped;
        _current = pack == null
            ? null
            : new StatsPack(pack, Synergy(pack.Synergy), pack == cached ? "downloaded" : "shipped");
        _log(_current == null
            ? "Stats: no snapshot on this computer."
            : $"Stats: using the {Describe(_current)}.");
        return _current;
    }

    /// <summary>Download the snapshot if the site's differs from the cached one, then any synergy file it names
    /// that isn't here yet.</summary>
    public async Task Refresh(Uri url, HttpClient http, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        try
        {
            Directory.CreateDirectory(_folder);
            PackData? cached = Parse(ReadCached(PackFile), "the downloaded snapshot");
            // Without a good copy the ETag is worthless: the site would answer "not changed" for good.
            string? etag = cached == null ? null : ReadEtag();

            using (CancellationTokenSource stop = new(limit))
            using (HttpRequestMessage request = new(HttpMethod.Get, url))
            {
                if (etag != null && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
                    request.Headers.IfNoneMatch.Add(tag);
                using HttpResponseMessage response =
                    await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    _log("Stats: the snapshot hasn't changed.");
                }
                else
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new HttpRequestException($"the site answered {(int)response.StatusCode}");
                    byte[] body = await ReadLimited(response.Content, stop.Token);
                    cached = Parse(body, "the download") ?? throw new InvalidDataException("the download isn't usable");
                    // The old ETag goes first, so it is never left beside a newer pack.
                    File.Delete(Path.Combine(_folder, EtagFile));
                    Write(PackFile, body);
                    if (response.Headers.ETag is { } fresh) Write(EtagFile, System.Text.Encoding.UTF8.GetBytes(fresh.ToString()));
                    _log($"Stats: downloaded the snapshot built {Date(cached.Built)}.");
                }
            }

            if (cached?.Synergy is { } name) await FetchSynergy(new Uri(url, name), name, http, limit);
        }
        catch (Exception e)
        {
            _log($"Stats: couldn't update the snapshot ({(e is OperationCanceledException ? "it took too long" : e.Message)}).");
        }

        Load();
    }

    private async Task FetchSynergy(Uri url, string name, HttpClient http, TimeSpan limit)
    {
        if (!SynergyName.IsMatch(name) || ParseSynergy(ReadCached(name), name) != null) return;
        using CancellationTokenSource stop = new(limit);
        using HttpResponseMessage response =
            await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"the site answered {(int)response.StatusCode} for {name}");
        byte[] body = await ReadLimited(response.Content, stop.Token);
        if (ParseSynergy(body, name) == null) throw new InvalidDataException($"{name} isn't usable");
        Write(name, body);
        _log($"Stats: downloaded {name}.");
        // Only the one in use is kept.
        foreach (string old in CachedSynergies().Where(f => Path.GetFileName(f) != name))
            File.Delete(old);
    }

    /// <summary>The named synergy data; failing that, whatever older copy there is, since the game data it
    /// describes changes only with game patches.</summary>
    private JsonObject? Synergy(string? name)
    {
        if (name == null) return null;
        byte[]? shipped = _bundled("synergy.json");
        return ParseSynergy(ReadCached(name), name)
               ?? ParseSynergy(shipped, name)
               ?? CachedSynergies().Select(f => ParseSynergy(ReadCached(Path.GetFileName(f)), Path.GetFileName(f)))
                   .FirstOrDefault(s => s != null)
               ?? ParseSynergy(shipped, null);
    }

    private string[] CachedSynergies()
    {
        try
        {
            return Directory.Exists(_folder)
                ? Directory.GetFiles(_folder, "synergy.*.json").Where(f => SynergyName.IsMatch(Path.GetFileName(f)))
                    .ToArray()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private PackData? Parse(byte[]? bytes, string what)
    {
        if (bytes == null) return null;
        try
        {
            PackData? pack = JsonSerializer.Deserialize<PackData>(Unzip(bytes));
            if (pack is { Version: 1, Built: > 0, Ruler: { } ruler, Lanes: not null } && Whole(ruler) &&
                (pack.Synergy == null || SynergyName.IsMatch(pack.Synergy)))
                return pack;
            _log($"Stats: {what} isn't a snapshot this version reads.");
        }
        catch (Exception e)
        {
            _log($"Stats: {what} is damaged ({e.Message}).");
        }
        return null;
    }

    /// <summary>Every list the ruler indexes into is there.</summary>
    private static bool Whole(RulerData ruler)
    {
        static bool Edges(Cut? cut) => cut is { Guard.Length: 2, Pace.Length: 2 };
        return ruler.Encounters != null && ruler.Encounters.Values.All(e => e is { Pool: not null, Tiers: not null }) &&
               ruler.Cuts is { Acts: not null } cuts && Edges(cuts.Run) && cuts.Acts.All(Edges);
    }

    /// <summary>The synergy data, if it parses and (when <paramref name="name"/> is given) its content is what
    /// the name says.</summary>
    private static JsonObject? ParseSynergy(byte[]? bytes, string? name)
    {
        if (bytes == null) return null;
        try
        {
            byte[] json = Unzip(bytes);
            if (name != null && !Convert.ToHexString(SHA256.HashData(json))
                    .StartsWith(SynergyName.Match(name).Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                return null;
            return JsonNode.Parse(json) is JsonObject root && root["v"]?.GetValue<int>() == 1 &&
                   root["synergy"] is JsonObject
                ? root
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The bytes, unzipped if they are gzip, up to <see cref="MaxUnzipped"/>.</summary>
    private static byte[] Unzip(byte[] bytes)
    {
        if (bytes is not [0x1f, 0x8b, ..]) return bytes;
        using GZipStream zip = new(new MemoryStream(bytes), CompressionMode.Decompress);
        return Copy(zip, MaxUnzipped);
    }

    private static byte[] Copy(Stream from, int max)
    {
        MemoryStream into = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = from.Read(buffer)) > 0)
        {
            if (into.Length + read > max) throw new InvalidDataException("it is too big");
            into.Write(buffer, 0, read);
        }
        return into.ToArray();
    }

    private static async Task<byte[]> ReadLimited(HttpContent content, CancellationToken stop)
    {
        await using Stream from = await content.ReadAsStreamAsync(stop);
        MemoryStream into = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await from.ReadAsync(buffer, stop)) > 0)
        {
            if (into.Length + read > MaxDownload) throw new InvalidDataException("the download is too big");
            into.Write(buffer, 0, read);
        }
        return into.ToArray();
    }

    private byte[]? ReadCached(string name)
    {
        try
        {
            FileInfo file = new(Path.Combine(_folder, name));
            return file.Exists && file.Length <= MaxDownload ? File.ReadAllBytes(file.FullName) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ReadEtag() =>
        ReadCached(EtagFile) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes).Trim() : null;

    /// <summary>Whole or not at all: written beside the file, then moved over it.</summary>
    private void Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static string Date(long built) =>
        DateTimeOffset.FromUnixTimeSeconds(built).ToString("yyyy-MM-dd HH:mm 'UTC'");

    private static string Describe(StatsPack pack) =>
        $"{pack.Source} snapshot built {Date(pack.Data.Built)}{(pack.Synergy == null ? ", without synergy data" : "")}";
}
