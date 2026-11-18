# Slay My Stats mod

A Slay the Spire 2 mod that uploads each run to [slay-my-stats.com](https://slay-my-stats.com) when it ends,
so you don't have to sign in and pick your history folder.

## Install

Subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3838251909).

## What it sends

Your Steam ID, a short-lived Steam ticket that says this Steam account is playing, and each run as the game
saves it, with a log of each fight. Runs you played unmodded before installing it are sent too.

## Choosing what uploads

By default the mod uploads every run from every profile, exactly as the game saved it. A co-op run file already contains your partners' Steam IDs, so they upload too, and the site uses them to link a co-op run to each player's copy of it. A settings file lets you pause uploads, choose which profiles upload, and blank your partners' IDs. The steps are below.

### Changing a setting

1. Press Win+R, paste `%APPDATA%\SlayTheSpire2\SlayMyStats` and press Enter.
2. Open `config.json` in Notepad. The mod creates it with the defaults the first time it runs; if it isn't there yet, start the game once with the mod on.
3. Change a value and save.

The file holds one JSON object. Keep the commas between lines, and the quotes around names and text:

```json
{
  "uploadsEnabled": true,
  "profiles": "all",
  "shareMultiplayerIds": true
}
```

### The settings

- `uploadsEnabled` (default `true`): `false` stops all uploads. Set it back to `true` and the mod sends what it skipped, at the end of your next run or the next time you launch the game.
- `profiles` (default `"all"`): which of your game profiles may upload. `"all"`, or a list such as `["profile1"]` or `["profile1", "profile3"]` (`[1, 3]` works too). A profile is one of the game's three save slots; profile 1 is the first. Runs from a profile that isn't listed never upload.
- `shareMultiplayerIds` (default `true`): `false` blanks your co-op partners' Steam IDs in a run before it is sent. Your own ID stays, so the site can tell which player you are. With it off, the site can't link your co-op run to the other players' copies.

For example, to upload only profile 1 and keep your co-op partners' IDs private:

```json
{
  "uploadsEnabled": true,
  "profiles": ["profile1"],
  "shareMultiplayerIds": false
}
```

### What happens to runs the settings skip

A skipped run isn't marked as uploaded, so it goes out later if you change the settings to allow it (runs from before you installed the mod are picked up when you launch the game). Runs already on the site are not removed when you change a setting.

### If the file has a mistake

If the file can't be read, **nothing uploads** until it is fixed. The main menu shows "Slay My Stats: config error, uploads paused". `slay-my-stats-config-error.txt`, next to the config, holds the exact error and the line, and the game log has it too. Fix the file, or delete it to get the defaults back. Once the file reads correctly, uploads resume at the end of your next run or the next time you launch the game.

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
