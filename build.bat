@echo off
rem ============================================================================
rem  KL Win Health - build a single-file, self-contained release
rem  Output: publish\KLWinHealth.exe  (x64, runs without installing .NET)
rem ============================================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0"

rem --- find a .NET 8+ SDK (a dotnet.exe on PATH may be runtime-only) --------------
set "DOTNET="
for %%D in ("dotnet" "%ProgramFiles%\dotnet\dotnet.exe" "%LOCALAPPDATA%\Microsoft\dotnet-sdk8\dotnet.exe") do (
    if not defined DOTNET (
        for /f "tokens=1 delims=." %%V in ('"%%~D" --list-sdks 2^>nul') do (
            if %%V GEQ 8 set "DOTNET=%%~D"
        )
    )
)
if not defined DOTNET (
    echo [ERROR] No .NET 8 SDK was found.
    echo         Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run build.bat again.
    exit /b 1
)
echo  Using SDK: !DOTNET!

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

echo.
echo  Building KL Win Health (Release, win-x64, single file)...
echo.

if exist "publish\KLWinHealth.exe" (
    2>nul ( call ) >> "publish\KLWinHealth.exe" || (
        echo [ERROR] publish\KLWinHealth.exe is running. Exit KL Win Health ^(tray icon - Exit^) and run build.bat again.
        exit /b 1
    )
)
if exist publish rmdir /s /q publish

"!DOTNET!" publish "KLWinHealth\KLWinHealth.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=embedded ^
    -o publish
set "RC=!errorlevel!"

if not "!RC!"=="0" goto :failed
if not exist "publish\KLWinHealth.exe" goto :failed

echo.
echo  Done:  %CD%\publish\KLWinHealth.exe
echo  First run asks for administrator rights once (UAC) and installs to "%ProgramFiles%\KL Win Health".
echo  After that it opens with no UAC prompt.
echo.
endlocal
exit /b 0

:failed
echo.
echo [ERROR] Build failed (exit code !RC!). See the messages above.
endlocal
exit /b 1
