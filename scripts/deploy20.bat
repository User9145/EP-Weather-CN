@echo off
setlocal
set DST=C:\Program Files\ExplorerPatcher\ep_weather_local
set SRC=C:\Users\Admin\Documents\kimi\workspace\ep-weather\gen_test

taskkill /f /im ep_cmd_server.exe >nul 2>&1
timeout /t 3 /nobreak >nul
copy /y "%SRC%\ep_cmd_server.exe" "%DST%\"
if errorlevel 1 (echo COPY_FAILED & exit /b 1)
copy /y "%SRC%\ep_weather_local.exe" "%DST%\"
if errorlevel 1 (echo COPY_FAILED & exit /b 1)
start "" "%DST%\ep_cmd_server.exe"
timeout /t 2 /nobreak >nul
curl -s -o nul "http://127.0.0.1:24381/refresh"
echo DEPLOY_DONE
