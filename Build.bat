@echo off
rem Builds the player packages into dist\. The source zip is always rebuilt too.
setlocal
cd /d "%~dp0"
echo.
echo   Gears of War 3 Jacinto Launcher - build
echo.
echo   1. Windows x64
echo   2. Linux x64 / Steam Deck
echo   3. macOS Apple Silicon  (test build, not signed)
echo   4. macOS Intel          (test build, not signed)
echo   5. All
echo.
echo   The release macOS download is ONE signed and notarized .dmg for both Macs. It is built by GitHub
echo   Actions on a Mac (tools\Package-macOS.sh) when Release.bat pushes the tag, never here: Windows cannot sign for macOS.
echo.
set /p "CHOICE=Choose 1-5: "
set "RIDS="
if "%CHOICE%"=="1" set "RIDS=win-x64"
if "%CHOICE%"=="2" set "RIDS=linux-x64"
if "%CHOICE%"=="3" set "RIDS=osx-arm64"
if "%CHOICE%"=="4" set "RIDS=osx-x64"
if "%CHOICE%"=="5" set "RIDS=win-x64,linux-x64,osx-arm64,osx-x64"
if not defined RIDS (
    echo Invalid choice.
    pause
    exit /b 1
)
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0tools\Build-Package.ps1' -Rids %RIDS%"
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo.
echo Done: %~dp0dist
pause
