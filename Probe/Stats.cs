using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SlayMyStats.SlayMyStatsCode;

namespace SlayMyStats.Probe;

/// <summary>The in-game stats pieces, without the game.</summary>
public static class Stats
{
    /// <summary>Reads every run in a file written by the site repo's tools/power_curve/ruler_fixture.py and fails
    /// unless Ruler reads what the site's Python did.</summary>
    public static int Parity(string file)
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(file));
        RulerData demand = fixture.RootElement.GetProperty("ruler").Deserialize<RulerData>()!;
        int cases = 0, wrong = 0, numbers = 0;

        foreach (JsonElement one in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            RunRecord run = one.GetProperty("run").Deserialize<RunRecord>()!;
            JsonElement expect = one.GetProperty("expect");
            List<string> found = [];
            void Same(string what, double got, JsonElement want)
            {
                numbers++;
                double value = want.GetDouble();
                if (Math.Abs(got - value) > 1e-9 * Math.Max(1, Math.Abs(value))) found.Add($"{what} {got} not {value}");
            }
            void Count(string what, int got, JsonElement list)
            {
                if (got != list.GetArrayLength()) found.Add($"{what}: {got} not {list.GetArrayLength()}");
            }
            void Text(string what, string? got, JsonElement want)
            {
                if (got != want.GetString()) found.Add($"{what} {got ?? "none"} not {want.GetString() ?? "none"}");
            }
            void Sums(string what, Totals got, JsonElement want)
            {
                Same($"{what} turns", got.Turns, want.GetProperty("turns"));
                Same($"{what} winTurns", got.WinTurns, want.GetProperty("winTurns"));
                Same($"{what} taken", got.Taken, want.GetProperty("taken"));
                Same($"{what} winTaken", got.WinTaken, want.GetProperty("winTaken"));
                Same($"{what} pace", got.Pace, want.GetProperty("pace"));
                Same($"{what} guard", got.Guard, want.GetProperty("guard"));
            }

            List<Fight> fights = Ruler.RunFights(run);
            Same("fights", fights.Count, expect.GetProperty("fights"));

            List<Reading> readings = Ruler.Readings(fights, demand);
            JsonElement[] rows = expect.GetProperty("readings").EnumerateArray().ToArray();
            Count("readings", readings.Count, expect.GetProperty("readings"));
            foreach ((Reading r, JsonElement want) in readings.Zip(rows))
            {
                Text("reading enc", r.Encounter, want.GetProperty("enc"));
                Same("reading act", r.Act, want.GetProperty("act"));
                Same("ask", r.Ask, want.GetProperty("ask"));
                Same("doing", r.Doing, want.GetProperty("doing"));
                Same("damage", r.Damage, want.GetProperty("damage"));
            }

            int who = 0;
            foreach (JsonElement player in expect.GetProperty("players").EnumerateArray())
            {
                int me = who++;
                List<ActReading> acts = Ruler.ActReadings(fights, demand, f => f.Each[me]);
                Count($"player {me} acts", acts.Count, player.GetProperty("acts"));
                foreach ((ActReading a, JsonElement want) in acts.Zip(player.GetProperty("acts").EnumerateArray()))
                {
                    string what = $"player {me} act {a.Act}";
                    Same($"{what} number", a.Act, want.GetProperty("act"));
                    Same($"{what} fights", a.Fights, want.GetProperty("fights"));
                    Sums(what, a, want);
                    Same($"{what} each", a.Each, want.GetProperty("each"));
                    Text($"{what} speed", a.Speed, want.GetProperty("speed"));
                    Text($"{what} held", a.Held, want.GetProperty("held"));
                    Text($"{what} verdict", a.Verdict, want.GetProperty("verdict"));
                    Text($"{what} lacking", a.Lacking, want.GetProperty("lacking"));
                }

                RunReading? whole = Ruler.ReadRun(acts, demand.Cuts);
                JsonElement expected = player.GetProperty("run");
                if (whole == null || expected.ValueKind == JsonValueKind.Null)
                {
                    if (whole != null || expected.ValueKind != JsonValueKind.Null) found.Add($"player {me} run reading");
                    continue;
                }
                Sums($"player {me} run", whole.Totals, expected);
                Text($"player {me} run verdict", whole.Verdict, expected.GetProperty("verdict"));
                if (whole.Op != expected.GetProperty("op").GetBoolean()) found.Add($"player {me} op");
            }

            cases++;
            if (found.Count == 0) continue;
            wrong++;
            if (wrong <= 10) Console.WriteLine($"FAIL run {cases}: {string.Join("; ", found.Take(5))}");
        }

        Console.WriteLine($"{cases} runs, {numbers} numbers compared, {wrong} runs differ.");
        return cases > 0 && wrong == 0 ? 0 : 1;
    }

    /// <summary>Loads the snapshot in a folder and asks an address for a newer one, as the mod does at launch.</summary>
    public static async Task<int> Fetch(string folder, Uri url)
    {
        PackStore store = new(folder, _ => null, Console.WriteLine);
        store.Load();
        using HttpClient http = new();
        await store.Refresh(url, http);
        if (store.Current is not { } pack) return 1;
        Console.WriteLine($"{pack.Data.Ruler!.Encounters.Count} encounters; lanes " +
                          string.Join(", ", pack.Data.Lanes!.Select(l => $"{l.Key}: {l.Value.Cards.Count} cards")));
        return 0;
    }

    /// <summary>The snapshot cache and the display settings against made-up files and a server on this computer:
    /// fresh install, offline, unchanged, stale and damaged.</summary>
    public static async Task<int> SelfTest()
    {
        bool ok = true;
        void Check(string name, bool pass)
        {
            ok &= pass;
            Console.WriteLine($"{(pass ? "ok  " : "FAIL")} {name}");
        }

        PackData rated = JsonSerializer.Deserialize<PackData>("""
            {"v":1,"lanes":{"solo":{"cards":{"A":[200,81],"B":[19,19]},"relics":{"R":[20,5]},
              "picks":{"X":{"cards":{"A":[3,0,15,0],"B":[1,0,19,0]},"relics":{}},
                       "Y":{"cards":{"A":[7,0,25,0]},"relics":{}}}}}}
            """)!;
        Check("rates: wins and picks summed over characters", Rates.Of(rated, false, "A", false) == new Rates(41, 25));
        Check("rates: under 20 runs and offers shows nothing", Rates.Of(rated, false, "B", false) == null);
        Check("rates: a relic with no offers shows its win rate", Rates.Of(rated, false, "R", true) == new Rates(25, null));
        Check("rates: an unknown id or missing lane shows nothing",
            Rates.Of(rated, false, "Z", false) == null && Rates.Of(rated, true, "A", false) == null);

        RulerData ruler = new()
        {
            Encounters = new() { ["E"] = new() { Tiers = [new Line { Turns = 4, Taken = 10 }], Pool = [40] } },
            Cuts = new() { Acts = [], Run = new() { Guard = [0.8, 1.2], Pace = [0.8, 1.2] } },
        };
        RunRecord Played(int fights) => new()
        {
            InProgress = true,
            Players = [new RunPlayer()],
            Acts = [Enumerable.Repeat(new RunPoint
            {
                Type = "monster",
                Stats = [new PointStats { DamageTaken = 2 }],
                Rooms = [new RunRoom { ModelId = "E", MonsterIds = ["M"], Turns = 4 }],
            }, fights).ToList()],
        };
        Check("strength: no fight, no reading", Strength.Of(Played(0), ruler, 0, 0) == null);
        Check("strength: one fight reads the act",
            Strength.Of(Played(1), ruler, 0, 0) is { Current.Act: 0, Fights.Count: 1 });
        Check("strength: three do, with a line for each",
            Strength.Of(Played(3), ruler, 0, 0) is { Current.Verdict: Ruler.Ahead, Fights.Count: 3, Earlier.Count: 0 });
        Check("strength: a new act keeps the last reading until its first fight",
            Strength.Of(Played(3), ruler, 1, 0) is { Current.Act: 0, Earlier.Count: 0 });

        string root = Directory.CreateTempSubdirectory("smst-stats").FullName;
        List<string> log = [];
        using FakeSite site = new();
        using HttpClient http = new();

        (byte[] Body, string Name) Synergy(string tag)
        {
            byte[] json = Encoding.UTF8.GetBytes($"{{\"v\":1,\"synergy\":{{\"tag\":\"{tag}\"}}}}");
            return (Zip(json), $"synergy.{Convert.ToHexString(SHA256.HashData(json))[..10].ToLowerInvariant()}.json");
        }
        byte[] Pack(long built, string? synergy, int version = 1) => Zip(Encoding.UTF8.GetBytes(
            $"{{\"v\":{version},\"built\":{built},\"synergy\":{(synergy == null ? "null" : $"\"{synergy}\"")}," +
            "\"ruler\":{\"encounters\":{},\"cuts\":{\"acts\":[],\"run\":{\"guard\":[1,2],\"pace\":[1,2]}}},\"lanes\":{}}"));
        PackStore Store(string folder, Dictionary<string, byte[]>? shipped = null) =>
            new(folder, name => shipped?.GetValueOrDefault(name), log.Add);
        string Tag(PackStore s) => s.Current?.Synergy?["synergy"]?["tag"]?.ToString() ?? "none";
        long Built(PackStore s) => s.Current?.Data.Built ?? 0;
        TimeSpan quick = TimeSpan.FromSeconds(1);

        var (oldBody, oldName) = Synergy("old");
        var (newBody, newName) = Synergy("new");
        Uri nowhere = new("http://127.0.0.1:1/mod/v1/pack.json.gz");
        Dictionary<string, byte[]> bundle = new() { ["pack.json.gz"] = Pack(100, oldName), ["synergy.json"] = oldBody };

        string bare = Path.Combine(root, "bare");
        PackStore store = Store(bare);
        await store.Refresh(nowhere, http, quick);
        Check("fresh install, offline, nothing shipped: no snapshot", store.Current == null);

        string folder = Path.Combine(root, "cache");
        store = Store(folder, bundle);
        store.Load();
        await store.Refresh(nowhere, http, quick);
        Check("fresh install, offline: the shipped one", Built(store) == 100 && Tag(store) == "old" &&
                                                       store.Current!.Source == "shipped");

        site.Pack = Pack(200, oldName);
        site.Etag = "\"a\"";
        site.Files[oldName] = oldBody;
        await store.Refresh(site.Url, http, quick);
        Check("first download", Built(store) == 200 && Tag(store) == "old" && store.Current!.Source == "downloaded" &&
                                File.Exists(Path.Combine(folder, oldName)) &&
                                File.ReadAllText(Path.Combine(folder, "pack.etag")) == "\"a\"");

        await store.Refresh(site.Url, http, quick);
        Check("unchanged: asked with the ETag, answered 304", site.LastAsked == "\"a\"" && site.LastStatus == 304 &&
                                                              Built(store) == 200);

        PackStore later = Store(folder, bundle);
        later.Load();
        await later.Refresh(nowhere, http, quick);
        Check("offline with a download: it is used", Built(later) == 200);

        void Kept(string name) => Check(name, Built(store) == 200 && Tag(store) == "old" &&
                                              File.ReadAllText(Path.Combine(folder, "pack.etag")) == "\"a\"" &&
                                              !Directory.GetFiles(folder, "*.tmp").Any());
        site.Etag = "\"b\"";
        site.Pack = Encoding.UTF8.GetBytes("<html>not a snapshot</html>");
        await store.Refresh(site.Url, http, quick);
        Kept("damaged download: the old one stays");
        site.Pack = Pack(300, oldName)[..40];
        await store.Refresh(site.Url, http, quick);
        Kept("cut-off download: the old one stays");
        site.Pack = Pack(300, oldName, version: 2);
        await store.Refresh(site.Url, http, quick);
        Kept("a newer format: the old one stays");
        site.Pack = Pack(300, "../../evil.json");
        await store.Refresh(site.Url, http, quick);
        Kept("a synergy name that is a path: refused");
        site.Pack = new byte[PackStore.MaxDownload + 1];
        await store.Refresh(site.Url, http, quick);
        Kept("too big a download: refused");
        site.Pack = Zip(new byte[PackStore.MaxUnzipped + 1]);
        await store.Refresh(site.Url, http, quick);
        Kept("too big once unzipped: refused");
        site.Pack = Pack(300, oldName);
        site.Status = 500;
        await store.Refresh(site.Url, http, quick);
        Kept("server error: the old one stays");
        site.Status = null;
        site.Delay = TimeSpan.FromSeconds(3);
        await store.Refresh(site.Url, http, quick);
        Kept("server too slow: the old one stays");
        site.Delay = TimeSpan.Zero;

        site.Pack = Pack(300, newName);
        site.Files[newName] = oldBody; // not what the name says
        await store.Refresh(site.Url, http, quick);
        Check("synergy that doesn't match its name: older synergy kept",
            Built(store) == 300 && Tag(store) == "old" && !File.Exists(Path.Combine(folder, newName)));

        site.Files[newName] = newBody;
        await store.Refresh(site.Url, http, quick);
        Check("missing synergy is fetched after a 304, and the old one removed", site.LastPackStatus == 304 &&
            Tag(store) == "new" && !File.Exists(Path.Combine(folder, oldName)));

        File.WriteAllText(Path.Combine(folder, "pack.json.gz"), "damaged");
        PackStore damaged = Store(folder, bundle);
        damaged.Load();
        Check("damaged cache: the shipped one", Built(damaged) == 100);
        await damaged.Refresh(site.Url, http, quick);
        Check("damaged cache: downloaded again, without the ETag", site.LastAsked == null && Built(damaged) == 300);

        Dictionary<string, byte[]> newer = new() { ["pack.json.gz"] = Pack(900, newName), ["synergy.json"] = newBody };
        PackStore updated = Store(folder, newer);
        updated.Load();
        Check("a mod update ships a newer snapshot than the cache: the shipped one",
            Built(updated) == 900 && updated.Current!.Source == "shipped");

        // Settings.
        string user = Path.Combine(root, "user");
        string config = Path.Combine(user, "config.json");
        DisplaySettings Read() => DisplaySettings.Read(user, log.Add);
        Check("new config: everything on", Read() == new DisplaySettings(true, true, true) &&
                                           File.ReadAllText(config).Contains("\"showSynergyHints\": true"));
        File.WriteAllText(config, "{\"uploadsEnabled\": true}");
        Check("a config from before these keys: everything on", Read() == new DisplaySettings(true, true, true));
        File.WriteAllText(config, "{// mine\n\"uploadsEnabled\": false, \"showRunStrength\": false,}");
        Check("one switched off, uploads off", Read() == new DisplaySettings(true, false, true));
        File.WriteAllText(config, "{\"showCardStats\": \"no\"}");
        Check("wrong kind of value: all off, with the notice", !Read().Any && UploadSettings.Notice != null);
        File.WriteAllText(config, "{,,");
        Check("broken config: all off, with the notice", !Read().Any && UploadSettings.Notice != null);

        string mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, ModConfig.FileName),
            "{\"enabled\": false, \"statsUrl\": \"https://example.com/pack\"}");
        File.WriteAllText(config, "{}");
        Check("shipped statsUrl, even with uploads off",
            ModConfig.StatsUrl(user, mod)?.ToString() == "https://example.com/pack");
        File.WriteAllText(config, "{\"statsUrl\": \"http://127.0.0.1:9/p\", \"debug\": true,}");
        Check("the player's statsUrl and debug win, trailing comma and all",
            ModConfig.StatsUrl(user, mod)?.Port == 9 && ModConfig.DebugEnabled(user, mod));
        File.WriteAllText(config, "{\"statsUrl\": \"file:///c:/x\"}");
        Check("only web addresses", ModConfig.StatsUrl(user, mod) == null);

        Directory.Delete(root, recursive: true);
        if (!ok) log.ForEach(Console.WriteLine);
        Console.WriteLine(ok ? "stats self-test OK" : "stats self-test WRONG");
        return ok ? 0 : 1;
    }

    private static byte[] Zip(byte[] bytes)
    {
        MemoryStream into = new();
        using (GZipStream zip = new(into, CompressionLevel.Fastest)) zip.Write(bytes);
        return into.ToArray();
    }

    /// <summary>The two files the site serves, with the failures to order.</summary>
    private sealed class FakeSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Uri Url { get; }
        public byte[] Pack = [];
        public string Etag = "";
        public Dictionary<string, byte[]> Files { get; } = [];
        public int? Status;
        public TimeSpan Delay;
        public string? LastAsked;
        public int LastStatus, LastPackStatus;

        public FakeSite()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{port}/mod/v1/pack.json.gz");
            _ = Serve();
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                try
                {
                    _ = Answer(await _listener.GetContextAsync());
                }
                catch (Exception)
                {
                    // the test is over
                }
            }
        }

        // Each on its own, so one slow answer doesn't hold up the next request.
        private async Task Answer(HttpListenerContext call)
        {
            try
            {
                await Task.Delay(Delay);
                string name = call.Request.Url!.Segments[^1];
                bool pack = name == "pack.json.gz";
                byte[]? body = pack ? Pack : Files.GetValueOrDefault(name);
                int status = Status ?? (body == null ? 404 : 200);
                if (pack)
                {
                    LastAsked = call.Request.Headers["If-None-Match"];
                    if (status == 200 && LastAsked == Etag) status = 304;
                    call.Response.Headers["ETag"] = Etag;
                    LastPackStatus = status;
                }
                LastStatus = status;
                call.Response.StatusCode = status;
                if (status == 200)
                {
                    call.Response.ContentLength64 = body!.Length;
                    await call.Response.OutputStream.WriteAsync(body);
                }
                call.Response.Close();
            }
            catch (Exception)
            {
                // the caller gave up, or the test is over
            }
        }

        public void Dispose() => _listener.Close();
    }
}
