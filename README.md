# Slay My Stats mod

A Slay the Spire 2 mod that uploads each run to [slay-my-stats.com](https://slay-my-stats.com) when it ends,
so you don't have to sign in and pick your history folder.

## Install

Subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3838251909).

## What it sends

Your Steam ID, a short-lived Steam ticket that says this Steam account is playing, and each run as the game
saves it, with a log of each fight. Runs you played unmodded before installing it are sent too.

## Your saves

With any mod loaded, the game keeps its saves in a separate "modded" folder, and it starts empty. So that you don't have to replay ascensions or unlock everything again, Slay My Stats lifts each modded profile to your normal profile in the same slot. Each character's highest ascension, any wins and losses the modded profile doesn't have yet, and your unlocks all carry over. It only ever raises things, and it never changes your normal saves.

To turn this off, create `%APPDATA%\SlayTheSpire2\SlayMyStats\config.json` containing `{"carry_progress": false}`. Unsubscribe and the game goes back to your normal saves.

### Backups

Backups of your modded progress are in `%APPDATA%\SlayTheSpire2\SlayMyStats\backups\modded-profileN\`, where N is the profile number. Each is named `progress-<date>-<time>.save` and is a copy of the profile as it was just before the mod changed it. The mod only makes one when it is about to change something, and it never deletes any of your files.

To restore one:

1. Close the game.
2. Open `%APPDATA%\SlayTheSpire2\steam\<your Steam ID>\modded\profileN\saves\`. Rename `progress.save` to `progress.save.old`. That keeps what you have now.
3. Copy the backup you want from the backups folder into this folder. Leave the original backup where it is.
4. Rename the copy to `progress.save`.
5. If you don't want the mod to raise the profile again on the next launch, create `%APPDATA%\SlayTheSpire2\SlayMyStats\config.json` containing `{"carry_progress": false}` before starting the game.

Steam Cloud may keep its own copy of your saves. The backups folder is not synced.

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

The site's code is in [slay-my-stats](https://github.com/johnstonjesse329/slay-my-stats).

## License

MIT
