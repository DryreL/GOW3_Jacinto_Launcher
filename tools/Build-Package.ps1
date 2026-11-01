[CmdletBinding()]
param(
    # win-x64, linux-x64, osx-arm64, osx-x64. Build.bat is the usual way in.
    [string[]]$Rids = @('win-x64','linux-x64','osx-arm64','osx-x64'),
    # From tools\Build-Patchset.ps1. The csproj embeds patch-data\multipatch.jcpatch into the executable.
    [string]$Patchset,
    # Native sources of the JacintoNet DLL, added to the source zip when present. Never the Steamworks SDK.
    [string]$JacintoNetSource
)
# One zip per platform plus a separate source zip, all in dist\. The player packages need no PowerShell.
$ErrorActionPreference = 'Stop'
if (-not $JacintoNetSource) { $JacintoNetSource = Join-Path $PSScriptRoot '..\multipatch\tool\_files\JacintoNet' }
if (-not $Patchset) { $Patchset = Join-Path $PSScriptRoot '..\patch-data\multipatch.jcpatch' }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $repo 'dist'; $staging = Join-Path $dist '_staging'
if (-not (Test-Path -LiteralPath $Patchset)) { throw "Patch data not found: $Patchset. Run tools\Build-Patchset.ps1 first." }
$names = @{ 'win-x64' = 'Windows_x64'; 'linux-x64' = 'Linux_x64'; 'osx-arm64' = 'macOS_arm64'; 'osx-x64' = 'macOS_x64' }
$packageName = 'Gears3_Multipatch_Launcher'
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'GoW3Jacinto_Launcher.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in the csproj.' }
# Entries are written by hand: Windows PowerShell's ZipFile puts backslashes in names, which breaks on Linux.
function New-Zip([string]$Folder, [string]$Zip, [switch]$Unix) {
    if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
    $archive = [IO.Compression.ZipFile]::Open($Zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Folder -Recurse -File) {
            $entry = $archive.CreateEntry($file.FullName.Substring($Folder.Length + 1).Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
            $output = $entry.Open(); $input = $file.OpenRead()
            try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
        }
    } finally { $archive.Dispose() }
    if ($Unix) { Set-UnixModes $Zip }
    Write-Output ("{0} ({1} MB)" -f (Split-Path -Leaf $Zip), [int]((Get-Item -LiteralPath $Zip).Length / 1MB))
}
# unzip honours file modes only in entries marked as made on Unix, which no Windows zip API can do. So the central directory is edited.
function Set-UnixModes([string]$Zip) {
    $bytes = [IO.File]::ReadAllBytes($Zip)
    $end = $bytes.Length - 22
    while ([BitConverter]::ToUInt32($bytes, $end) -ne 0x06054B50) { $end-- }
    $at = [BitConverter]::ToUInt32($bytes, $end + 16)
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($bytes, $end + 10); $i++) {
        $nameLength = [BitConverter]::ToUInt16($bytes, $at + 28)
        $name = [Text.Encoding]::UTF8.GetString($bytes, $at + 46, $nameLength)
        $mode = if ($name.EndsWith('/GoW3Jacinto_Launcher')) { 0x81ED } else { 0x81A4 }
        $bytes[$at + 5] = 3
        [BitConverter]::GetBytes([uint32]($mode * 65536)).CopyTo($bytes, $at + 38)
        $at += 46 + $nameLength + [BitConverter]::ToUInt16($bytes, $at + 30) + [BitConverter]::ToUInt16($bytes, $at + 32)
    }
    [IO.File]::WriteAllBytes($Zip, $bytes)
}

foreach ($rid in $Rids) {
    if (-not $names.ContainsKey($rid)) { throw "Unknown platform: $rid" }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $package = Join-Path $staging $packageName
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    & dotnet publish (Join-Path $repo 'GoW3Jacinto_Launcher.csproj') -c Release -r $rid -o $package --nologo -v quiet -p:JacintoEmbedPatch=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $rid failed." }
    Copy-Item -LiteralPath (Join-Path $repo 'packaging\INSTALL_TUTORIAL.txt'),(Join-Path $repo 'changelog.txt'),(Join-Path $repo 'languages.csv'),(Join-Path $repo 'LICENSE') -Destination $package
    $zip = Join-Path $dist "Gears3_Multipatch_Launcher_v$($version)_$($names[$rid]).zip"
    New-Zip $staging $zip -Unix:($rid -ne 'win-x64')
}

# Attach these two to a release whose fixes changed: the updater brings them to players who already installed.
Copy-Item -LiteralPath $Patchset -Destination (Join-Path $dist 'multipatch.jcpatch') -Force
[IO.File]::WriteAllText((Join-Path $dist 'multipatch.jcpatch.sha256'), (Get-FileHash -LiteralPath $Patchset).Hash)

# Tracked and new files only, so build output and keys.local.props stay out.
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
$source = Join-Path $staging 'GOW3_Jacinto_Launcher'
foreach ($file in & git -C $repo ls-files --cached --others --exclude-standard | Where-Object { $_ -notlike 'multipatch/*' }) {
    $from = Join-Path $repo ($file -replace '/','\')
    if (-not (Test-Path -LiteralPath $from)) { continue }
    $to = Join-Path $source ($file -replace '/','\')
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $to))
    Copy-Item -LiteralPath $from -Destination $to
}
if (Test-Path -LiteralPath $JacintoNetSource) {
    $native = Join-Path $source 'JacintoNet'
    New-Item -ItemType Directory -Force -Path (Join-Path $native 'src') | Out-Null
    Copy-Item -Path (Join-Path $JacintoNetSource 'src\*') -Destination (Join-Path $native 'src') -Exclude '*.before_*'
    Copy-Item -LiteralPath (Join-Path $JacintoNetSource 'build.bat') -Destination $native
}
New-Zip $staging (Join-Path $dist "Gears3_Multipatch_Launcher_v$($version)_Source.zip")
Remove-Item -LiteralPath $staging -Recurse -Force
