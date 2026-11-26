using SlayMyStats.SlayMyStatsCode;
using Steamworks;

namespace SlayMyStats.Probe;

/// <summary>
/// Tries the mod's upload path without the game: the same Uploader and SteamTicket code, run from a console.
///
///   SlayMyStatsProbe config <folder>
///       Read the upload settings (config.json) in the folder as the mod does, creating it if missing.
///
///   SlayMyStatsProbe health
///       Self-test of the debug health checks (RunHealth) on made-up event logs. Needs no game or Steam.
///
///   SlayMyStatsProbe ruler <fixture.json>
///       Check the mod's ruler against the site's: the fixture comes from the site repo's
///       tools/power_curve/ruler_fixture.py (Probe/ruler_fixture.json is the committed one).
///
///   SlayMyStatsProbe stats [<folder> <pack address>]
///       Self-test of the stats snapshot cache and display settings against a server on this computer. With a
///       folder and address, loads and downloads there as the mod does at launch.
///
///   SlayMyStatsProbe ticket
///       Ask Steam for a ticket and say whether one came back. Sends nothing anywhere.
///
///   SlayMyStatsProbe upload &lt;endpoint&gt; &lt;file.run&gt;... [--as &lt;steamid&gt;]
///       Queue the given runs and upload them, exactly as the mod would at the end of a run.
///       --as skips Steam and claims that id with a made-up ticket, which only a local dev server accepts.
///
///   SlayMyStatsProbe backfill &lt;endpoint&gt; &lt;steam folder&gt;
///       Ask the site which runs it has, queue the rest from the history under the steam folder (the game's
///       %APPDATA%\SlayTheSpire2\steam), and upload them, as the mod does at launch. The queue and its record of
///       what was sent stay in %TEMP%\SlayMyStatsProbe\backfill-&lt;host&gt; between tries.
///
/// Anything that asks Steam for a ticket needs Steam running, and shows you as playing the game while it runs.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["config", var folder])
        {
            UploadSettings settings = UploadSettings.Read(folder, Console.WriteLine);
            Console.WriteLine($"enabled={settings.Enabled} profiles={(settings.Profiles == null ? "all" : string.Join(",", settings.Profiles))} " +
                              $"shareIds={settings.ShareMultiplayerIds} error={settings.Error ?? "none"} notice={UploadSettings.Notice ?? "none"}");
            return 0;
        }

        if (args is ["health"]) return Health();

        if (args is ["configedit"]) return ConfigEdit();

        if (args is ["ruler", var fixture]) return Stats.Parity(fixture);

        if (args is ["stats"]) return await Stats.SelfTest();

        if (args is ["stats", var cache, var packUrl]) return await Stats.Fetch(cache, new Uri(packUrl));

        if (args is ["ticket"])
        {
            return await WithSteam(async () =>
            {
                Uploader.Ticket ticket = await SteamTicket.GetAsync();
                Console.WriteLine($"Steam says the player is {ticket.SteamId}; ticket is {ticket.Hex.Length / 2} bytes.");
                ticket.Release();
                return 0;
            });
        }

        if (args.Length >= 3 && args[0] == "upload")
        {
            List<string> rest = args.Skip(2).ToList();
            string? claim = null;
            int flag = rest.IndexOf("--as");
            if (flag >= 0 && flag + 1 < rest.Count)
            {
                claim = rest[flag + 1];
                rest.RemoveRange(flag, 2);
            }

            Uri endpoint = new(args[1]);
            string? configDir = Take(rest, "--config");
            string? profile = Take(rest, "--profile");
            Func<UploadSettings>? settings = configDir == null ? null : () => UploadSettings.Read(configDir, Console.WriteLine);
            return claim != null
                ? await Upload(endpoint, rest, () => Task.FromResult(new Uploader.Ticket(claim, new string('0', 64), () => { })), settings, profile)
                : await WithSteam(() => Upload(endpoint, rest, SteamTicket.GetAsync, settings, profile));
        }

        if (args is ["backfill", var url, var steamDir])
        {
            Uri endpoint = new(url);
            return await WithSteam(() => Backfill(endpoint, steamDir));
        }

        Console.Error.WriteLine("usage: SlayMyStatsProbe ticket\n" +
                                "       SlayMyStatsProbe upload <endpoint> <file.run>... [--as <steamid>]\n" +
                                "       SlayMyStatsProbe backfill <endpoint> <steam folder>");
        return 2;
    }

    /// <summary>Changes keys in made-up config files and fails unless only the asked-for value moved, and a broken
    /// file stayed as it was.</summary>
    private static int ConfigEdit()
    {
        string folder = Directory.CreateTempSubdirectory("smst-configedit").FullName;
        string path = Path.Combine(folder, "config.json");
        bool ok = true;
        void Case(string name, string? before, string key, bool value, string? after)
        {
            if (before == null) File.Delete(path);
            else File.WriteAllText(path, before);
            bool wrote = ConfigEditor.SetFlag(path, key, value);
            // Decoded from the bytes, so a byte order mark shows.
            string got = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
            bool pass = wrote == (after != null) && got == (after ?? before);
            ok &= pass;
            Console.WriteLine($"{(pass ? "ok  " : "FAIL")} {name}{(pass ? "" : $": wrote={wrote} got={got}")}");
        }

        Case("change one value, keep comments and other keys",
            "{\r\n  // mine\r\n  \"a\": true, // note\r\n  \"profiles\": [\"profile1\"],\r\n}\r\n", "a", false,
            "{\r\n  // mine\r\n  \"a\": false, // note\r\n  \"profiles\": [\"profile1\"],\r\n}\r\n");
        Case("add a missing key", "{\n  \"a\": true\n}\n", "b", false, "{\n  \"b\": false,\n  \"a\": true\n}\n");
        Case("add to an empty object", "{}", "b", true, "{\n  \"b\": true}");
        Case("leave a nested key of the same name", "{\"x\":{\"a\":true},\"a\":true}", "a", false,
            "{\"x\":{\"a\":true},\"a\":false}");
        Case("replace a value of the wrong kind", "{\"a\": \"yes\", \"b\": 1}", "a", true, "{\"a\": true, \"b\": 1}");
        Case("make a missing file", null, "a", false, "{\n  \"a\": false\n}\n");
        Case("keep a byte order mark", "﻿{\"a\":true}", "a", false, "﻿{\"a\":false}");
        Case("never write a broken file", "{\"a\": true,, }", "a", false, null);
        Case("never write a file that is not an object", "[1]", "a", false, null);

        Directory.Delete(folder, recursive: true);
        Console.WriteLine(ok ? "config edits OK" : "config edits WRONG");
        return ok ? 0 : 1;
    }

    /// <summary>Feeds the debug self-check a clean run, then a save-and-resume that recorded a fight twice and one
    /// that never closed, and fails unless it flags exactly those.</summary>
    private static int Health()
    {
        static string[] Run(Action<Action<string>> script)
        {
            List<string> found = [];
            RunHealth.Enabled = true;
            RunHealth.Log = m => found.Add(m);
            EventLog.Reset();
            script(key =>
            {
                EventLog.NewFight();
                EventLog.Add("fight0", new() { ["key"] = key });
                EventLog.Add("play1");
            });
            return found.ToArray();
        }

        string[] clean = Run(fight => { fight("a"); EventLog.Add("fight1"); fight("b"); EventLog.Add("fight1"); EventLog.Verify(true); });
        string[] twice = Run(fight => { fight("a"); EventLog.Add("fight1"); fight("a"); EventLog.Add("fight1"); EventLog.Verify(true); });
        string[] open = Run(fight => { fight("a"); EventLog.Verify(true); });

        // Damage dealt: this player's hits on enemies, Block included; the biggest card hit counts its overkill.
        EventLog.Reset();
        EventLog.NewFight();
        EventLog.Add("fight0");
        void Hit(int slot, int total, int over, string? card, string by = "P", string target = "Nibbit") =>
            EventLog.Add("hit", new() { ["by"] = by, ["t"] = target, ["sourceSlot"] = slot, ["enemy"] = target != "P",
                ["card"] = card, ["total"] = total, ["ovk"] = over });
        Hit(0, 12, 9, "CARD.STRIKE");
        Hit(0, 30, 0, null);                    // a Power's damage: in the total, not a card's hit
        Hit(1, 50, 0, "CARD.STRIKE");           // the other player's
        Hit(0, 40, 0, "CARD.STRIKE", target: "P");
        Hit(0, 40, 0, null, by: "Osty");
        bool dealt = EventLog.DealtBy(0, 1) == new Dealt(42, 21) && EventLog.DealtBy(0, 2) == null;
        Console.WriteLine($"damage dealt: {EventLog.DealtBy(0, 1)}");

        bool ok = dealt && clean.Length == 0
                  && twice.Length == 1 && twice[0].Contains("duplicate-fight")
                  && open.Length == 1 && open[0].Contains("fight-unclosed");
        Console.WriteLine($"clean: {clean.Length} findings; repeated fight: {string.Join("|", twice)}; unclosed: {string.Join("|", open)}");
        Console.WriteLine(ok ? "health checks OK" : "health checks WRONG");
        return ok ? 0 : 1;
    }

    private static string? Take(List<string> rest, string flag)
    {
        int i = rest.IndexOf(flag);
        if (i < 0 || i + 1 >= rest.Count) return null;
        string value = rest[i + 1];
        rest.RemoveRange(i, 2);
        return value;
    }

    private static async Task<int> Upload(Uri endpoint, List<string> runFiles, Func<Task<Uploader.Ticket>> getTicket,
        Func<UploadSettings>? settings = null, string? profile = null)
    {
        string queueDir = Path.Combine(Path.GetTempPath(), "SlayMyStatsProbe", Guid.NewGuid().ToString("N"));
        Uploader uploader = new(endpoint, queueDir, getTicket, Console.WriteLine, "probe", settings);
        foreach (string file in runFiles)
        {
            // as the run-end hook does: a run whose profile the config leaves out is never queued
            if (uploader.Wants(profile)) uploader.Enqueue(File.ReadAllText(file), mods: [], profile: profile);  // no game, so no mods to report
            else Console.WriteLine($"{Path.GetFileName(file)}: profile {profile ?? "unknown"} isn't on the list; not queued.");
        }

        await uploader.DrainAsync();

        int left = Directory.GetFiles(queueDir, "*.json").Length;
        Console.WriteLine(left == 0 ? "Queue is empty: everything was sent." : $"{left} run(s) still queued in {queueDir}.");
        return left == 0 ? 0 : 1;
    }

    private static async Task<int> Backfill(Uri endpoint, string steamDir)
    {
        string queueDir = Path.Combine(Path.GetTempPath(), "SlayMyStatsProbe", $"backfill-{endpoint.Host.Split('.')[0]}");
        Uploader uploader = new(endpoint, queueDir, SteamTicket.GetAsync, Console.WriteLine, "probe");
        uploader.Backfill(steamDir);
        await uploader.DrainAsync();

        int left = Directory.GetFiles(queueDir, "*.json").Length;
        Console.WriteLine(left == 0 ? "Queue is empty." : $"{left} run(s) still queued in {queueDir}.");
        return left == 0 ? 0 : 1;
    }

    /// <summary>Stand in for the game: start Steam, and keep its callbacks running while the work is done.</summary>
    private static async Task<int> WithSteam(Func<Task<int>> work)
    {
        if (!SteamAPI.Init())
        {
            Console.Error.WriteLine("Steam didn't start. Is the Steam client running, and signed in to an account that owns the game?");
            return 1;
        }

        using CancellationTokenSource done = new();
        Task pump = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                SteamAPI.RunCallbacks();
                await Task.Delay(50);
            }
        });

        try
        {
            return await work();
        }
        catch (Uploader.TicketException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            done.Cancel();
            await pump;
            SteamAPI.Shutdown();
        }
    }
}
