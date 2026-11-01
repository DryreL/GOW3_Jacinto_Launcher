@echo off
rem Downloads the newest Gears of War 3 Jacinto Launcher package from GitHub Releases into the current folder.
rem It works alone, so it can be shared without the other files. To choose a platform: Gears3_Multipatch_Launcher_Download.bat Linux_x64
rem Platforms: Windows_x64, Linux_x64, macOS_arm64, macOS_x64
setlocal
echo You only need to download and install this once for your system. After that, the launcher offers updates by itself.
echo.
set "JACINTO_PLATFORM=%~1"
if not defined JACINTO_PLATFORM set "JACINTO_PLATFORM=Windows_x64"
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;$p=$env:JACINTO_PLATFORM;if('Windows_x64','Linux_x64','macOS_arm64','macOS_x64' -notcontains $p){throw \"Unknown platform '$p'.\"};$u=$env:JACINTO_RELEASES_URL;if(-not $u){$u='https://api.github.com/repos/DryreL/GOW3_Jacinto_Launcher/releases/latest'};$h=@{'User-Agent'='GoW3JacintoDownloader'};Write-Host \"Platform: $p\";$r=Invoke-RestMethod -Uri $u -Headers $h;$a=$r.assets|Where-Object{$_.name -match ('^Gears3_Multipatch_Launcher_(v[\d.]+_)?'+$p+'\.zip$')}|Select-Object -First 1;if(-not $a){throw \"Release $($r.tag_name) has no package for $p.\"};$t=Join-Path (Get-Location).Path $a.name;Write-Host \"Downloading $($a.name)...\";Invoke-WebRequest -Uri $a.browser_download_url -OutFile $t -UseBasicParsing -Headers $h;if($a.digest -match '^sha256:(.+)$'){if((Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash -ne $Matches[1]){Remove-Item -LiteralPath $t -Force;throw 'The download failed its SHA-256 check and was removed.'};Write-Host 'SHA-256 check passed.'};Write-Host \"Saved: $t\";Write-Host 'Unzip it anywhere and open GoW3Jacinto_Launcher.'"
if errorlevel 1 echo Download failed.
pause
