param(
    [string]$QproRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$qproPrivatePython = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\python-3.12.10\python.exe'
$qproRocmEnv = Join-Path $QproRoot '.venv-rocm'
$qproRocmPython = Join-Path $qproRocmEnv 'Scripts\python.exe'
$qproRequirements = Join-Path $QproRoot 'requirements-runtime.txt'
$qproGateModel = Join-Path $QproRoot 'models\qpro-stereo-tongue-v8-gate.pt'
$qproDirectionModel = Join-Path $QproRoot 'models\qpro-stereo-tongue-v8-direction.pt'

if (-not (Test-Path -LiteralPath $qproPrivatePython)) {
    throw "Open QproFaceTracking.exe and run Set up PC runtime first, then rerun this AMD installer."
}
if (-not (Test-Path -LiteralPath $qproRequirements)) {
    throw "The QproFaceTracking release was not found: $QproRoot"
}
$qproSiteCustomize = Join-Path $qproRocmEnv 'Lib\site-packages\sitecustomize.py'
if (Test-Path -LiteralPath $qproSiteCustomize) {
    throw "Remove the existing Python startup customization before continuing: $qproSiteCustomize"
}

& $qproPrivatePython -c 'import sys; assert sys.version_info[:2] == (3, 12), sys.version'
if ($LASTEXITCODE -ne 0) { throw 'QproFaceTracking requires Python 3.12 for these AMD wheels.' }

if (-not (Test-Path -LiteralPath $qproRocmPython)) {
    & $qproPrivatePython -m venv $qproRocmEnv
    if ($LASTEXITCODE -ne 0) { throw 'Creating the separate ROCm Python environment failed.' }
}

$qproRuntimeReady = $false
$qproSavedErrorActionPreference = $ErrorActionPreference
try {
    # Windows PowerShell 5.1 can promote Python's expected import traceback on
    # stderr into a terminating NativeCommandError when preference is Stop.
    $ErrorActionPreference = 'Continue'
    & $qproRocmPython -c "import cv2,numpy,torch; assert torch.version.hip and torch.cuda.is_available()" > $null 2>&1
    $qproRuntimeReady = $LASTEXITCODE -eq 0
} finally {
    $ErrorActionPreference = $qproSavedErrorActionPreference
}

if (-not $qproRuntimeReady) {
    & $qproRocmPython -m pip install --disable-pip-version-check --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw 'Updating pip in the ROCm environment failed.' }

    $qproAmdSdkPackages = @(
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl',
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl',
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl',
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz'
    )
    & $qproRocmPython -m pip install --disable-pip-version-check --no-cache-dir @qproAmdSdkPackages
    if ($LASTEXITCODE -ne 0) { throw 'Installing AMD ROCm 7.2.1 components failed.' }

    $qproAmdTorchPackages = @(
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl',
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl',
        'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl'
    )
    & $qproRocmPython -m pip install --disable-pip-version-check --no-cache-dir @qproAmdTorchPackages
    if ($LASTEXITCODE -ne 0) { throw 'Installing AMD ROCm PyTorch 2.9.1 failed.' }

    & $qproRocmPython -m pip install --disable-pip-version-check -r $qproRequirements
    if ($LASTEXITCODE -ne 0) { throw 'Installing QproFaceTracking Python requirements failed.' }
}

$qproFix = "if torch.version.hip:`r`n    # Windows MIOpen HIPRTC cannot compile these tongue-model BatchNorm kernels.`r`n    torch.backends.cudnn.enabled = False`r`n"
foreach ($qproScript in @('train_tongue_model.py', 'tongue_model_preview.py')) {
    $qproScriptPath = Join-Path $QproRoot $qproScript
    if (-not (Test-Path -LiteralPath $qproScriptPath)) { throw "Missing Qpro script: $qproScriptPath" }
    $qproText = [System.IO.File]::ReadAllText($qproScriptPath)
    if ($qproText.Contains('torch.backends.cudnn.enabled = False')) { continue }
    $qproImport = [regex]::Match($qproText, '(?m)^import torch\r?\n')
    if (-not $qproImport.Success) { throw "Cannot locate the torch import in $qproScriptPath" }
    $qproNewline = if ($qproImport.Value.EndsWith("`r`n")) { "`r`n" } else { "`n" }
    $qproInsertion = "$qproNewline" + $qproFix.Replace("`r`n", $qproNewline)
    $qproBackup = "$qproScriptPath.pre-rocm.bak"
    if (-not (Test-Path -LiteralPath $qproBackup)) { Copy-Item -LiteralPath $qproScriptPath -Destination $qproBackup }
    $qproUpdated = $qproText.Insert($qproImport.Index + $qproImport.Length, $qproInsertion)
    [System.IO.File]::WriteAllText($qproScriptPath, $qproUpdated, [System.Text.UTF8Encoding]::new($false))
}

& $qproRocmPython -c "import cv2,numpy,torch; print('PyTorch:',torch.__version__); print('HIP:',torch.version.hip); print('GPU:',torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'unavailable'); assert torch.version.hip and torch.cuda.is_available()"
if ($LASTEXITCODE -ne 0) { throw 'ROCm installed, but PyTorch did not detect the GPU.' }

$env:PYTHONPATH = $QproRoot
& $qproRocmPython -c "import sys,torch; from train_tongue_model import create_model; assert not torch.backends.cudnn.enabled; c=torch.load(sys.argv[1],map_location='cpu',weights_only=False); m=create_model(c['architecture'],list(c['targetNames'])).cuda(); x=torch.rand(2,2,c['imageSize'],c['imageSize'],device='cuda'); m(x).float().square().mean().backward(); torch.cuda.synchronize(); print('GPU training smoke test passed')" $qproGateModel
if ($LASTEXITCODE -ne 0) { throw 'The Qpro tongue model failed a GPU training step.' }

& $qproRocmPython -c "import sys,numpy as np,torch; from tongue_model_preview import LiveTongueModelPreview; assert not torch.backends.cudnn.enabled; p=LiveTongueModelPreview(sys.argv[1],direction_checkpoint_path=sys.argv[2]); assert p.device.type=='cuda'; p.predict(np.zeros((400,800),dtype=np.uint8),None,[]); torch.cuda.synchronize(); print('GPU inference smoke test passed')" $qproGateModel $qproDirectionModel
if ($LASTEXITCODE -ne 0) { throw 'The Qpro tongue model failed a GPU inference step.' }

Write-Host "ROCm runtime ready: $qproRocmPython"
Write-Host 'Open QproFaceTracking.exe. Tongue tracking and training will select this ROCm runtime automatically.'
