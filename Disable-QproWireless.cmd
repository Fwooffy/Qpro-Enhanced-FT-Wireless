@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Disable-QproWireless.ps1"
if errorlevel 1 echo Wireless shutdown failed. Read the error above.
pause
