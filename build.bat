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

rem --- optional code signing ---------------------------------------------------------
rem  Put your certificate settings in signing.local.bat (git-ignored), e.g.
rem      set "KL_SIGN_PFX=C:\certs\kl-codesign.pfx"
rem      set "KL_SIGN_PASSWORD=..."
rem  or, for a certificate in the Windows store / on a USB token:
rem      set "KL_SIGN_THUMBPRINT=0123ABCD..."
rem  Optional: set "KL_SIGN_TIMESTAMP=http://timestamp.digicert.com"
if exist "signing.local.bat" call "signing.local.bat"
set "SIGNED=no"
if defined KL_SIGN_PFX goto :sign
if defined KL_SIGN_THUMBPRINT goto :sign
echo.
echo  [NOTE] Not code-signed. Other PCs will show SmartScreen "Unknown publisher".
echo         See "Code signing" in README.md to set it up.
goto :signed

:sign
set "SIGNTOOL="
for /f "delims=" %%S in ('dir /b /s /o-n "%ProgramFiles(x86)%\Windows Kits\10\bin\*signtool.exe" 2^>nul ^| findstr /i "\\x64\\signtool.exe"') do (
    if not defined SIGNTOOL set "SIGNTOOL=%%S"
)
if not defined SIGNTOOL (
    echo [ERROR] Signing is configured but signtool.exe was not found. Install the Windows SDK ^(Signing Tools^).
    exit /b 1
)
if not defined KL_SIGN_TIMESTAMP set "KL_SIGN_TIMESTAMP=http://timestamp.digicert.com"
if defined KL_SIGN_PFX (
    "!SIGNTOOL!" sign /fd SHA256 /td SHA256 /tr "!KL_SIGN_TIMESTAMP!" /d "KL Win Health" /f "!KL_SIGN_PFX!" /p "!KL_SIGN_PASSWORD!" "publish\KLWinHealth.exe"
) else (
    "!SIGNTOOL!" sign /fd SHA256 /td SHA256 /tr "!KL_SIGN_TIMESTAMP!" /d "KL Win Health" /sha1 "!KL_SIGN_THUMBPRINT!" "publish\KLWinHealth.exe"
)
if errorlevel 1 (
    echo [ERROR] Code signing failed. See the messages above.
    exit /b 1
)
set "SIGNED=yes"

:signed
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Get-Item 'publish\KLWinHealth.exe').VersionInfo.ProductVersion"`) do set "VER=%%V"
certutil -hashfile "publish\KLWinHealth.exe" SHA256 | findstr /v ":" > "publish\KLWinHealth.exe.sha256"

echo.
echo  Done:  %CD%\publish\KLWinHealth.exe
echo  Version !VER!   Signed: !SIGNED!   SHA-256 in publish\KLWinHealth.exe.sha256
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
