@echo off
rem ============================================================
rem  EP "News and Interests" local panel v1.2 - installer (v8)
rem  Requires ExplorerPatcher (https://github.com/valinet/ExplorerPatcher)
rem  Run as Administrator (right-click -> Run as administrator)
rem  Log: %TEMP%\epw_install.log
rem ============================================================
setlocal EnableDelayedExpansion
set SRC=%~dp0
set SRC=%SRC:~0,-1%
set EPDIR=C:\Program Files\ExplorerPatcher
set LOG=%TEMP%\epw_install.log
echo === install %date% %time% === ^> "%LOG%"

rem [0] admin check
net session >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Please right-click and "Run as administrator".
  pause
  exit /b 1
)

rem [1] ExplorerPatcher must be installed (registry key or dxgi.dll)
set EPFOUND=0
reg query "HKCU\Software\ExplorerPatcher" >nul 2>&1
if not errorlevel 1 set EPFOUND=1
if exist C:\Windows\dxgi.dll set EPFOUND=1
if %EPFOUND%==0 (
  echo ExplorerPatcher was NOT detected on this PC.
  echo Please install the official ExplorerPatcher first:
  echo   https://github.com/valinet/ExplorerPatcher/releases
  echo.
  echo Press any key to open the download page and exit...
  pause >nul
  start "" "https://github.com/valinet/ExplorerPatcher/releases"
  exit /b 1
)
echo [1] ExplorerPatcher detected
echo [1] ExplorerPatcher detected >> "%LOG%"

rem [1b] WebView2 Runtime is required by the EP weather host
reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv >nul 2>&1
if errorlevel 1 (
  echo WebView2 Runtime not found - downloading it now...
  powershell -NoProfile -Command "Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile \"%TEMP%\MicrosoftEdgeWebview2Setup.exe\"" >nul 2>&1
  if exist "%TEMP%\MicrosoftEdgeWebview2Setup.exe" (
    "%TEMP%\MicrosoftEdgeWebview2Setup.exe" /silent /install
    echo [1b] WebView2 Runtime installed
    echo webview2 installed >> "%LOG%"
  ) else (
    echo [ERROR] Could not download WebView2 Runtime. Connect this PC to the
    echo         internet and run install.bat again, or install it manually:
    echo         https://developer.microsoft.com/microsoft-edge/webview2/
    echo webview2 download FAILED >> "%LOG%"
    pause
    exit /b 1
  )
) else (
  echo [1b] WebView2 Runtime present
)

rem [1c] decide install dir: next to ExplorerPatcher itself (same as a manual
rem      host install). The DLL patch bakes a file:// URL built from real 8.3
rem      short names (the 50-char URL slot cannot hold the long path), so all
rem      three path components must have short names; if this volume refuses
rem      8.3 names entirely, fall back to C:\EPW.
set TGT=%EPDIR%\ep_weather_local
if not exist "%TGT%" mkdir "%TGT%"
set SN_PROG=NONE
set SN_EP=NONE
set SN_TGT=NONE
set /a SNI=0
for /f "usebackq delims=" %%s in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%\shortname.ps1" "C:\Program Files" "%EPDIR%" "%TGT%" 2^>nul`) do (
  set /a SNI+=1
  if !SNI! equ 1 set SN_PROG=%%s
  if !SNI! equ 2 set SN_EP=%%s
  if !SNI! equ 3 set SN_TGT=%%s
)
rem try to assign our dir's short name if it has none
if /i "%SN_TGT%"=="NONE" (
  fsutil file setShortName "%TGT%" EPWLOC~1 >nul 2>&1
  for /f "usebackq delims=" %%s in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%\shortname.ps1" "%TGT%" 2^>nul`) do set SN_TGT=%%s
)
set EXPECT=
if /i not "%SN_PROG%"=="NONE" if /i not "%SN_EP%"=="NONE" if /i not "%SN_TGT%"=="NONE" set EXPECT=file:///C:/%SN_PROG%/%SN_EP%/%SN_TGT%/w.html
if not defined EXPECT (
  echo [1c] 8.3 short names are disabled or missing on C: - enabling them so
  echo      the panel can live in Program Files like a normal install...
  fsutil 8dot3name set C: 0 >nul 2>&1
  rem the setting applies immediately, but only to NEWLY created names, so
  rem assign the missing short names explicitly
  if /i "%SN_PROG%"=="NONE" fsutil file setShortName "C:\Program Files" PROGRA~1 >nul 2>&1
  if /i "%SN_EP%"=="NONE" fsutil file setShortName "%EPDIR%" EXPLOR~1 >nul 2>&1
  fsutil file setShortName "%TGT%" EPWLOC~1 >nul 2>&1
  set SN_PROG=NONE
  set SN_EP=NONE
  set SN_TGT=NONE
  set /a SNI=0
  for /f "usebackq delims=" %%s in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%\shortname.ps1" "C:\Program Files" "%EPDIR%" "%TGT%" 2^>nul`) do (
    set /a SNI+=1
    if !SNI! equ 1 set SN_PROG=%%s
    if !SNI! equ 2 set SN_EP=%%s
    if !SNI! equ 3 set SN_TGT=%%s
  )
  if /i not "!SN_PROG!"=="NONE" if /i not "!SN_EP!"=="NONE" if /i not "!SN_TGT!"=="NONE" set EXPECT=file:///C:/!SN_PROG!/!SN_EP!/!SN_TGT!/w.html
)
if not defined EXPECT (
  echo [WARN] Could not enable 8.3 short names on this volume;
  echo        falling back to C:\EPW for the panel files.
  set TGT=C:\EPW
  set EXPECT=file:///C:/EPW/w.html
  echo fallback to C:\EPW >> "%LOG%"
)
if not exist "%TGT%" mkdir "%TGT%"
echo [1c] panel directory: %TGT%
echo [1c] panel url: %EXPECT%
echo [1c] panel directory: %TGT% >> "%LOG%"
echo [1c] panel url: %EXPECT% >> "%LOG%"

