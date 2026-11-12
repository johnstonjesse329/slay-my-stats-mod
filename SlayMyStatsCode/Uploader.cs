using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Sends finished runs to slay-my-stats. Nothing here touches the game: a run is handed over as its JSON text,
/// parked in a queue folder on disk, and sent from a background task, so a slow or failed upload can never
/// stall the game or lose the run. Whatever is still queued goes with the next run, or the next launch.
///
/// One upload is the same two steps the site's upload page takes: ask the ingest endpoint for an upload URL
/// (proving who is playing with a Steam ticket instead of a browser sign-in), then POST the gzip'd runs, one
/// JSON object per line, straight to that URL.
///
/// Runs played unmodded before the mod was installed are backfilled: once per launch the uploader asks the site
/// which runs it already has, and queues the rest from the game's normal history, until the site has them all.
/// </summary>
public sealed class Uploader
{
    /// <summary>A Steam Web API ticket for the account that is playing. Release it once the server has answered.</summary>
    public sealed record Ticket(string SteamId, string Hex, Action Release);

    /// <summary>Steam had no ticket to give just now. Worth another try later.</summary>
    public sealed class TicketException(string message) : Exception(message);

    // The server has its own limits. These are here so that this mod, if it ever goes wrong, stops by itself
    // rather than being stopped: it gives up on a batch after a few tries, and asks at most this often.
    private const int MaxRequestsPerHour = 30;
    private const int MaxQueuedRuns = 500;
    private const int MaxRunsPerUpload = 500;
    // Under the site's 1 MiB. A batch is halved until it fits: ~250 old runs, or a handful with fight logs.
    private const int MaxUploadBytes = 1_000_000;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private enum Outcome { Sent, Retry, Stop }

    // The steam folder again when the last backfill found more missing runs than the queue had room for.
    private string? _backfillMore;

    // Every run this uploader has sent, one start time per line, kept beside the queue. A run the site took but
    // didn't keep (one its parser rejects) is never backfilled again because of it.
    private const string SentFile = "sent.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Uri _endpoint;
    private readonly string _queueDir;
    private readonly Func<Task<Ticket>> _getTicket;
    private readonly Action<string> _log;
    private readonly string _modVersion;
    private readonly List<DateTime> _requests = [];
    private int _batchSize = MaxRunsPerUpload;
    private int _running;
    private int _kickRequested;
    private bool _stopped;
    private string? _backfillFrom;

    public Uploader(Uri endpoint, string queueDir, Func<Task<Ticket>> getTicket, Action<string> log, string modVersion)
    {
        _endpoint = endpoint;
        _queueDir = queueDir;
        _getTicket = getTicket;
        _log = log;
        _modVersion = modVersion;
        Directory.CreateDirectory(_queueDir);
    }

