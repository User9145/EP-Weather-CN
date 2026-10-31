@echo off
rem ============================================================
rem  EP "News and Interests" local panel v1.2 - uninstaller
rem  Run as Administrator (right-click -> Run as administrator)
rem ============================================================
setlocal
set EPDIR=C:\Program Files\ExplorerPatcher
set TGT=%EPDIR%\ep_weather_local

net session >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Please right-click and "Run as administrator".
  pause
  exit /b 1
)

echo This will remove the local "News and Interests" panel and restore
echo the official ExplorerPatcher files.
set /p OK=Uninstall? (y/n)
if /i not "%OK%"=="y" exit /b 0

rem [1] stop service, remove autostart and scheduled task
taskkill /f /im ep_cmd_server.exe >nul 2>&1
taskkill /f /im ep_weather_local.exe >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v EPWeatherLocal /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v EPNewsCmdServer /f >nul 2>&1
schtasks /delete /tn "EPWeatherLocal Refresh" /f >nul 2>&1
echo [1] service and autostart removed

rem [2] restore official DLLs (weather host + settings GUI)
for /f "tokens=2 delims=," %%a in ('tasklist /m ep_weather_host.dll /fo csv /nh ^| findstr /i dllhost') do taskkill /f /pid %%~a >nul 2>&1
if exist "%EPDIR%\ep_weather_host.dll.orig" (
  copy /y "%EPDIR%\ep_weather_host.dll.orig" "%EPDIR%\ep_weather_host.dll" >nul
  echo [2] ep_weather_host.dll restored
)
for /f "tokens=2 delims=," %%a in ('tasklist /m ep_gui.dll /fo csv /nh ^| findstr /i rundll32') do taskkill /f /pid %%~a >nul 2>&1
if exist "%EPDIR%\ep_gui.dll.orig" (
  copy /y "%EPDIR%\ep_gui.dll.orig" "%EPDIR%\ep_gui.dll" >nul
  echo [3] ep_gui.dll restored
)

rem [3] remove program directory (current location and legacy C:\EPW)
if exist "%TGT%\ep_weather_local.exe" (
  rmdir /s /q "%TGT%"
  echo [4] program directory removed
)
if exist "C:\EPW\ep_weather_local.exe" (
  rmdir /s /q "C:\EPW"
  echo [4] legacy C:\EPW removed
)

taskkill /f /im explorer.exe >nul 2>&1
start explorer.exe
echo.
echo Uninstall finished.
pause
