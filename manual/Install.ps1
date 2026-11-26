# Copies the SlayMyStats folder next to this script into the game's mods folder. Downloads nothing.
param([string]$GameDir, [switch]$WhatIf)
$ErrorActionPreference = "Stop"

$source = Join-Path $PSScriptRoot "SlayMyStats"
if (-not (Test-Path -LiteralPath (Join-Path $source "SlayMyStats.dll"))) {
    throw "SlayMyStats folder not found next to this script. Extract the whole zip first."
}
if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) { throw "Close the game, then run this again." }

if (-not $GameDir) {
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if ($steam) {
        $libraries = @($steam)
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            $libraries += Select-String -LiteralPath $vdf -Pattern '"path"\s+"(.+)"' -Encoding UTF8 |
                ForEach-Object { $_.Matches[0].Groups[1].Value.Replace('\\', '\') }
        }
        foreach ($library in $libraries) {
            $candidate = Join-Path $library "steamapps\common\Slay the Spire 2"
            try { if (Test-Path -LiteralPath $candidate) { $GameDir = $candidate; break } } catch {}
        }
    }
}
if (-not $GameDir -or -not (Test-Path -LiteralPath $GameDir)) {
    throw "Couldn't find Slay the Spire 2. Follow the manual steps in the README instead."
}

$target = Join-Path $GameDir "mods\SlayMyStats"
if ($WhatIf) { Write-Host "Would install to $target"; return }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $target -Force
Write-Host "Installed to $target. Start the game."
