@echo off
rem Tags the csproj version and publishes it on GitHub Releases. Run Build.bat (5. All) and commit first. It asks before anything is pushed.
setlocal
cd /d "%~dp0"
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Publish-Release.ps1" %*
if errorlevel 1 echo Release failed.
pause
