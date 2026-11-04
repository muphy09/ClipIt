$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$CacheRoot = Join-Path $RepoRoot '.local\cache'
New-Item -ItemType Directory -Force $CacheRoot | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $CacheRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $CacheRoot 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
}
function Get-Version {
    [xml]$project = Get-Content -LiteralPath (Join-Path $RepoRoot 'source\ClipIt.csproj')
    return [string]$project.Project.PropertyGroup.Version
}
function Get-NextVersion([string]$Current, [string]$Kind) {
    if ($Current -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw 'Expected a stable major.minor.patch version.' }
    $major=[int]$Matches[1]; $minor=[int]$Matches[2]; $patch=[int]$Matches[3]
    switch ($Kind) {
        Hotfix { return "$major.$minor.$($patch + 1)" }
        Minor { return "$major.$($minor + 1).0" }
        Major { return "$($major + 1).0.0" }
        default { throw 'Unknown release kind.' }
    }
}
function Find-GitHubCli {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $path = Join-Path $RepoRoot '.local\tools\github-cli\bin\gh.exe'
    if (Test-Path -LiteralPath $path) { return $path }
    throw 'Install GitHub CLI (gh), then run gh auth login, or put its portable files in .local/tools/github-cli/.'
}
function Ensure-GitHubAuth([string]$Gh) {
    & $Gh auth status 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { return }
    # Reuse Git's existing credential in this process only; do not write a token into the repository.
    $oldPrompt = $env:GIT_TERMINAL_PROMPT; $oldInteractive = $env:GCM_INTERACTIVE
    try {
        $env:GIT_TERMINAL_PROMPT = '0'; $env:GCM_INTERACTIVE = 'never'
        $credential = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
        if ($LASTEXITCODE -eq 0) {
            $password = $credential | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
            if ($password) { $env:GH_TOKEN = $password.Substring(9) }
        }
    } finally { $env:GIT_TERMINAL_PROMPT=$oldPrompt; $env:GCM_INTERACTIVE=$oldInteractive }
    & $Gh auth status 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Sign in once with gh auth login, then rerun the release task.' }
}
function Stop-DevelopmentApp {
    $exe = Join-Path $RepoRoot '.local\publish\ClipIt.exe'
    Get-Process ClipIt -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process
}
