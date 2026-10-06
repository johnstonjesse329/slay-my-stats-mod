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

    /// <summary>Null when uploads are off: no config file, or turned off in it.</summary>
    public static Uploader? Uploader { get; private set; }

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // Nothing below may stop the game from loading.
        try
        {
            string modDir = ModDir();
            Action<string> log = message => Logger.Info(message);
            ModConfig? config = ModConfig.Load(modDir, log);
            if (config != null)
            {
                string queueDir = Path.Combine(OS.GetUserDataDir(), ModId, "queue");
                Uploader = new Uploader(new Uri(config.Endpoint), queueDir, SteamTicket.GetAsync, log, Version(modDir));

                // Runs a previous session couldn't send go out now.
                _ = Task.Delay(StartupDelay).ContinueWith(_ => Uploader.Kick());
            }
        }
        catch (Exception e)
        {
            Logger.Error($"Uploads are off: {e}");
        }

        Logger.Info($"Slay My Stats loaded. Uploads are {(Uploader == null ? "off" : "on")}.");
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