rem [2] weather-host DLL: check state, self-heal if damaged/foreign, then patch
if not exist "%EPDIR%\ep_weather_host.dll" (
  echo [ERROR] %EPDIR%\ep_weather_host.dll not found. ExplorerPatcher install looks incomplete.
  pause
  exit /b 1
)
"%SRC%\ep_patch_dll.exe" --check "%EPDIR%\ep_weather_host.dll" %EXPECT% >nul 2>&1
set EPCHECK=%errorlevel%
if %EPCHECK%==0 (
  echo [2] weather-host DLL already patched - keeping it
  echo [2] dll already patched >> "%LOG%"
) else (
  set NEEDPATCH=1
  if %EPCHECK%==2 (
    rem official DLL: refresh the .orig backup from it
    copy /y "%EPDIR%\ep_weather_host.dll" "%EPDIR%\ep_weather_host.dll.orig" >nul
    echo [2] official DLL backed up as ep_weather_host.dll.orig
  ) else (
    rem state 3 (damaged) or 4 (patched by an older build with another path):
    rem find a clean official DLL - local .orig first, else the bundled copy
    rem (only when its FileVersion matches the installed one)
    set SRCDLL=
    if exist "%EPDIR%\ep_weather_host.dll.orig" (
      "%SRC%\ep_patch_dll.exe" --check "%EPDIR%\ep_weather_host.dll.orig" %EXPECT% >nul 2>&1
      if !errorlevel!==2 set SRCDLL=%EPDIR%\ep_weather_host.dll.orig
    )
    if "!SRCDLL!"=="" (
      if exist "%SRC%\official\ep_weather_host.dll" (
        set VNOW=
        set VBOX=
        for /f "usebackq delims=" %%v in (`"%SRC%\ep_patch_dll.exe" --ver "%EPDIR%\ep_weather_host.dll"`) do set VNOW=%%v
        for /f "usebackq delims=" %%v in (`"%SRC%\ep_patch_dll.exe" --ver "%SRC%\official\ep_weather_host.dll"`) do set VBOX=%%v
        if "!VNOW!"=="!VBOX!" if not "!VNOW!"=="" set SRCDLL=%SRC%\official\ep_weather_host.dll
      )
    )
    if "!SRCDLL!"=="" (
      echo [ERROR] ep_weather_host.dll is damaged or was patched by another build,
      echo         and no clean copy for version %VNOW% is available.
      echo         Repair ExplorerPatcher first: run its ep_setup.exe as
      echo         administrator, then run this installer again.
      echo damaged dll, no clean source >> "%LOG%"
      pause
      exit /b 1
    )
    for /f "tokens=2 delims=," %%a in ('tasklist /m ep_weather_host.dll /fo csv /nh ^| findstr /i dllhost') do taskkill /f /pid %%~a >nul 2>&1
    copy /y "!SRCDLL!" "%EPDIR%\ep_weather_host.dll" >nul
    copy /y "!SRCDLL!" "%EPDIR%\ep_weather_host.dll.orig" >nul
    echo [2] official DLL restored from a clean copy
  )
  for /f "tokens=2 delims=," %%a in ('tasklist /m ep_weather_host.dll /fo csv /nh ^| findstr /i dllhost') do taskkill /f /pid %%~a >nul 2>&1
  "%SRC%\ep_patch_dll.exe" "%EPDIR%\ep_weather_host.dll" "%EPDIR%\ep_weather_host.dll" %EXPECT%
  if errorlevel 1 (
    echo [ERROR] DLL patch failed - see %LOG%
    echo patch FAILED >> "%LOG%"
    pause
    exit /b 1
  )
  echo [3] weather-host DLL patched
  echo [3] weather-host DLL patched >> "%LOG%"
)
if not exist "%EPDIR%\ep_weather_host.dll.orig" (
  echo [WARN] .orig backup missing - uninstall will not restore the official DLL.
  echo no .orig backup, non-fatal >> "%LOG%"
)

