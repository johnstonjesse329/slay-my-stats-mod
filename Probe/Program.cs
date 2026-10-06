using SlayMyStats.SlayMyStatsCode;
using Steamworks;

namespace SlayMyStats.Probe;

/// <summary>
/// Tries the mod's upload path without the game: the same Uploader and SteamTicket code, run from a console.
///
///   SlayMyStatsProbe ticket
///       Ask Steam for a ticket and say whether one came back. Sends nothing anywhere.
///
///   SlayMyStatsProbe upload &lt;endpoint&gt; &lt;file.run&gt;... [--as &lt;steamid&gt;]
///       Queue the given runs and upload them, exactly as the mod would at the end of a run.
///       --as skips Steam and claims that id with a made-up ticket, which only a local dev server accepts.
///
/// Anything that asks Steam for a ticket needs Steam running, and shows you as playing the game while it runs.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
            return claim != null
                ? await Upload(endpoint, rest, () => Task.FromResult(new Uploader.Ticket(claim, new string('0', 64), () => { })))
                : await WithSteam(() => Upload(endpoint, rest, SteamTicket.GetAsync));
        }

        Console.Error.WriteLine("usage: SlayMyStatsProbe ticket\n" +
                                "       SlayMyStatsProbe upload <endpoint> <file.run>... [--as <steamid>]");
        return 2;
    }

    private static async Task<int> Upload(Uri endpoint, List<string> runFiles, Func<Task<Uploader.Ticket>> getTicket)
    {
        string queueDir = Path.Combine(Path.GetTempPath(), "SlayMyStatsProbe", Guid.NewGuid().ToString("N"));
        Uploader uploader = new(endpoint, queueDir, getTicket, Console.WriteLine, "probe");
        foreach (string file in runFiles) uploader.Enqueue(File.ReadAllText(file), mods: []);  // no game, so no mods to report

        await uploader.DrainAsync();

        int left = Directory.GetFiles(queueDir, "*.json").Length;
        Console.WriteLine(left == 0 ? "Queue is empty: everything was sent." : $"{left} run(s) still queued in {queueDir}.");
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
