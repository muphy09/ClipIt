param([string]$Version='', [switch]$SkipPreviousRelease, [string]$PackId='ClipIt', [switch]$TestPackage)
. "$PSScriptRoot\Common.ps1"
Push-Location $RepoRoot
$originalData = $env:CLIPIT_DATA
try {
    if (!$Version) { $Version = Get-Version }
    $out = Join-Path $RepoRoot 'artifacts\releases'
    $publish = Join-Path $RepoRoot '.local\publish'
    if (Test-Path -LiteralPath $out) {
        $expected = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts\releases'))
        if ((Resolve-Path -LiteralPath $out).Path -ne $expected) { throw 'Unexpected release output path.' }
        $history = Join-Path $RepoRoot ('.local\package-history\' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Split-Path $history -Parent) | Out-Null
        Move-Item -LiteralPath $out -Destination $history
    }
    New-Item -ItemType Directory -Force $out | Out-Null
    Invoke-Checked dotnet @('tool','restore')
    Stop-DevelopmentApp
    & (Join-Path $RepoRoot 'source\Build.ps1') -Output $publish -Version $Version
    $checkFolder = Join-Path $RepoRoot '.local\media-checks'
    New-Item -ItemType Directory -Force $checkFolder | Out-Null
    $env:CLIPIT_DATA = Join-Path $RepoRoot '.local\check-data'
    $process = Start-Process -FilePath (Join-Path $publish 'ClipIt.exe') -ArgumentList '--media-checks',('"'+$checkFolder+'"') -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Media checks failed. See $checkFolder\media-check-results.json" }
    $env:CLIPIT_DATA = $originalData
    if (!$SkipPreviousRelease) {
        $gh = Find-GitHubCli
        Ensure-GitHubAuth $gh
        $previous = & $gh release list --repo muphy09/ClipIt --exclude-drafts --exclude-pre-releases --limit 1 --json tagName | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Could not query the previous GitHub release.' }
        if (@($previous).Count -gt 0) {
            Invoke-Checked $gh @('release','download',$previous[0].tagName,'--repo','muphy09/ClipIt','--pattern','*-full.nupkg','--pattern','releases.win.json','--dir',$out,'--clobber')
        }
    }
    $arguments = @('vpk','pack','--packId',$PackId,'--packTitle','ClipIt','--packVersion',$Version,'--packDir',$publish,'--mainExe','ClipIt.exe','--runtime','win-x64','--channel','win','--outputDir',$out,'--icon',(Join-Path $RepoRoot 'source\Assets\ClipIt.ico'),'--noPortable','--framework','vcredist143-x64')
    if ($TestPackage) { $arguments += @('--shortcuts','None') }
    Invoke-Checked dotnet $arguments
    Write-Host "Windows installer and update packages ready in $out"
} finally { $env:CLIPIT_DATA = $originalData; Pop-Location }