rem [3] component files
icacls "%TGT%" /grant "%USERNAME%:(OI)(CI)F" /t >nul 2>&1
copy /y "%SRC%\ep_weather_local.exe" "%TGT%\" >nul
copy /y "%SRC%\ep_cmd_server.exe" "%TGT%\" >nul
copy /y "%SRC%\GuiStrings.exe" "%TGT%\" >nul
copy /y "%SRC%\ep_weather_local_config.txt" "%TGT%\" >nul
copy /y "%SRC%\ep_patch_dll.exe" "%TGT%\" >nul
if not exist "%TGT%\icons" mkdir "%TGT%\icons" >nul 2>&1
copy /y "%SRC%\icons\*.png" "%TGT%\icons\" >nul
echo [4] component files deployed
echo [4] component files deployed >> "%LOG%"

rem [3b] rename EP settings entries (optional, non-fatal)
for /f "tokens=2 delims=," %%a in ('tasklist /m ep_gui.dll /fo csv /nh ^| findstr /i rundll32') do taskkill /f /pid %%~a >nul 2>&1
if exist "%EPDIR%\ep_gui.dll" (
  if not exist "%EPDIR%\ep_gui.dll.orig" copy /y "%EPDIR%\ep_gui.dll" "%EPDIR%\ep_gui.dll.orig" >nul
  "%TGT%\GuiStrings.exe" "%EPDIR%\ep_gui.dll" patch "Weather" "News and Interests" >nul 2>&1
)
reg add "HKCU\Software\ExplorerPatcher" /v WeatherWindowCornerPreference /t REG_DWORD /d 1 /f >nul 2>&1

rem [4] autostart + hourly weather refresh task
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v EPWeatherLocal /t REG_SZ /d "\"%TGT%\ep_weather_local.exe\"" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v EPNewsCmdServer /t REG_SZ /d "\"%TGT%\ep_cmd_server.exe\"" /f >nul
schtasks /create /tn "EPWeatherLocal Refresh" /tr "\"%TGT%\ep_weather_local.exe\" --weather" /sc minute /mo 60 /f >nul 2>&1
echo [5] autostart and scheduled task set
echo [5] autostart and scheduled task set >> "%LOG%"

rem [5] generate page, start local service, restart explorer
"%TGT%\ep_weather_local.exe"
if not exist "%TGT%\w.html" (
  copy /y "%SRC%\fallback_w.html" "%TGT%\w.html" >nul
  echo [5b] gen produced no w.html - fallback page installed
  echo fallback w.html used >> "%LOG%"
)
taskkill /f /im ep_cmd_server.exe >nul 2>&1
start "" "%TGT%\ep_cmd_server.exe"
taskkill /f /im explorer.exe >nul 2>&1
start explorer.exe

echo.
echo ============================================================
echo  Install finished! Click "News and Interests" on the taskbar.
echo  To uninstall, run uninstall.bat as administrator.
echo  Log: %TEMP%\epw_install.log
echo ============================================================
echo install finished >> "%LOG%"
pause
