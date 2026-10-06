# Builds the mod and copies it into the game's mods folder, set up to upload to gamma.
#
#   .\install.ps1                      look up gamma's upload address from AWS
#   .\install.ps1 -Endpoint <url>      use this address instead (the ingest Function URL followed by "mod")
#
# With a mod loaded the game keeps its saves in a separate "modded" folder. To go back, delete
# <game>\mods\SlayMyStats.
param([string]$Endpoint)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Endpoint) {
    $url = aws cloudformation describe-stacks --stack-name SlayMyStatsGammaStack --output text `
        --query "Stacks[0].Outputs[?OutputKey=='IngestFunctionUrl'].OutputValue"
    if ($LASTEXITCODE -ne 0 -or "$url" -notlike "https://*") {
        throw "Couldn't read gamma's IngestFunctionUrl from AWS. Sign in to AWS, or pass -Endpoint."
    }
    $Endpoint = "$($url.Trim().TrimEnd('/'))/mod"
}

[ordered]@{ enabled = $true; endpoint = $Endpoint } | ConvertTo-Json | Set-Content SlayMyStats.config.json
Write-Host "Uploading to $Endpoint"

dotnet build -p:InstallMod=true
if ($LASTEXITCODE -ne 0) { throw "Build failed; nothing was installed." }
