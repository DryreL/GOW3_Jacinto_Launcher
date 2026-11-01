@echo off
title Gears of War 3 PC - Multi Patch Tool - Installation Audit
cd /d "%~dp0"
rem Read-only audit of what the Multi Patch Tool did to the game folder. It changes nothing.
rem Optional: Gears3_Multipatch_Verify.bat "D:\Games\Gears of War 3"   (game folder, when it is not next to the tool)
for %%I in ("%~dp0..\..") do set "GOW3_MULTIPATCH_ROOT=%%~fI"
set "PSExecutionPolicyPreference=Bypass"
if "%~1"=="" (
    powershell -NoProfile -File "%~dp0Gears3_Multipatch_Verify.ps1"
) else (
    powershell -NoProfile -File "%~dp0Gears3_Multipatch_Verify.ps1" -GameDir "%~1"
)
