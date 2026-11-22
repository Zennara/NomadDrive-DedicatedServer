#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-common.ps1')
$source = Get-AbsoluteDirectory $SourceDirectory
$destination = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/NomadDriveServerPanel'
$programs = [Environment]::GetFolderPath('Programs')
$shortcutPath = Join-Path $programs 'Nomad Drive Server Panel.lnk'
Assert-NoReparseAncestors $source
Assert-NoReparseAncestors $destination
Assert-NoReparseAncestors $shortcutPath

$files = foreach ($app in 'NomadDrive.Panel', 'NomadDrive.Server') {
    foreach ($extension in 'exe', 'dll', 'deps.json', 'runtimeconfig.json') { "$app.$extension" }
}
foreach ($file in $files) {
    $inputFile = Join-Path $source $file
    Assert-NoReparseAncestors $inputFile
    Assert-NoReparseAncestors (Join-Path $destination $file)
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Missing build file: $inputFile" }
}
if ($source -eq $destination) { throw 'Choose a build folder outside the installation.' }
if (Get-Process -Name 'NomadDrive.Panel' -ErrorAction SilentlyContinue) {
    throw 'Close the running panel before installing an update.'
}
[void][IO.Directory]::CreateDirectory($destination)
[void][IO.Directory]::CreateDirectory($programs)
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $destination $file) -Force }
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $destination 'NomadDrive.Panel.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.Description = 'Manage Nomad Drive dedicated servers'
$shortcut.Save()
Write-Output $shortcutPath
