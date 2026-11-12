Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AbsoluteDirectory([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A directory path is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.TrimEnd('\', '/') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) {
        throw 'Use a dedicated subdirectory, not a drive root.'
    }
    return $full.TrimEnd('\', '/')
}

function Assert-NoReparseAncestors([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not supported: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Test-Within([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SeparateDirectories([string]$First, [string]$Second) {
    if ((Test-Within $First $Second) -or (Test-Within $Second $First)) {
        throw "Directories must be separate, without nesting: '$First' and '$Second'."
    }
}

function Get-SafeFiles([string]$Directory, [switch]$ExcludeDebugSymbols) {
    Assert-NoReparseAncestors $Directory
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Directory)
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        foreach ($entry in Get-ChildItem -LiteralPath $current -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not supported: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) {
                if ($ExcludeDebugSymbols -and $entry.Name -like '*_DoNotShip') { continue }
                $queue.Enqueue($entry.FullName)
            } else { $entry }
        }
    }
}

function Assert-GameBuild([string]$Directory) {
    $assembly = Join-Path $Directory 'Nomad Drive_Data/Managed/Assembly-CSharp.dll'
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'Nomad Drive.exe') -PathType Leaf)) {
        throw 'GameDirectory must contain Nomad Drive.exe.'
    }
    if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -ne '6957F9A1180D811CA2AE1BB44748157D794FED5BEFD845290FD15E41FB54FFAF') {
        throw 'Unsupported game assembly. This adapter requires Steam build 26391437. Do not bypass the version check.'
    }
    $stream = [IO.File]::OpenRead((Join-Path $Directory 'Nomad Drive_Data/globalgamemanagers'))
    try {
        $header = New-Object byte[] 256
        $count = $stream.Read($header, 0, $header.Length)
        if ([Text.Encoding]::ASCII.GetString($header, 0, $count) -notmatch '6000\.0\.73f1\x00') {
            throw 'Unsupported Unity version. Expected 6000.0.73f1.'
        }
    } finally { $stream.Dispose() }
    # The assembly hash identifies the supported build even for copies without Steam metadata.
    $manifest = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($Directory))) 'appmanifest_2223980.acf'
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        if ((Get-Content -LiteralPath $manifest -Raw) -notmatch '"buildid"\s+"26391437"') {
            throw 'The Steam manifest is not build 26391437.'
        }
    }
}

function Get-PinnedDownload([string]$Url, [string]$Destination, [string]$Sha256) {
    Assert-NoReparseAncestors $Destination
    if (-not (Test-Path -LiteralPath $Destination)) {
        $temporary = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.part'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host "Downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $temporary -UseBasicParsing
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Sha256) {
            throw "Downloaded checksum mismatch. Untrusted bytes retained for inspection: $temporary"
        }
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Cached download checksum mismatch; the file was not replaced: $Destination"
    }
    return $Destination
}

function Expand-SafeZip([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Assert-NoReparseAncestors $Destination
    $root = Get-AbsoluteDirectory $Destination
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or
                -not (Test-Within $target $root) -or -not $seen.Add($target)) {
                throw "Unsafe or duplicate archive entry: $($entry.FullName)"
            }
            Assert-NoReparseAncestors $target
            if ($entry.Name -and (Test-Path -LiteralPath $target)) { throw "Archive would overwrite $target" }
        }
        foreach ($entry in $zip.Entries) {
            $target = Join-Path $root $entry.FullName.Replace('/', '\')
            if (-not $entry.Name) { [void][IO.Directory]::CreateDirectory($target); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $zip.Dispose() }
}

function Assert-WindowsMonoProfile([string]$Directory, [string]$ManifestPath) {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $files = @(Get-SafeFiles $Directory)
    if ($files.Count -ne $manifest.files.Count) {
        throw 'The extracted Windows Mono profile is incomplete or contains unexpected files.'
    }
    foreach ($entry in $manifest.files) {
        $path = Join-Path $Directory $entry.path
        Assert-NoReparseAncestors $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Windows Mono profile checksum mismatch: $($entry.path)"
        }
    }
}