    /// <summary>
    /// Park one run (the text of its .run file) in the queue. The run is rewritten onto a single line, which
    /// is what the server reads, with a note that it came from this mod and which mods were loaded: the game's
    /// own file doesn't say.
    /// </summary>
    /// <param name="mods">The loaded mods, or null for a run from history, which went unwatched: it is sent like a
    /// run uploaded from files, saying nothing about mods.</param>
    public void Enqueue(string runJson, JsonArray? mods, JsonObject? telemetry = null, JsonArray? maps = null)
    {
        JsonObject run = JsonNode.Parse(runJson)?.AsObject() ?? throw new JsonException("not a run");
        if (mods != null)
        {
            JsonObject note = new() { ["version"] = _modVersion, ["mods"] = mods.DeepClone() };
            if (maps != null) note["maps"] = maps.DeepClone();
            run["slay_my_stats_mod"] = note;
        }
        // What the deck actually did, when this session watched the run. Runs
        // from history have none: they went unwatched.
        if (telemetry != null)
        {
            run["telemetry"] = telemetry.DeepClone();
        }

        string name = run["start_time"]?.ToString() ?? Guid.NewGuid().ToString("N");

        // Written whole or not at all, so a crash mid-write can't leave half a run to be sent.
        string path = Path.Combine(_queueDir, $"{name}.json");
        File.WriteAllText(path + ".tmp", run.ToJsonString());
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Queue the runs under <paramref name="steamDir"/> (the game's "steam" folder, one folder per account) that
    /// the site doesn't have. Done at the start of the next pass through the queue, ahead of sending it.
    /// </summary>
    public void Backfill(string steamDir)
    {
        _backfillFrom = steamDir;
    }

    /// <summary>Start sending the queue, if it isn't being sent already. Returns at once.</summary>
    public void Kick()
    {
        _ = Task.Run(DrainAsync);
    }

    /// <summary>Send whatever is queued, and come back when there is nothing left to try.</summary>
    public async Task DrainAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            // A run that ends while an earlier one is being sent gets its own pass afterwards.
            Interlocked.Exchange(ref _kickRequested, 1);
            return;
        }

        try
        {
            async Task Backfill(string steamDir)
            {
                try
                {
                    _backfillMore = await BackfillAsync(steamDir) ? steamDir : null;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TicketException or IOException or JsonException)
                {
                    // Only the backfill waits for the next launch; the queue still goes now.
                    _backfillMore = null;
                    _log($"Couldn't check the site for missing runs ({e.GetType().Name}: {e.Message}). Trying again next launch.");
                }
            }

            if (Interlocked.Exchange(ref _backfillFrom, null) is { } steamDir) await Backfill(steamDir);

            int attempt = 0;
            while (!_stopped)
            {
                List<string> batch = NextBatch();
                if (batch.Count == 0)
                {
                    // The queue is empty: if the history had more than it could hold, queue the next lot.
                    if (_backfillMore is not { } more) return;
                    await Backfill(more);
                    if (NextBatch().Count == 0) return;
                    continue;
                }

                Outcome outcome;
                try
                {
                    outcome = await SendAsync(batch);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TicketException or IOException)
                {
                    _log($"Upload didn't go through ({e.GetType().Name}: {e.Message}).");
                    outcome = Outcome.Retry;
                }

                if (outcome == Outcome.Sent)
                {
                    attempt = 0;
                }
                else if (outcome == Outcome.Stop)
                {
                    _stopped = true;
                    _log("Not trying again until the game is restarted. Queued runs are kept.");
                }
                else if (attempt >= RetryDelays.Length)
                {
                    _log($"Giving up for now. {batch.Count} run(s) stay queued for the next run or launch.");
                    return;
                }
                else
                {
                    await Task.Delay(RetryDelays[attempt++]);
                }
            }
        }
        catch (Exception e)
        {
            _log($"Uploader failed: {e}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if (Interlocked.Exchange(ref _kickRequested, 0) == 1 && !_stopped) Kick();
        }
    }

    /// <summary>
    /// The runs in the playing account's normal profiles that the site doesn't have and this uploader hasn't sent
    /// or queued, up to what the queue has room for. Says whether any were left over for the next lot. The
    /// "modded" profiles are skipped: the game doesn't record which mods a run was played with, and runs this mod
    /// watched were sent as they ended. A history file is named by the run's start time, which is also how the
    /// site tells runs apart.
    /// </summary>
    private async Task<bool> BackfillAsync(string steamDir)
    {
        (string steamId, HashSet<long> have) = await SiteRunsAsync();

        string sentPath = Path.Combine(_queueDir, SentFile);
        HashSet<string> skip = File.Exists(sentPath) ? File.ReadAllLines(sentPath).ToHashSet() : [];
        string[] queued = Directory.GetFiles(_queueDir, "*.json");
        skip.UnionWith(queued.Select(f => Path.GetFileNameWithoutExtension(f)));

        string accountDir = Path.Combine(steamDir, steamId);
        List<(long Start, string Path)> missing = (Directory.Exists(accountDir)
                ? Directory.GetFiles(accountDir, "*.run", SearchOption.AllDirectories)
                : [])
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "history")
            .Where(f => Path.GetRelativePath(accountDir, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] != "modded")
            .Select(f => (Ok: long.TryParse(Path.GetFileNameWithoutExtension(f), out long start), Start: start, Path: f))
            .Where(r => r.Ok && !have.Contains(r.Start) && !skip.Contains(r.Start.ToString()))
            .DistinctBy(r => r.Start)
            .OrderBy(r => r.Start)
            .Select(r => (r.Start, r.Path))
            .ToList();

        int room = Math.Max(0, MaxQueuedRuns - queued.Length);
        int added = 0;
        foreach ((long _, string path) in missing.Take(room))
        {
            try
            {
                Enqueue(File.ReadAllText(path), mods: null);
                added++;
            }
            catch (JsonException)
            {
                _log($"Skipping {Path.GetFileName(path)}: it isn't a readable run.");
            }
        }

        _log(missing.Count == 0
            ? $"The site has all {have.Count} run(s) it should; nothing to backfill."
            : $"The site is missing {missing.Count} run(s) from the game's history; queued {added}" +
              (missing.Count > room ? ", the rest once those are sent." : "."));
        return missing.Count > room;
    }

    /// <summary>Who is playing, and the start time of every run the site already has for them.</summary>
    private async Task<(string SteamId, HashSet<long> Runs)> SiteRunsAsync()
    {
        await RequestSlotAsync();
        Ticket ticket = await _getTicket();
        try
        {
            _requests.Add(DateTime.UtcNow);
            string ask = new JsonObject { ["steamId"] = ticket.SteamId, ["ticket"] = ticket.Hex }.ToJsonString();
            Uri runsEndpoint = new(_endpoint.AbsoluteUri.TrimEnd('/') + "/runs");
            using HttpResponseMessage answer = await Http.PostAsync(runsEndpoint, new StringContent(ask, Encoding.UTF8, "application/json"));
            string text = await answer.Content.ReadAsStringAsync();
            if (!answer.IsSuccessStatusCode) throw new HttpRequestException($"{(int)answer.StatusCode} {Truncate(text)}");

            HashSet<long> runs = JsonNode.Parse(text)!["runs"]!.AsArray().Select(n => n!.GetValue<long>()).ToHashSet();
            return (ticket.SteamId, runs);
        }
        finally
        {
            ticket.Release();
        }
    }

