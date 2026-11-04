@echo off
rem Tags the csproj version and publishes it on GitHub Releases. Run Build.bat (5. All) and commit first. It asks before anything is pushed.
setlocal
cd /d "%~dp0"
set "args=%*"
if "%args%"=="" (
    echo 1. Publish the csproj version
    echo 2. Delete the latest release and its tag ^(GitHub and local^)
    choice /c 12 /n /m "Choose 1 or 2: "
    if errorlevel 2 set "args=-DeleteLatest"
)
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Publish-Release.ps1" %args%
if errorlevel 1 echo Release failed.
pause
