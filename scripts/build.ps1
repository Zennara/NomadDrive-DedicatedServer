#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
. (Join-Path $PSScriptRoot 'runtime-common.ps1')
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeDirectory = Get-AbsoluteDirectory $RuntimeDirectory
Assert-NoReparseAncestors $RuntimeDirectory
if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory '.nomaddrive-server-runtime') -PathType Leaf)) {
    throw 'Runtime marker missing. Prepare an isolated runtime with setup-runtime.ps1 first.'
}
Assert-GameBuild $RuntimeDirectory
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'Nomad Drive.exe'" | Where-Object {
    $_.ExecutablePath -and [IO.Path]::GetDirectoryName($_.ExecutablePath).Equals($RuntimeDirectory, [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) { throw 'Stop this server runtime before updating its plugin.' }
$core = Join-Path $RuntimeDirectory 'BepInEx/core'
if (-not (Test-Path -LiteralPath (Join-Path $core 'BepInEx.dll'))) { throw 'BepInEx 5 core libraries are missing.' }
& dotnet build (Join-Path $repo 'src/HostPlugin/HostPlugin.csproj') '-c' $Configuration ('-p:GameDirectory=' + $RuntimeDirectory) ('-p:BepInExDirectory=' + $core) '--nologo'
if ($LASTEXITCODE -ne 0) { throw 'Hosting plugin build failed.' }
$cli = Join-Path $repo 'artifacts/cli'
Assert-NoReparseAncestors $cli
if (Test-Path -LiteralPath $cli) { $null = @(Get-SafeFiles $cli) }
& dotnet publish (Join-Path $repo 'src/Supervisor/Supervisor.csproj') '-c' $Configuration '-r' 'win-x64' '--self-contained' 'false' '-o' $cli '--nologo'
if ($LASTEXITCODE -ne 0) { throw 'Supervisor publish failed.' }
& dotnet publish (Join-Path $repo 'src/Panel/Panel.csproj') '-c' $Configuration '-r' 'win-x64' '--self-contained' 'false' '-o' $cli '--nologo'
if ($LASTEXITCODE -ne 0) { throw 'Windows panel publish failed.' }
$pluginDestination = Join-Path $RuntimeDirectory 'BepInEx/plugins/DedicatedServer'
Assert-NoReparseAncestors $pluginDestination
[void][IO.Directory]::CreateDirectory($pluginDestination)
$plugin = Join-Path $repo ('src/HostPlugin/bin/' + $Configuration + '/netstandard2.1/NomadDrive.DedicatedServer.dll')
$pluginTarget = Join-Path $pluginDestination 'NomadDrive.DedicatedServer.dll'
Assert-NoReparseAncestors $pluginTarget
Copy-Item -LiteralPath $plugin -Destination $pluginTarget -Force
Write-Host "CLI staged at $cli"
Write-Host "Hosting plugin staged at $pluginDestination"
