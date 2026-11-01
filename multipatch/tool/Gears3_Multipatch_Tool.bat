@echo off
title Gears of War 3 PC - Multi Patch Tool
cd /d "%~dp0"

rem Validate the complete tool package before downloading or checking the live Gist.
rem The data files are normally in the _files folder; a package extracted file by file into one folder works too.
if exist "%~dp0_files\" goto :files_ok
if exist "%~dp0GearGame.u" goto :files_ok
goto :missing_files
:files_ok

rem The tool no longer has to sit inside the game folder: when the game is not found next to it,
rem the script opens a folder picker so you can select the Gears of War 3 folder.

rem Pass the tool folder so the downloaded script can locate the game files.
set "GOW3_MULTIPATCH_ROOT=%CD%"
set "GOW3_MULTIPATCH_LOCAL_SCRIPT=%~dp0Gears3_Multipatch_Tool.ps1"
set "GIST_URL=https://gist.githubusercontent.com/DryreL/de1c192d4b95842d90fa6c28800f1d6d/raw/Gears3_Multipatch_Tool.ps1"
set "REMOTE_SCRIPT=%~dp0remote_Gears3_Multipatch_Tool.ps1"

rem Ensure the local script has a UTF-8 BOM so Windows PowerShell 5.1 decodes multi-byte localized strings (Russian, Polish, etc.) correctly instead of failing with ANSI parser errors.
call :ensure_utf8_bom "%GOW3_MULTIPATCH_LOCAL_SCRIPT%"

cls
echo.
echo  Choose which script version to run:
echo    [1] Remote - latest version from GitHub Gist ^(recommended^)
echo    [2] Local  - Gears3_Multipatch_Tool.ps1 included with this package
echo.

:choose_script_source
set "SCRIPT_SOURCE="
set /p "SCRIPT_SOURCE=Select an option [1]: "
if "%SCRIPT_SOURCE%"=="" set "SCRIPT_SOURCE=1"
if "%SCRIPT_SOURCE%"=="1" goto :run_remote
if "%SCRIPT_SOURCE%"=="2" goto :run_local
echo  Invalid selection. Please enter 1 or 2.
echo.
goto :choose_script_source

:run_remote
cls
echo.
echo  ====================================================================
echo    REMOTE SCRIPT SECURITY NOTICE
echo  ====================================================================
echo.
echo  This warning applies only to the Remote option.
echo.
echo  The Remote option downloads the latest script from the official GitHub Gist
echo  and saves it as remote_Gears3_Multipatch_Tool.ps1 in this folder first.
echo  The downloaded file is then launched normally from disk.
echo.
echo  Official Gist:
echo  https://gist.github.com/DryreL/de1c192d4b95842d90fa6c28800f1d6d
echo.
echo  If you downloaded the complete package from the official NexusMods page
echo  and trust its publisher, review the downloaded file before running it.
echo.
echo  The Local option runs Gears3_Multipatch_Tool.ps1 included with this package.
echo.
pause

rem Download the latest script from the official Gist using Windows native curl.exe (built-in on Windows 10/11)
where curl.exe >nul 2>&1
if not errorlevel 1 (
    curl.exe -f -s -S -L "%GIST_URL%" -o "%REMOTE_SCRIPT%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; (New-Object Net.WebClient).DownloadFile('%GIST_URL%', '%REMOTE_SCRIPT%')"
)
if errorlevel 1 goto :remote_download_failed
if not exist "%REMOTE_SCRIPT%" goto :remote_download_failed

call :ensure_utf8_bom "%REMOTE_SCRIPT%"

set "GOW3_MULTIPATCH_SOURCE=Remote"
powershell -NoProfile -ExecutionPolicy Bypass -File "%REMOTE_SCRIPT%"
set "TOOL_EXIT_CODE=%ERRORLEVEL%"
goto :script_finished

:remote_download_failed
echo.
echo  [WARNING] Unable to download the Official Gist. Using the local script instead.
echo            The local script may be an older version.
echo.
if not exist "%GOW3_MULTIPATCH_LOCAL_SCRIPT%" (
    echo  [ERROR] The local Gears3_Multipatch_Tool.ps1 file was not found.
    exit /b 1
)
call :ensure_utf8_bom "%GOW3_MULTIPATCH_LOCAL_SCRIPT%"
set "GOW3_MULTIPATCH_SOURCE=Local fallback"
powershell -NoProfile -ExecutionPolicy Bypass -File "%GOW3_MULTIPATCH_LOCAL_SCRIPT%"
set "TOOL_EXIT_CODE=%ERRORLEVEL%"
goto :script_finished

:run_local
echo.
echo  [INFO] Using the local script.
if not exist "%GOW3_MULTIPATCH_LOCAL_SCRIPT%" (
    echo  [ERROR] The local Gears3_Multipatch_Tool.ps1 file was not found.
    exit /b 1
)
call :ensure_utf8_bom "%GOW3_MULTIPATCH_LOCAL_SCRIPT%"
set "GOW3_MULTIPATCH_SOURCE=Local"
powershell -NoProfile -ExecutionPolicy Bypass -File "%GOW3_MULTIPATCH_LOCAL_SCRIPT%"
set "TOOL_EXIT_CODE=%ERRORLEVEL%"

:script_finished
if not "%TOOL_EXIT_CODE%"=="0" (
    echo.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Write-Host '====================================================================' -ForegroundColor Red; Write-Host '  [FATAL ERROR] Multipatch Tool stopped due to an error! (Exit Code: %TOOL_EXIT_CODE%)' -ForegroundColor Red; Write-Host '  Please review the error details above.' -ForegroundColor Red; Write-Host '====================================================================' -ForegroundColor Red"
    echo.
    pause
)
exit /b %TOOL_EXIT_CODE%

:missing_files
cls
echo.
echo  [ERROR] The _files folder is missing.
echo.
echo  Running this BAT file by itself cannot apply every patch.
echo  Download the complete Multi Patch Tool package from:
echo  https://www.nexusmods.com/gearsofwar3/mods/27?tab=files
echo.
echo  After extracting it, make sure the extracted files include the _files folder
echo  ^(or all of its files^) next to this BAT file.
echo.
pause
exit /b 1

:ensure_utf8_bom
if not exist "%~1" exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f='%~1'; try { if (Test-Path -LiteralPath $f) { $b = [System.IO.File]::ReadAllBytes($f); if ($b.Length -ge 3 -and -not ($b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191)) { [System.IO.File]::WriteAllBytes($f, [byte[]](239, 187, 191) + $b) } } } catch {}"
exit /b 0
