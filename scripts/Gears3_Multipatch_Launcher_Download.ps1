# Downloads the newest Gears of War 3 Jacinto Launcher package from GitHub Releases into the current folder.
# It picks the package for this system. Works from a checkout or a Gist:
#   irm <raw url>/Gears3_Multipatch_Launcher_Download.ps1 | iex
# To choose yourself: .\Gears3_Multipatch_Launcher_Download.ps1 -Platform Linux_x64
# Platforms: Windows_x64, Linux_x64 (also Steam Deck), macOS_arm64, macOS_x64
[CmdletBinding()]
param([string]$Platform = $env:JACINTO_PLATFORM, [string]$OutDir = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Write-Host 'You only need to download and install this once for your system. After that, the launcher offers updates by itself.'
Write-Host ''
$known = 'Windows_x64', 'Linux_x64', 'macOS_arm64', 'macOS_x64'

function Find-Platform {
    # $IsWindows and the others exist only from PowerShell 6; Windows PowerShell has neither.
    if ($PSVersionTable.PSEdition -ne 'Core' -or $IsWindows) { return 'Windows_x64' }
    if ($IsLinux) { return 'Linux_x64' }
    $arch = (& uname -m)
    if ($arch -eq 'arm64' -or (& sysctl -n hw.optional.arm64 2>$null) -eq '1') { return 'macOS_arm64' }
    return 'macOS_x64'
}

try {
    if (-not $Platform) { $Platform = Find-Platform }
    if ($known -notcontains $Platform) { throw "Unknown platform '$Platform'. Use one of: $($known -join ', ')." }
    $url = if ($env:JACINTO_RELEASES_URL) { $env:JACINTO_RELEASES_URL } else { 'https://api.github.com/repos/DryreL/GOW3_Jacinto_Launcher/releases/latest' }
    Write-Host "Platform: $Platform"
    Write-Host 'Looking for the latest release...'
    $release = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'GoW3JacintoDownloader' }
    $asset = $release.assets | Where-Object { $_.name -match ('^Gears3_Multipatch_Launcher_(v[\d.]+_)?' + $Platform + '\.zip$') } | Select-Object -First 1
    if (-not $asset) { throw "Release $($release.tag_name) has no package for $Platform." }
    $target = Join-Path $OutDir $asset.name
    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -UseBasicParsing -Headers @{ 'User-Agent' = 'GoW3JacintoDownloader' }
    # GitHub publishes each file's SHA-256 as "digest"; a download that does not match is removed.
    if ($asset.digest -match '^sha256:(.+)$') {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Matches[1]) { Remove-Item -LiteralPath $target -Force; throw 'The download failed its SHA-256 check and was removed.' }
        Write-Host 'SHA-256 check passed.'
    }
    Write-Host "Saved: $target"
    Write-Host 'Unzip it anywhere and open GoW3Jacinto_Launcher.'
} catch {
    Write-Host "Download failed: $($_.Exception.Message)" -ForegroundColor Red
    # Not exit: pasted through iex, that would close the player's window.
    if ($PSCommandPath) { exit 1 }
}
