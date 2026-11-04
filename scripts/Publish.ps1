param([Parameter(Mandatory)][ValidateSet('Hotfix','Minor','Major')][string]$Kind)
. "$PSScriptRoot\Common.ps1"
Push-Location $RepoRoot
$oldVersion = Get-Version
$nextVersion = Get-NextVersion $oldVersion $Kind
$tag = "v$nextVersion"
$projectFile = Join-Path $RepoRoot 'source\ClipIt.csproj'
$oldProject = Get-Content -LiteralPath $projectFile -Raw
$versionChanged = $false
$committed = $false
try {
    $gh = Find-GitHubCli
    Ensure-GitHubAuth $gh
    $branch = git branch --show-current
    if ($branch -ne 'main') { throw 'Publish from main. Merge your changes into main first.' }
    $remote = git remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $remote -notmatch 'github\.com[:/]muphy09/ClipIt(?:\.git)?$') { throw 'Unexpected origin; expected muphy09/ClipIt.' }
    if (git diff --name-only --diff-filter=U) { throw 'Resolve merge conflicts first.' }
    Invoke-Checked git @('fetch','origin','main','--tags')
    git merge-base --is-ancestor origin/main HEAD
    if ($LASTEXITCODE -ne 0) { throw 'main is behind or diverged from origin/main. Integrate remote changes first.' }
    if (git tag --list $tag) { throw "$tag already exists. Inspect the previous release before retrying." }
    Set-Content -LiteralPath $projectFile -Value $oldProject.Replace("<Version>$oldVersion</Version>","<Version>$nextVersion</Version>") -NoNewline
    $versionChanged = $true
    Write-Host "Validating ClipIt $oldVersion -> $nextVersion ($Kind)…"
    & "$PSScriptRoot\Package.ps1"
    Invoke-Checked git @('add','--all')
    Invoke-Checked git @('commit','-m',"Release $tag")
    $committed = $true
    Invoke-Checked git @('tag','-a',$tag,'-m',"ClipIt $nextVersion")
    Invoke-Checked git @('push','--atomic','origin','main',$tag)
    $sha = git rev-parse HEAD
    Write-Host "Pushed $tag. Waiting for the Windows release workflow…"
    $run = $null
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        $runs = & $gh run list --repo muphy09/ClipIt --workflow release.yml --commit $sha --json databaseId --limit 1 | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Could not query the release workflow. Check GitHub Actions before retrying.' }
        if (@($runs).Count -gt 0) { $run=$runs[0].databaseId; break }
        Start-Sleep -Seconds 2
    }
    if (!$run) { throw "Release was pushed, but the workflow has not appeared yet. Check GitHub Actions for $tag; do not bump again to retry." }
    Invoke-Checked $gh @('run','watch',"$run",'--repo','muphy09/ClipIt','--exit-status')
    Write-Host "ClipIt $nextVersion is published. Installed clients will find it on startup."
} catch {
    if ($versionChanged -and !$committed) {
        $current = Get-Content -LiteralPath $projectFile -Raw
        if ($current -eq $oldProject.Replace("<Version>$oldVersion</Version>","<Version>$nextVersion</Version>")) { Set-Content -LiteralPath $projectFile -Value $oldProject -NoNewline }
    }
    if ($committed) { Write-Warning "The release commit/tag may already exist. Inspect GitHub Actions and retry the existing release; do not force-push or bump again blindly." }
    throw
} finally { Pop-Location }
