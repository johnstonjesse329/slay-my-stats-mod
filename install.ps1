# Builds the mod and copies it into the game's mods folder, set up to upload every run to both prod and gamma.
#
#   .\install.ps1                      look up each site's upload address from AWS
#
# With a mod loaded the game keeps its saves in a separate "modded" folder. To go back, delete
# <game>\mods\SlayMyStats.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$endpoints = [ordered]@{}
foreach ($site in @(@{ name = "prod"; stack = "SlayMyStatsStack" }, @{ name = "gamma"; stack = "SlayMyStatsGammaStack" })) {
    $url = aws cloudformation describe-stacks --stack-name $site.stack --output text `
        --query "Stacks[0].Outputs[?OutputKey=='ModIngestFunctionUrl'].OutputValue"
    if ($LASTEXITCODE -ne 0 -or "$url" -notlike "https://*") {
        throw "Couldn't read $($site.name)'s ModIngestFunctionUrl from AWS. Sign in to AWS, or write SlayMyStats.config.json by hand."
    }
    $endpoints[$site.name] = "$($url.Trim().TrimEnd('/'))/mod"
    Write-Host "Uploading to $($site.name): $($endpoints[$site.name])"
}

[ordered]@{ enabled = $true; endpoints = $endpoints } | ConvertTo-Json | Set-Content SlayMyStats.config.json

dotnet build -p:InstallMod=true
if ($LASTEXITCODE -ne 0) { throw "Build failed; nothing was installed." }
