namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Which in-game stats the player wants, from three keys in their config.json:
///
///   showCardStats      true; card and relic win and pick rates
///   showRunStrength    true; how the run is keeping up with winning runs
///   showSynergyHints   true; what a card gives the deck or wants from it
///
/// Separate from uploading: turning uploads off, either way, leaves these alone. A config.json that doesn't
/// parse (or has a key of the wrong kind) turns all three off, with the notice <see cref="UploadSettings"/>
/// raises for it. Read from the file each time, so a hand edit applies without restarting.
/// </summary>
public sealed record DisplaySettings(bool CardStats, bool RunStrength, bool SynergyHints)
{
    public const string CardStatsKey = "showCardStats";
    public const string RunStrengthKey = "showRunStrength";
    public const string SynergyHintsKey = "showSynergyHints";

    private static readonly DisplaySettings Off = new(false, false, false);

    /// <summary>Whether anything shows, and so whether the snapshot is worth downloading.</summary>
    public bool Any => CardStats || RunStrength || SynergyHints;

    /// <summary>Read the player's config now. Never throws.</summary>
    public static DisplaySettings Read(string userFolder, Action<string> log)
    {
        if (UploadSettings.Read(userFolder, log).Error != null) return Off;
        var root = ConfigEditor.Read(Path.Combine(userFolder, ModConfig.UserFileName));
        if (root == null) return Off;
        bool On(string key) => root[key]?.GetValue<bool>() ?? true;
        try
        {
            return new DisplaySettings(On(CardStatsKey), On(RunStrengthKey), On(SynergyHintsKey));
        }
        catch (Exception)
        {
            return Off; // the file changed between the two reads
        }
    }
}
