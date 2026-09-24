@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch-QproWireless.ps1"
if errorlevel 1 (
  echo Wireless launch failed. Read the error above.
  pause
)
