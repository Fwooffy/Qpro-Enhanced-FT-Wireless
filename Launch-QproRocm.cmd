@echo off
setlocal
set "QPRO_ROOT=%~dp0"
set "QPRO_PYTHON=%QPRO_ROOT%.venv-rocm\Scripts\python.exe"
if not exist "%QPRO_PYTHON%" (
  echo ROCm runtime is missing. Run Install-QproRocm.ps1 first.
  pause
  exit /b 1
)
tasklist /FI "IMAGENAME eq QproFaceTracking.exe" /NH 2>NUL | find /I "QproFaceTracking.exe" >NUL
if not errorlevel 1 (
  echo QproFaceTracking is already open. Close it first, then run this launcher.
  pause
  exit /b 1
)
pushd "%QPRO_ROOT%"
"%QPRO_PYTHON%" -c "import torch; from train_tongue_model import create_model; assert torch.version.hip and torch.cuda.is_available() and not torch.backends.cudnn.enabled; m=torch.nn.Sequential(torch.nn.Conv2d(2,8,3,padding=1),torch.nn.BatchNorm2d(8)).cuda(); x=torch.rand(2,2,32,32,device='cuda'); m(x).mean().backward(); torch.cuda.synchronize(); print('AMD GPU ready:',torch.cuda.get_device_name(0))"
if errorlevel 1 (
  echo AMD GPU model check failed. QproFaceTracking was not launched.
  pause
  exit /b 1
)
start "" /D "%QPRO_ROOT%" "%QPRO_ROOT%QproFaceTracking.exe"
