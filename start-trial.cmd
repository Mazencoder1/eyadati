@echo off
rem Double-click to start the local trial; "start-trial.cmd -Stop" to stop it. See start-trial.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-trial.ps1" %*
echo.
pause
