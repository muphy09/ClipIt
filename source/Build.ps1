param(
    [string]$ObsArchive = '',
    [string]$Output = (Join-Path $PSScriptRoot '..\.local\publish'),
    [string]$BuildRoot = (Join-Path $PSScriptRoot '..\.local\cache'),
    [string]$MediaToolsPath = '',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$buildRoot = [IO.Path]::GetFullPath($BuildRoot)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $buildRoot | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $buildRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $buildRoot 'packages'
if (!$MediaToolsPath) {
    $MediaToolsPath = Join-Path $buildRoot 'media-tools'
    if (!(Test-Path -LiteralPath (Join-Path $MediaToolsPath 'ffmpeg.exe'))) {
        $mediaArchive = Join-Path $buildRoot 'ffmpeg-9.0.1.zip'
        if (!(Test-Path -LiteralPath $mediaArchive)) { Invoke-WebRequest 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip' -OutFile $mediaArchive }
        if ((Get-FileHash -LiteralPath $mediaArchive -Algorithm SHA256).Hash -ne 'FEC81AE03971D9DD4BE3EBE02E263BD2EC1D789483F931BDBA5F5715E65DA2E9') { throw 'Unexpected media archive checksum.' }
        $unpacked = Join-Path $buildRoot 'ffmpeg-unpacked'
        Expand-Archive -LiteralPath $mediaArchive -DestinationPath $unpacked -Force
        New-Item -ItemType Directory -Force $MediaToolsPath | Out-Null
        Copy-Item -LiteralPath (Join-Path $unpacked 'ffmpeg-9.0.1-essentials_build\bin\ffmpeg.exe'),(Join-Path $unpacked 'ffmpeg-9.0.1-essentials_build\bin\ffprobe.exe') -Destination $MediaToolsPath -Force
    }
}
$mediaHashes = @{
    'ffmpeg.exe' = '72A489ECCD008C2EC2C0A5856C5C75BC3D8BBFA90166C4566865C246445E6AA3'
    'ffprobe.exe' = '19202B23C0043F15AD1B7BCE2344F406FD52BD6EFD8F995CE02E7392A1CEC52F'
}
foreach ($name in $mediaHashes.Keys) {
    $tool = Join-Path $MediaToolsPath $name
    if (!(Test-Path -LiteralPath $tool)) { throw "Missing $name. Use the tools included with this package, or pass -MediaToolsPath pointing to the pinned Gyan FFmpeg 9.0.1 essentials bin folder." }
    if ((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash -ne $mediaHashes[$name]) { throw "Unexpected media tool checksum: $name" }
}
if (!$ObsArchive) {
    $ObsArchive = Join-Path $buildRoot 'OBS-Studio-33.0.1-Windows-x64.zip'
    if (!(Test-Path $ObsArchive)) {
        Invoke-WebRequest 'https://github.com/obsproject/obs-studio/releases/download/33.0.1/OBS-Studio-33.0.1-Windows-x64.zip' -OutFile $ObsArchive
    }
}
$expectedHash = '0D9436451C2D772B33428E3FDAE9776F071895C335DE17283176306FB72651D3'
if ((Get-FileHash -LiteralPath $ObsArchive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The OBS archive does not match the pinned official package.' }
$runtime = Join-Path $buildRoot 'obs'
if (!(Test-Path (Join-Path $runtime 'bin\64bit\obs.dll'))) { Expand-Archive -LiteralPath $ObsArchive -DestinationPath $runtime -Force }
if (!$Version) { [xml]$project = Get-Content (Join-Path $PSScriptRoot 'ClipIt.csproj'); $Version = $project.Project.PropertyGroup.Version }
$projectPath = Join-Path $PSScriptRoot 'ClipIt.csproj'
# Editor/design-time restores use source/obj and can replace the Windows runtime
# assets during a publish. Keep packaging intermediates separate and restore the
# exact publish configuration explicitly before compiling it.
# Force restore on each run: WPF's temporary compilation project can also leave
# a non-runtime assets file behind in this cache after a successful build.
$artifactsPath = Join-Path $buildRoot 'dotnet-publish'
dotnet restore $projectPath -r win-x64 --force --artifacts-path $artifactsPath -p:Configuration=Release -p:SelfContained=true -p:PublishSingleFile=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Could not restore the Windows x64 publishing dependencies.' }
dotnet publish $projectPath -c Release -r win-x64 --self-contained true --no-restore --artifacts-path $artifactsPath -p:PublishSingleFile=false "-p:Version=$Version" -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$mediaTarget = Join-Path $Output 'tools'
New-Item -ItemType Directory -Force $mediaTarget | Out-Null
if ([IO.Path]::GetFullPath($MediaToolsPath).TrimEnd('\') -ne [IO.Path]::GetFullPath($mediaTarget).TrimEnd('\')) {
    foreach ($name in $mediaHashes.Keys) { Copy-Item -LiteralPath (Join-Path $MediaToolsPath $name) -Destination $mediaTarget -Force }
}
$target = Join-Path $Output 'runtime'
$bin = Join-Path $target 'bin\64bit'
New-Item -ItemType Directory -Force $bin | Out-Null
Get-ChildItem (Join-Path $runtime 'bin\64bit') -Filter '*.dll' |
    Where-Object { $_.Name -notmatch '^(Qt6|obs-frontend-api|obs-scripting|lua51)' } |
    Copy-Item -Destination $bin -Force
foreach ($name in @('obs-ffmpeg-mux.exe','obs-nvenc-test.exe','obs-qsv-test.exe','obs-amf-test.exe')) {
    Copy-Item (Join-Path $runtime "bin\64bit\$name") $bin -Force
    Copy-Item (Join-Path $runtime "bin\64bit\$name") $Output -Force
}
New-Item -ItemType Directory -Force (Join-Path $target 'data') | Out-Null
Copy-Item (Join-Path $runtime 'data\libobs') (Join-Path $target 'data') -Recurse -Force
New-Item -ItemType Directory -Force (Join-Path $target 'core') | Out-Null
foreach ($name in @('win-capture','win-wasapi','obs-ffmpeg','obs-nvenc','obs-qsv11')) {
    Copy-Item (Join-Path $runtime "core\$name") (Join-Path $target 'core') -Recurse -Force
}
$notices = Join-Path $Output 'licenses'
New-Item -ItemType Directory -Force $notices | Out-Null
foreach ($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $packageRoot = Join-Path $env:NUGET_PACKAGES $package
    $packageVersion = Get-ChildItem $packageRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($packageVersion) {
        $noticeTarget = Join-Path $notices $package
        New-Item -ItemType Directory -Force $noticeTarget | Out-Null
        Get-ChildItem $packageVersion.FullName -File | Where-Object { $_.Name -match 'license|notice' } | Copy-Item -Destination $noticeTarget -Force
    }
}
$root = Split-Path $PSScriptRoot -Parent
Get-ChildItem -LiteralPath (Join-Path $root 'licenses') | Copy-Item -Destination $notices -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE.txt'),(Join-Path $root 'THIRD-PARTY.md') -Destination $Output -Force
Write-Output "Built standalone app: $Output\ClipIt.exe"
