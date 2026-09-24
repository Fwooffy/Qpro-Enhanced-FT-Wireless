@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Connect-QproWireless.ps1"
if errorlevel 1 echo Wireless connection failed. Read the error above.
pause
