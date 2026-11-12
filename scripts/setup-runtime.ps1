#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$DownloadDirectory,
    [string]$TarExecutable = 'tar.exe',
    [switch]$ValidateOnly
)
. (Join-Path $PSScriptRoot 'runtime-common.ps1')
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
$GameDirectory = Get-AbsoluteDirectory $GameDirectory
$RuntimeDirectory = Get-AbsoluteDirectory $RuntimeDirectory
$DownloadDirectory = Get-AbsoluteDirectory $DownloadDirectory
Assert-SeparateDirectories $GameDirectory $RuntimeDirectory
Assert-SeparateDirectories $GameDirectory $DownloadDirectory
Assert-SeparateDirectories $RuntimeDirectory $DownloadDirectory
Assert-NoReparseAncestors $GameDirectory
Assert-NoReparseAncestors $RuntimeDirectory
Assert-NoReparseAncestors $DownloadDirectory
if ((Test-Path -LiteralPath $RuntimeDirectory) -and @(Get-ChildItem -LiteralPath $RuntimeDirectory -Force).Count -ne 0) {
    throw 'RuntimeDirectory must be new or empty. Existing installations are never overwritten.'
}
foreach ($loader in @('BepInEx', 'MelonLoader', 'winhttp.dll', 'doorstop_config.ini')) {
    if (Test-Path -LiteralPath (Join-Path $GameDirectory $loader)) {
        throw "Source contains $loader. Use a clean game installation; third-party mods are not copied implicitly."
    }
}
$files = @(Get-SafeFiles $GameDirectory -ExcludeDebugSymbols)
Assert-GameBuild $GameDirectory
if (-not (Get-Command $TarExecutable -ErrorAction SilentlyContinue)) { throw 'Windows tar.exe is required; pass -TarExecutable if it is not on PATH.' }
if ($ValidateOnly) { Write-Host 'Source, destination, build, and prerequisites validated. No files changed.'; return }
[void][IO.Directory]::CreateDirectory($DownloadDirectory)
$bepinex = Get-PinnedDownload 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip' (Join-Path $DownloadDirectory 'BepInEx_win_x64_5.4.23.6.zip') '6C57A714A892EFB4E9E1FEF63D608A3391F30EC984031C465404E4EE381A0755'
# Unity 2021.2+ corlibs are platform-specific. The generic BepInEx corlibs archive
# contains Unix libraries and cannot be used with this Windows player.
# The editor archive includes profiles for every player platform. Extract only
# unityjit-win32, never the Unix profile or editor executable.
$editor = Get-PinnedDownload 'https://download.unity3d.com/download_unity/a166abc3bf0e/LinuxEditorInstaller/Unity-6000.0.73f1.tar.xz' (Join-Path $DownloadDirectory 'Unity-6000.0.73f1.tar.xz') 'C55AD511D41AEE03086A1BF5E431F3600468539B0BFA2BDDDDC460F8CF96B1AC'
$extract = Join-Path $DownloadDirectory 'unity-win32-6000.0.73f1'
$monoProfileDirectory = Join-Path $extract 'Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32'
if (-not (Test-Path -LiteralPath $extract)) {
    [void][IO.Directory]::CreateDirectory($extract)
    & $TarExecutable '-xf' $editor '-C' $extract 'Editor/Data/MonoBleedingEdge/lib/mono/unityjit-win32'
    if ($LASTEXITCODE -ne 0) { throw "Could not extract the Unity Windows Mono profile. Partial cache retained: $extract" }
}
foreach ($required in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'Facades/netstandard.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $monoProfileDirectory $required))) { throw "Extracted Windows Mono profile lacks $required." }
}
Assert-WindowsMonoProfile $monoProfileDirectory (Join-Path $PSScriptRoot 'unity-win32-6000.0.73f1.json')

# All downloads and source validation finish before any game files are copied.
[void][IO.Directory]::CreateDirectory($RuntimeDirectory)
foreach ($file in $files) {
    $relative = $file.FullName.Substring($GameDirectory.Length).TrimStart('\', '/')
    $target = Join-Path $RuntimeDirectory $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($file.FullName, $target, $false)
}
Expand-SafeZip $bepinex $RuntimeDirectory
$corlibs = Join-Path $RuntimeDirectory 'BepInEx/corlibs'
[void][IO.Directory]::CreateDirectory($corlibs)
foreach ($file in Get-SafeFiles $monoProfileDirectory) {
    $relative = $file.FullName.Substring($monoProfileDirectory.Length).TrimStart('\', '/')
    $target = Join-Path $corlibs $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($file.FullName, $target, $false)
}
$doorstop = Join-Path $RuntimeDirectory 'doorstop_config.ini'
$content = [IO.File]::ReadAllText($doorstop)
if ($content -notmatch '(?m)^dll_search_path_override\s*=') { throw 'Unexpected Doorstop configuration format.' }
$content = [regex]::Replace($content, '(?m)^dll_search_path_override\s*=.*$', 'dll_search_path_override = BepInEx\corlibs;BepInEx\corlibs\Facades')
[IO.File]::WriteAllText($doorstop, $content, [Text.UTF8Encoding]::new($false))
Assert-GameBuild $RuntimeDirectory
$marker = @{ tool = 'NomadDrive-DedicatedServer'; gameBuild = '26391437'; unity = '6000.0.73f1'; bepinex = '5.4.23.6'; createdUtc = [DateTime]::UtcNow.ToString('O') }
[IO.File]::WriteAllText((Join-Path $RuntimeDirectory '.nomaddrive-server-runtime'), ($marker | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Prepared isolated runtime: $RuntimeDirectory"
Write-Host "Downloaded editor and extracted profile retained at: $DownloadDirectory"
Write-Host 'Run scripts/build.ps1 next to install the hosting plugin.'
