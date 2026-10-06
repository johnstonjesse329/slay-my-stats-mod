# Slay My Stats mod

A Slay the Spire 2 mod that uploads each run to [slay-my-stats](https://github.com/johnstonjesse329/slay-my-stats)
when it ends, so you don't have to sign in and pick your history folder.

**Prototype.** It only works for players on the site's allowlist, and it has uploaded one run from inside
the game. See [What has been tried](#what-has-been-tried).

## Read this before installing

With any mod loaded, the game keeps its saves in a separate folder:

```
%APPDATA%\SlayTheSpire2\steam\<your steam id>\modded\profile1\...
```

instead of `...\<your steam id>\profile1\...`. Your existing runs and progress stay where they are, and
runs you play with the mod are saved in the `modded` folder. That folder starts as a new profile: none of
your unlocks or progress are in it. With no mods left in `mods\`, the game goes back to its normal saves.

That is why a plain build doesn't touch the game: installing is its own step.

## How it works

1. A run ends and the game writes it to your run history. The mod is hooked there
   (`RunHistorySaveManager.SaveHistory`) and takes the same JSON the `.run` file holds.
2. The run goes into a queue on disk, so nothing is lost if you're offline or quit.
3. Off the game thread, the mod asks Steam for a ticket that says who is playing, and sends your Steam ID
   and the ticket to the site.
4. The site checks your Steam ID is on its allowlist, checks the ticket with Steam, and answers with a
   one-time upload URL.
5. The mod uploads the queued runs there. From then on it is the same pipeline as an upload from the
   website.

There is no password or key in the mod. A Steam ticket is short-lived and only says "this Steam account is
running Slay the Spire 2 right now"; the mod never writes it to the log.

Each run is sent with one added key, `slay_my_stats_mod`, holding the mod's version and the mods the game
had loaded (this one included): each one's id, name, version and whether it says it affects gameplay. The
site marks those runs "Modded" and lists the mods.

### Guardrails

In case the mod goes haywire, both ends hold it back.

| | Mod | Site |
|---|---|---|
| How often | 20 requests an hour | 5 s apart, 200 a day per Steam account |
| How much | 50 runs per upload, 200 runs queued | 2 MB per upload |
| On failure | Retries after 10 s, 1 min, 5 min, then waits for the next run or restart | |
| On "no" | A refusal (not on the allowlist, bad ticket) stops uploads until the game restarts; runs stay queued | Alarm if requests or refusals pile up |

Nothing here runs on the game thread except reading the run that just ended.

## Build

Needs the game installed and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). The game
is found through your Steam libraries (`Sts2PathDiscovery.props`).

```
dotnet build
```

This builds `SlayMyStats.dll` and changes nothing in the game.

## Configure

Copy `SlayMyStats.config.example.json` to `SlayMyStats.config.json` (not in git) and set the endpoint: the
site's `IngestFunctionUrl` followed by `mod`.

```json
{
  "enabled": true,
  "endpoint": "https://xxxxxxxx.lambda-url.us-west-2.on.aws/mod"
}
```

With no config file, or `"enabled": false`, the mod loads and sends nothing.

## Install

```
.\install.ps1
```

does the configure step for gamma (it looks the endpoint up from AWS, or takes `-Endpoint <url>`) and then
installs. By hand,

```
dotnet build -p:InstallMod=true
```

copies the `.dll`, `SlayMyStats.json` and your config to `<game>\mods\SlayMyStats\`. Start the game and look
for `Slay My Stats loaded. Uploads are on.` in `%APPDATA%\SlayTheSpire2\logs\godot.log`.

To uninstall, delete `<game>\mods\SlayMyStats\`.

## Try it without the game

`Probe` is a console program that runs the mod's own upload code (`Uploader.cs`, `SteamTicket.cs`), so you
can test against a server without installing anything or moving your saves.

```
dotnet build Probe

# Ask Steam for a ticket and say whether one came back. Sends nothing anywhere.
Probe\bin\Debug\net9.0\SlayMyStatsProbe.exe ticket

# Upload runs exactly as the mod would, with a real Steam ticket.
Probe\bin\Debug\net9.0\SlayMyStatsProbe.exe upload <endpoint> <file.run>...

# Against a local dev server only: skip Steam and claim an id with a made-up ticket.
Probe\bin\Debug\net9.0\SlayMyStatsProbe.exe upload http://127.0.0.1:8000/api/ingest/mod <file.run> --as <steamid>
```

Steam has to be running. While the probe asks for a ticket, Steam shows you as playing the game for a
second or two.

## What has been tried

Tried for real:

- Both projects build.
- `SlayMyStatsProbe ticket` got a ticket from Steam for the signed-in account.
- The probe against slay-my-stats's dev server (the site's real allowlist, limit and run-processing code,
  with only the Steam check stubbed): a run uploaded and showed up on the profile; a Steam ID not on the
  allowlist was refused; an allowlisted ID with someone else's ticket was refused; a batch with a duplicate
  was merged once; uploading again straight away hit the cooldown and went through on the retry; an
  unreachable server kept the run queued.
- The mod loaded in the game (v0.111.0) against the gamma site: an abandoned run was picked up by the hook,
  Steam gave a ticket from inside the game, the site checked it with Steam and S3 took the upload, and the
  run was added to the profile.

Not tried yet:

- A run that is saved, exited and picked up again later. The game's own history files say it is written
  once, at the end, but the mod hasn't been watched through one.
- A win or a death (only an abandon so far), and more than one run in a session.
- The mod list as the game reports it. The site has shown "Modded" for a made-up list sent to its dev
  server; the run uploaded from inside the game was sent before the list was added.
- The site's alarms going off.

## Layout

| | |
|---|---|
| `SlayMyStatsCode/MainFile.cs` | Entry point: patches the game, reads the config, starts the uploader |
| `SlayMyStatsCode/RunEndPatch.cs` | The hook on the game saving a run to history |
| `SlayMyStatsCode/Uploader.cs` | Queue, batching, retries, the two HTTP requests |
| `SlayMyStatsCode/SteamTicket.cs` | Asks Steam who is playing |
| `SlayMyStatsCode/ModConfig.cs` | `SlayMyStats.config.json` |
| `Probe/` | The console program above |
| `Sts2PathDiscovery.props` | Finds the game, for both projects to build against |
| `SlayMyStats.json` | The manifest the game reads |

## License

MIT
