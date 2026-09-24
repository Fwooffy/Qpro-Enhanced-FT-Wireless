@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-QproRocm.ps1"
if errorlevel 1 (
  echo.
  echo AMD ROCm setup failed. Read the error above, then retry this installer.
) else (
  echo.
  echo AMD ROCm setup completed. Open QproFaceTracking.exe to start.
)
pause
