# Slay My Stats mod

A Slay the Spire 2 mod that uploads each run to [slay-my-stats.com](https://slay-my-stats.com) when it ends,
so you don't have to sign in and pick your history folder.

## Install

Subscribe on the Steam Workshop.

## What it sends

Your Steam ID, a short-lived Steam ticket that says this Steam account is playing, and each run as the game
saves it, with a log of each fight. Runs you played unmodded before installing it are sent too.

## Your saves

With any mod loaded, the game keeps its saves in a separate "modded" folder. The first time, the game copies
your progress there. Unsubscribe and it goes back to your normal saves.

## Turning it off

Switch it off in the game's mod list, or unsubscribe.

## Development

Needs the game and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). Once per clone,
`git config core.hooksPath githooks` so commits refuse private values (`tools/check_secrets.py`).

```
.\install.ps1                          # build, point it at the site, copy into <game>\mods\SlayMyStats\
python tools/check_patches.py          # after a game update: fails if a patch no longer fits the game
.\workshop\stage.ps1 -Uploads On       # stage a Workshop release in workshop\content\
.\workshop\.uploader\ModUploader.exe upload -w workshop
```

Each release: `check_patches.py`, bump `version` in `SlayMyStats.json`, set `changeNote` in
`workshop\workshop.json`, stage, upload. Set visibility and branches in `workshop.json`, not on the Workshop
page: every upload sends them again.

## License

MIT