    /// <summary>The oldest queued runs, after dropping any beyond what the queue is allowed to hold.</summary>
    private List<string> NextBatch()
    {
        List<string> queued = Directory.GetFiles(_queueDir, "*.json").OrderBy(File.GetCreationTimeUtc).ToList();
        while (queued.Count > MaxQueuedRuns)
        {
            _log($"Queue is full; dropping {Path.GetFileName(queued[0])}. Its .run file is still in the game's history.");
            File.Delete(queued[0]);
            queued.RemoveAt(0);
        }

        return queued.Take(_batchSize).ToList();
    }

    /// <summary>
    /// Wait, if need be, until fewer than MaxRequestsPerHour requests were made in the last hour. A big
    /// history takes a while this way; a mod gone wrong can't ask more often than that however long it runs.
    /// </summary>
    private async Task RequestSlotAsync()
    {
        DateTime now = DateTime.UtcNow;
        _requests.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        if (_requests.Count < MaxRequestsPerHour) return;

        TimeSpan wait = _requests.Min() + TimeSpan.FromHours(1) - now + TimeSpan.FromSeconds(1);
        _log($"Already asked {MaxRequestsPerHour} times in the last hour. Waiting {wait.TotalMinutes:0} min.");
        await Task.Delay(wait);
    }

    private async Task<Outcome> SendAsync(List<string> batch)
    {
        // As many runs as fit: halve the batch until it compresses under the limit.
        byte[] body = Gzip(string.Join("\n", batch.Select(File.ReadAllText)));
        while (body.Length > MaxUploadBytes && batch.Count > 1)
        {
            batch.RemoveRange(batch.Count / 2, batch.Count - batch.Count / 2);
            body = Gzip(string.Join("\n", batch.Select(File.ReadAllText)));
        }

        await RequestSlotAsync();
        Ticket ticket = await _getTicket();
        try
        {
            _requests.Add(DateTime.UtcNow);
            string ask = new JsonObject { ["steamId"] = ticket.SteamId, ["ticket"] = ticket.Hex }.ToJsonString();
            using HttpResponseMessage answer = await Http.PostAsync(_endpoint, new StringContent(ask, Encoding.UTF8, "application/json"));
            string text = await answer.Content.ReadAsStringAsync();
            if (!answer.IsSuccessStatusCode)
            {
                _log($"Server said no: {(int)answer.StatusCode} {Truncate(text)}");
                return IsWorthRetrying(answer.StatusCode) ? Outcome.Retry : Outcome.Stop;
            }

            JsonObject auth = JsonNode.Parse(text)!.AsObject();
            if (body.Length > auth["maxBytes"]!.GetValue<long>())
            {
                if (batch.Count == 1)
                {
                    _log($"{Path.GetFileName(batch[0])} is {body.Length} bytes compressed, over the limit. Dropping it.");
                    File.Delete(batch[0]);
                    return Outcome.Sent;
                }

                _batchSize = Math.Max(1, batch.Count / 2);
                return Outcome.Retry;
            }

            Uri uploadUrl = new(_endpoint, auth["url"]!.GetValue<string>());
            using HttpResponseMessage stored = await Http.PostAsync(uploadUrl, UploadForm(auth["fields"]!.AsObject(), body));
            if (!stored.IsSuccessStatusCode)
            {
                _log($"Storing the upload failed: {(int)stored.StatusCode} {Truncate(await stored.Content.ReadAsStringAsync())}");
                return Outcome.Retry;
            }

            File.AppendAllLines(Path.Combine(_queueDir, SentFile), batch.Select(p => Path.GetFileNameWithoutExtension(p)));
            foreach (string path in batch) File.Delete(path);
            _log($"Uploaded {batch.Count} run(s), {body.Length} bytes, as upload {auth["uploadId"]}.");
            return Outcome.Sent;
        }
        finally
        {
            ticket.Release();
        }
    }

    /// <summary>
    /// Too soon, a clash with another upload, or the server's own trouble: all worth another try. Anything else
    /// (a ticket Steam won't confirm) will get the same answer however often it's asked.
    /// </summary>
    private static bool IsWorthRetrying(HttpStatusCode status)
    {
        return status is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict || (int)status >= 500;
    }

    private static byte[] Gzip(string text)
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Optimal))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The form S3 expects for a presigned POST: every field the server handed back, exactly as given, and the
    /// file last. Built by hand because S3 is strict about a form's shape (unquoted boundary, quoted names).
    /// </summary>
    private static ByteArrayContent UploadForm(JsonObject fields, byte[] file)
    {
        string boundary = "----slaymystats" + Guid.NewGuid().ToString("N");
        using MemoryStream form = new();

        void Write(string text) => form.Write(Encoding.UTF8.GetBytes(text));

        foreach ((string name, JsonNode? value) in fields)
        {
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value!.GetValue<string>()}\r\n");
        }

        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"runs.ndjson.gz\"\r\n" +
              "Content-Type: application/gzip\r\n\r\n");
        form.Write(file);
        Write($"\r\n--{boundary}--\r\n");

        ByteArrayContent content = new(form.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        return content;
    }

    private static string Truncate(string text)
    {
        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
