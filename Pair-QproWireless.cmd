@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Pair-QproWireless.ps1"
if errorlevel 1 echo Wireless pairing failed. Read the error above.
pause
