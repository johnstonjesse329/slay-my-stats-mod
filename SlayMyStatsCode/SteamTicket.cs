using Steamworks;

namespace SlayMyStats.SlayMyStatsCode;

/// <summary>
/// Asks the Steam client the game is running under for a Web API ticket. Only someone signed in to that Steam
/// account, with the game running, can get one, and the server checks it with Steam itself, so it stands in for
/// the Steam sign-in the site's upload page does in a browser.
///
/// Steam answers through a callback, which arrives whenever the game next runs its Steam callbacks.
/// </summary>
public static class SteamTicket
{
    /// <summary>The name the ticket is asked for under. The server asks Steam about the same name.</summary>
    public const string Identity = "slay-my-stats";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly object Lock = new();
    private static readonly Dictionary<HAuthTicket, TaskCompletionSource<GetTicketForWebApiResponse_t>> Waiting = new();

    // Kept in a field so the callback isn't garbage collected before Steam answers.
    private static Callback<GetTicketForWebApiResponse_t>? _callback;

    public static async Task<Uploader.Ticket> GetAsync()
    {
        TaskCompletionSource<GetTicketForWebApiResponse_t> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HAuthTicket handle;
        lock (Lock)
        {
            try
            {
                _callback ??= Callback<GetTicketForWebApiResponse_t>.Create(OnAnswer);
                handle = SteamUser.GetAuthTicketForWebApi(Identity);
            }
            catch (InvalidOperationException e)
            {
                // Steamworks throws this until the game has started Steam, and when Steam isn't running.
                throw new Uploader.TicketException($"Steam isn't ready ({e.Message}).");
            }

            if (handle == HAuthTicket.Invalid) throw new Uploader.TicketException("Steam didn't start a ticket request.");
            Waiting[handle] = answer;
        }

        if (await Task.WhenAny(answer.Task, Task.Delay(Timeout)) != answer.Task)
        {
            lock (Lock) Waiting.Remove(handle);
            SteamUser.CancelAuthTicket(handle);
            throw new Uploader.TicketException("Steam didn't answer the ticket request.");
        }

        GetTicketForWebApiResponse_t response = await answer.Task;
        if (response.m_eResult != EResult.k_EResultOK || response.m_cubTicket <= 0)
        {
            SteamUser.CancelAuthTicket(handle);
            throw new Uploader.TicketException($"Steam refused the ticket request ({response.m_eResult}).");
        }

        // The ticket is a credential: it goes to the server and nowhere else, and is never logged.
        string hex = Convert.ToHexString(response.m_rgubTicket, 0, response.m_cubTicket);
        string steamId = SteamUser.GetSteamID().m_SteamID.ToString();
        return new Uploader.Ticket(steamId, hex, () => SteamUser.CancelAuthTicket(handle));
    }

    private static void OnAnswer(GetTicketForWebApiResponse_t response)
    {
        // Every ticket request in the process answers here, other mods' included; only ours are waited on.
        TaskCompletionSource<GetTicketForWebApiResponse_t>? answer;
        lock (Lock)
        {
            if (!Waiting.Remove(response.m_hAuthTicket, out answer)) return;
        }

        answer.TrySetResult(response);
    }
}
