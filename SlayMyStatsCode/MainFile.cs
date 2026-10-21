using System.Reflection;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SlayMyStats.SlayMyStatsCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SlayMyStats";

    // Long enough for the game to have Steam up before the first ticket is asked for.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    /// <summary>One per endpoint in the config, each with its own queue. Empty when uploads are off.</summary>
    public static List<Uploader> Uploaders { get; } = [];

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        List<string> sites = [];

        // Nothing below may stop the game from loading.
        try
        {
            string modDir = ModDir();
            ModConfig? config = ModConfig.Load(modDir, message => Logger.Info(message));
            foreach ((string name, string url) in config?.Endpoints ?? [])
            {
                // Separate queues, so a site that is down or says no never holds runs back from the others.
                string queueDir = Path.Combine(OS.GetUserDataDir(), ModId, "queue", name);
                Uploaders.Add(new Uploader(new Uri(url), queueDir, SteamTicket.GetAsync,
                    message => Logger.Info($"[{name}] {message}"), Version(modDir)));
                sites.Add(name);
            }

            // Runs a previous session couldn't send go out now, with any the site is missing from the history.
            string steamDir = Path.Combine(OS.GetUserDataDir(), "steam");
            _ = Task.Delay(StartupDelay).ContinueWith(_ => Uploaders.ForEach(u =>
            {
                u.Backfill(steamDir);
                u.Kick();
            }));
        }
        catch (Exception e)
        {
            Logger.Error($"Uploads are off: {e}");
        }

        Logger.Info($"Slay My Stats loaded. Uploads are {(Uploaders.Count == 0 ? "off" : $"on, to {string.Join(" and ", sites)}")}.");
    }

    /// <summary>The folder this mod was installed to: where its .dll, manifest and config sit.</summary>
    private static string ModDir()
    {
        string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(dir)) return dir;

        // An assembly loaded from memory has no location; mods live in mods/<id> beside the game.
        return Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "mods", ModId);
    }

    /// <summary>The version in the manifest, so it is written down in one place.</summary>
    private static string Version(string modDir)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(modDir, $"{ModId}.json")))?["version"]?.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
