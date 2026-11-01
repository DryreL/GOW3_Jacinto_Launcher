@echo off
rem Builds JacintoNet (wsock32.dll proxy) into _dev\JacintoNet\bin. Needs Visual Studio (C++ x64) and the Steamworks SDK.
setlocal
set "HERE=%~dp0"
set "SDK=%HERE%..\..\_sdk\steamworks_sdk\sdk"
if not exist "%SDK%\public\steam\steam_api.h" ( echo Steamworks SDK not found at %SDK% & exit /b 1 )
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist "%HERE%bin" mkdir "%HERE%bin"
rc /nologo /fo"%HERE%bin\resource.res" "%HERE%src\resource.rc" || exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /W3 /LD /guard:cf /I"%SDK%\public" "%HERE%src\jacintonet.cpp" ^
   /Fe"%HERE%bin\wsock32.dll" /Fo"%HERE%bin\\" ^
   "%HERE%bin\resource.res" ^
   /link /DEF:"%HERE%src\wsock32.def" /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf ^
   "%SDK%\redistributable_bin\win64\steam_api64.lib" ws2_32.lib mswsock.lib user32.lib delayimp.lib /DELAYLOAD:steam_api64.dll || exit /b 1
copy /y "%SDK%\redistributable_bin\win64\steam_api64.dll" "%HERE%bin\" >nul
echo Built: %HERE%bin\wsock32.dll
