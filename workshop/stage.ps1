# Fills workshop\content with what a Workshop upload ships, and fetches Mega Crit's uploader. Uploads nothing.
#
#   .\workshop\stage.ps1 -Uploads On     subscribers upload to prod as soon as the mod loads
#   .\workshop\stage.ps1 -Uploads Off    subscribers turn uploads on themselves (see the Workshop description)
#
# content\ gets the release .dll, the manifest and a SlayMyStats.config.json pointing at prod only (gamma stays
# yours, through install.ps1). A player's own %APPDATA%\SlayTheSpire2\SlayMyStats\config.json overrides it, and
# survives updates; this one doesn't, since Steam replaces the mod's folder each time.
#
# Then, with Steam running and signed in to the account that owns the Workshop item:
#
#   .\workshop\.uploader\ModUploader.exe upload -w workshop
#
# The first upload creates the item and writes workshop\mod_id.txt (commit it); later ones update that item.
# Visibility, branches and the change note come from workshop.json on every upload.

param(
    [Parameter(Mandatory)][ValidateSet("On", "Off")][string]$Uploads
)

$ErrorActionPreference = "Stop"
$workshop = $PSScriptRoot
$repo = Split-Path $workshop

# Mega Crit's uploader, pinned: https://github.com/megacrit/sts2-mod-uploader
$uploaderVersion = "v0.2.0"
$uploaderSha256 = "2B55C19CC5932235CA9DBD663CA07D04E5D5A0018402303199F9B4EC8CA06578"
$uploaderDir = Join-Path $workshop ".uploader"
if (-not (Test-Path (Join-Path $uploaderDir "ModUploader.exe"))) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) "ModUploader-$uploaderVersion-win-x64.zip"
    Invoke-WebRequest "https://github.com/megacrit/sts2-mod-uploader/releases/download/$uploaderVersion/ModUploader-win-x64.zip" -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $uploaderSha256) {
        Remove-Item $zip
        throw "The uploader download doesn't match the pinned hash; not using it."
    }
    Expand-Archive $zip $uploaderDir -Force
    Remove-Item $zip
}

$url = aws cloudformation describe-stacks --stack-name SlayMyStatsStack --output text `
    --query "Stacks[0].Outputs[?OutputKey=='ModIngestFunctionUrl'].OutputValue"
if ($LASTEXITCODE -ne 0 -or "$url" -notlike "https://*") {
    throw "Couldn't read prod's ModIngestFunctionUrl from AWS. Sign in to AWS and run this again."
}

dotnet build "$repo\SlayMyStats.csproj" -c ExportRelease
if ($LASTEXITCODE -ne 0) { throw "Build failed; nothing was staged." }
$dll = (dotnet msbuild "$repo\SlayMyStats.csproj" -p:Configuration=ExportRelease -getProperty:TargetPath).Trim()

$content = Join-Path $workshop "content"
if (Test-Path $content) { Remove-Item $content -Recurse -Force }
New-Item -ItemType Directory $content | Out-Null
Copy-Item $dll, "$repo\SlayMyStats.json" $content
[ordered]@{ enabled = ($Uploads -eq "On"); endpoints = [ordered]@{ prod = "$($url.Trim().TrimEnd('/'))/mod" } } |
    ConvertTo-Json | Set-Content (Join-Path $content "SlayMyStats.config.json")

$version = (Get-Content "$repo\SlayMyStats.json" -Raw | ConvertFrom-Json).version
Write-Host "Staged $version with uploads $($Uploads.ToLower()) by default:"
Get-ChildItem $content | ForEach-Object { Write-Host "  $($_.Name)  $($_.Length) bytes" }
Write-Host "Upload with: .\workshop\.uploader\ModUploader.exe upload -w workshop"
