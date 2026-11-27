using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using HttpClient = System.Net.Http.HttpClient;

namespace SlayMyStats.SlayMyStatsCode;

internal static class ModFeedback
{
    private static readonly (string Label, string Key)[] Categories =
    [
        ("Bug", "bug"),
        ("Feature request", "feature"),
        ("Improvement idea", "improvement"),
        ("Kudos", "kudos")
    ];

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(22) };
    private static readonly Color Panel = new("202d34"), Input = new("162128"), Border = new("405862"),
        Gold = new("e0c468"), Ink = new("18232a"), Paper = new("f5efdc"), Muted = new("b6c4cb");
    private static ConfirmationDialog? _dialog;

    internal static void Open(Node owner, bool fromRun = false, string? lastEncounter = null)
    {
        try
        {
            if (GodotObject.IsInstanceValid(_dialog) && !_dialog!.IsQueuedForDeletion()) return;
            Show(owner, fromRun, lastEncounter);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Couldn't open feedback: {e.GetType().Name}");
        }
    }

    private static void Show(Node owner, bool fromRun, string? lastEncounter)
    {
        string modDir = MainFile.ModDir();
        string version = MainFile.Version(modDir);
        var config = ModConfig.Load(Path.Combine(OS.GetUserDataDir(), MainFile.ModId), modDir, _ => {}, forFeedback: true);
        string site = config?.FeedbackSite ?? (config?.Endpoints.ContainsKey("prod") == true ? "prod" : "gamma");
        string? url = config?.Endpoints.GetValueOrDefault(site);
        Uri? endpoint = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? ingest) && ingest.Scheme == Uri.UriSchemeHttps)
            endpoint = new UriBuilder(ingest) { Path = ingest.AbsolutePath.TrimEnd('/') + "/feedback" }.Uri;

        string? encounter = lastEncounter;
        string? card = null;
        int? turn = null;
        if (fromRun && RunManager.Instance.DebugOnlyGetState() is { } state &&
            state.CurrentRoom is CombatRoom room)
        {
            encounter = room.Encounter.Id.ToString();
            int slot = state.Players.ToList().FindIndex(p => p.NetId == LocalContext.NetId);
            if (slot >= 0)
            {
                turn = state.Players[slot].PlayerCombatState?.TurnNumber;
                card = EventLog.LastPlayedCard(encounter, slot);
            }
            if (turn <= 0) turn = null;
        }
        string context = $"Included: mod {version}";
        if (encounter != null)
            context += $" · {encounter}" + (turn is int t ? $" · Turn {t}" : "");
        if (card != null)
            context += $"\nLast card played: {card}";
        if (site != "prod")
            context += $"\nInbox: {site}";

        ConfirmationDialog dialog = new()
        {
            Title = "Slay My Stats Mod Feedback",
            OkButtonText = "Send",
            DialogHideOnOk = false,
            Exclusive = true
        };
        Theme theme = new();
        theme.SetStylebox("panel", "AcceptDialog", Box(Panel, Border, 18, 14));
        theme.SetColor("title_color", "Window", Gold);
        theme.SetColor("font_color", "Label", Paper);
        dialog.Theme = theme;
        _dialog = dialog;
        VBoxContainer content = new() { CustomMinimumSize = new Vector2(465, 0) };
        content.AddThemeConstantOverride("separation", 12);
        OptionButton category = new() { CustomMinimumSize = new Vector2(0, 36) };
        StyleButton(category, primary: false);
        category.AddItem("Choose a category");
        category.SetItemDisabled(0, true);
        foreach (var (label, _) in Categories) category.AddItem(label);
        category.Select(0);
        TextEdit comment = new()
        {
            PlaceholderText = "What happened, or what would you like to see?",
            CustomMinimumSize = new Vector2(0, 130),
            WrapMode = TextEdit.LineWrappingMode.Boundary
        };
        Label status = new() { AutowrapMode = TextServer.AutowrapMode.Word };
        Label details = new() { Text = context, AutowrapMode = TextServer.AutowrapMode.Word };
        details.AddThemeColorOverride("font_color", Muted);
        details.AddThemeFontSizeOverride("font_size", 14);
        comment.AddThemeStyleboxOverride("normal", Box(Input, Border, 12, 10));
        comment.AddThemeStyleboxOverride("read_only", Box(Input, Border, 12, 10));
        comment.AddThemeColorOverride("font_color", Paper);
        comment.AddThemeColorOverride("font_placeholder_color", Muted);
        content.AddChild(category);
        content.AddChild(comment);
        content.AddChild(details);
        content.AddChild(status);
        dialog.AddChild(content);
        owner.GetTree().Root.AddChild(dialog);
        StyleButton(dialog.GetOkButton(), primary: true);
        StyleButton(dialog.GetCancelButton(), primary: false);
        dialog.Canceled += dialog.QueueFree;
        dialog.CloseRequested += dialog.QueueFree;

        bool sending = false, sent = false;
        void Update()
        {
            int length = comment.Text.Trim().Length;
            dialog.GetOkButton().Disabled = !sent && (sending || endpoint == null || category.Selected < 1 ||
                length is < 1 or > 4000);
        }
        if (endpoint == null) status.Text = "No feedback server is configured.";
        category.ItemSelected += _ => Update();
        comment.TextChanged += () =>
        {
            status.Text = endpoint == null ? "No feedback server is configured."
                : comment.Text.Trim().Length > 4000 ? "Use 4,000 characters or fewer." : "";
            Update();
        };
        dialog.Confirmed += async () =>
        {
            if (sent) { dialog.QueueFree(); return; }
            if (sending || endpoint == null || category.Selected < 1 || comment.Text.Trim().Length is < 1 or > 4000)
                return;
            sending = true;
            category.Disabled = true;
            comment.Editable = false;
            Update();
            status.Text = "Sending…";
            var body = new JsonObject { ["comment"] = comment.Text.Trim(), ["modVersion"] = version,
                ["category"] = Categories[category.Selected - 1].Key };
            if (encounter != null)
                body["fight"] = new JsonObject { ["encounter"] = encounter, ["turn"] = turn, ["lastCard"] = card };
            try
            {
                int seq = await Send(endpoint, body);
                sent = true;
                if (GodotObject.IsInstanceValid(dialog) && !dialog.IsQueuedForDeletion())
                {
                    status.Text = $"Sent as FB-{seq}.";
                    dialog.OkButtonText = "Done";
                }
            }
            catch (Exception e)
            {
                if (GodotObject.IsInstanceValid(dialog) && !dialog.IsQueuedForDeletion())
                    status.Text = e is FeedbackRefused ? e.Message : "Couldn't send feedback. Try again.";
            }
            finally
            {
                sending = false;
                if (GodotObject.IsInstanceValid(dialog) && !dialog.IsQueuedForDeletion())
                {
                    comment.Editable = !sent;
                    category.Disabled = sent;
                    Update();
                }
            }
        };
        Update();
        dialog.PopupCentered();
        comment.GrabFocus();
    }

    private static StyleBoxFlat Box(Color fill, Color outline, int horizontal = 14, int vertical = 8)
    {
        StyleBoxFlat box = new() { BgColor = fill, BorderColor = outline,
            ContentMarginLeft = horizontal, ContentMarginRight = horizontal,
            ContentMarginTop = vertical, ContentMarginBottom = vertical };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(6);
        return box;
    }

    private static void StyleButton(Button button, bool primary)
    {
        Color fill = primary ? Gold : Input;
        Color text = primary ? Ink : Paper;
        button.AddThemeStyleboxOverride("normal", Box(fill, primary ? Gold : Border));
        button.AddThemeStyleboxOverride("hover", Box(fill.Lightened(0.12f), Gold));
        button.AddThemeStyleboxOverride("pressed", Box(fill.Darkened(0.12f), Gold));
        button.AddThemeStyleboxOverride("disabled", Box(Panel, Border));
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(state, text);
        button.AddThemeColorOverride("font_disabled_color", Muted);
    }

    private static async Task<int> Send(Uri endpoint, JsonObject body)
    {
        Uploader.Ticket ticket = await SteamTicket.GetAsync();
        try
        {
            body["steamId"] = ticket.SteamId;
            body["ticket"] = ticket.Hex;
            using HttpResponseMessage response = await Http.PostAsJsonAsync(endpoint, body);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FeedbackRefused("In-game feedback isn't available on this server yet.");
            var result = await response.Content.ReadFromJsonAsync<JsonObject>();
            if (response.IsSuccessStatusCode && result?["seq"] is JsonValue seq && seq.TryGetValue(out int number))
                return number;
            string? message = result?["message"]?.GetValue<string>();
            throw new FeedbackRefused(message is { Length: > 0 and <= 250 } ? message : "Couldn't send feedback. Try again.");
        }
        finally { ticket.Release(); }
    }

    private sealed class FeedbackRefused(string message) : Exception(message);
}
