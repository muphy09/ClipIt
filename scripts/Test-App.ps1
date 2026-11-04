. "$PSScriptRoot\Common.ps1"
$exe = Join-Path $RepoRoot '.local\publish\ClipIt.exe'
# Only this repository's disposable development instance is restarted.
Stop-DevelopmentApp
& (Join-Path $RepoRoot 'source\Build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Development build failed.' }
$env:CLIPIT_DATA = Join-Path $RepoRoot '.local\dev-data'
$env:CLIPIT_UPDATE_FEED = ''
New-Item -ItemType Directory -Force $env:CLIPIT_DATA | Out-Null
$settings = Join-Path $env:CLIPIT_DATA 'settings.json'
if (!(Test-Path -LiteralPath $settings)) {
    @{ Folder=(Join-Path $RepoRoot '.local\dev-clips'); Modifiers=3; Key=119 } | ConvertTo-Json | Set-Content -LiteralPath $settings
}
Start-Process -FilePath $exe -WindowStyle Normal
Write-Host 'ClipIt development instance launched with isolated settings and clips.'
