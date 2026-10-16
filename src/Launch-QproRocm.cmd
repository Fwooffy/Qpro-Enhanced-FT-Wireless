@echo off
setlocal
set "QPRO_HOME=%~dp0.."
if not exist "%QPRO_HOME%\QproFaceTracking.exe" set "QPRO_HOME=%~dp0."
set "QPRO_ROOT=%QPRO_HOME%\QproRuntime"
if not exist "%QPRO_ROOT%\train_tongue_model.py" set "QPRO_ROOT=%QPRO_HOME%"
set "QPRO_HUB=%QPRO_HOME%\QproFaceTracking.exe"
if not exist "%QPRO_HUB%" (
  echo QproFaceTracking.exe is missing. Extract the full ZIP again.
  pause
  exit /b 1
)
tasklist /FI "IMAGENAME eq QproFaceTracking.exe" /NH 2>NUL | find /I "QproFaceTracking.exe" >NUL
if not errorlevel 1 (
  echo Qpro Companion is already open. Close it first, then run this launcher.
  pause
  exit /b 1
)

rem Use the same compact/shared and release-local resolution as tracking/training.
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%QPRO_ROOT%\runtime-python.ps1" -LaunchRocmHub -RocmLaunchRoot "%QPRO_ROOT%" -RocmHubExecutable "%QPRO_HUB%"
if errorlevel 1 (
  pause
  exit /b 1
)
exit /b 0
