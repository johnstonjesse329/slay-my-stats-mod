using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>The game's settings tickbox, bound to one true/false key of the player's config.json.</summary>
public partial class ModTickbox : NSettingsTickbox
{
    private string _path = "";
    private string _key = "";

    public static ModTickbox Create(PackedScene scene, string path, string key)
    {
        ModTickbox box = new()
        {
            Name = "SettingsTickbox",
            _path = path,
            _key = key,
            CustomMinimumSize = new Vector2(320, 64),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Pass,
        };
        // The scene's root carries no script of its own, so its parts move under ours.
        Node parts = scene.Instantiate();
        foreach (Node part in parts.GetChildren())
        {
            part.Owner = null;
            parts.RemoveChild(part);
            box.AddChild(part);
            part.Owner = box;
        }
        parts.Free();
        return box;
    }

    public override void _Ready()
    {
        ConnectSignals();
        Show(_path);
    }

    /// <summary>Tick as the file says: an absent key is on, and a file that can't be read is off.</summary>
    public void Show(string path)
    {
        var config = ConfigEditor.Read(path);
        IsTicked = config != null && !(config[_key] is { } node && node.ToJsonString() == "false");
    }

    protected override void OnTick() => Save(true);

    protected override void OnUntick() => Save(false);

    private void Save(bool value)
    {
        if (ConfigEditor.SetFlag(_path, _key, value))
        {
            MainFile.Logger.Info($"Settings: {_key} = {(value ? "true" : "false")}");
            LiveRun.ChangedSettings();
            return;
        }
        MainFile.Logger.Error($"Settings: couldn't write {_key} to {_path}");
        // The box goes back to what the file says, after the game's own toggle has finished.
        Callable.From(() => Show(_path)).CallDeferred();
    }
}
