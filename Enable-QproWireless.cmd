@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Enable-QproWireless.ps1"
if errorlevel 1 echo Wireless setup failed. Read the error above.
pause
