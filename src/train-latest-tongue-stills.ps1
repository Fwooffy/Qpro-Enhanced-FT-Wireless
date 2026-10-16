param(
    [string]$SessionPath = "",
    [ValidateRange(1, 200)]
    [int]$Epochs = 48,
    [ValidateRange(8, 256)]
    [int]$BatchSize = 96,
    [ValidateRange(0, 999)]
    [int]$Version = 0
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'runtime-python.ps1')
$qproGpuVisibilityNames = @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')
$qproSavedGpuVisibility = @{}
foreach ($name in $qproGpuVisibilityNames) {
    $qproSavedGpuVisibility[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$qproSavedGpuEnvironment = @{}
foreach ($name in @('ROCM_SDK_TARGET_FAMILY', 'QPRO_ROCM_INSTALL_SMOKE_TEST', 'QPRO_ROCM_EXPECTED_GFX_TARGET')) {
    $qproSavedGpuEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Restore-QproGpuVisibility {
    foreach ($name in $qproGpuVisibilityNames) {
        [Environment]::SetEnvironmentVariable($name, $qproSavedGpuVisibility[$name], 'Process')
    }
}
$trainingRunRoot = $null
Push-Location $PSScriptRoot
try {
    function Test-QproTrainingPython([string]$Candidate) {
        if ([string]::IsNullOrWhiteSpace($Candidate) -or -not (Test-Path -LiteralPath $Candidate -PathType Leaf)) { return $false }
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            & $Candidate -c "import sys,cv2,numpy,torch; assert sys.version_info[:2] == (3,12) and sys.maxsize > 2**32" *> $null
            return $LASTEXITCODE -eq 0
        } catch { return $false }
        finally { $ErrorActionPreference = $previousPreference }
    }

    $python = $null
    Remove-Item Env:QPRO_ROCM_INSTALL_SMOKE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:QPRO_ROCM_EXPECTED_GFX_TARGET -ErrorAction SilentlyContinue
    $rocmCandidates = @(Get-QproRocmCandidates $PSScriptRoot)
    foreach ($candidate in $rocmCandidates) {
        if (-not (Test-Path -LiteralPath $candidate.Python -PathType Leaf) -or
            -not (Test-Path -LiteralPath $candidate.ReadyMarker -PathType Leaf)) { continue }
        # Clear inherited masks only for ROCm so HIP can enumerate the
        # discrete card itself, independently of Windows display order.
        foreach ($name in $qproGpuVisibilityNames) {
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
        if ($candidate.TargetFamily) { $env:ROCM_SDK_TARGET_FAMILY = $candidate.TargetFamily }
        else { Remove-Item Env:ROCM_SDK_TARGET_FAMILY -ErrorAction SilentlyContinue }
        $probePreference = $ErrorActionPreference
        $probeSucceeded = $false
        try {
            $ErrorActionPreference = 'Continue'
            $global:LASTEXITCODE = $null
            & $candidate.Python -c "import torch; from qpro_gpu import require_rocm_device_name; d=require_rocm_device_name(torch); print('Tongue training GPU:', torch.cuda.get_device_name(int(d.split(':')[1])), 'on', d)"
            $probeSucceeded = $LASTEXITCODE -eq 0
        } catch { Write-Warning "$($candidate.Name) validation failed: $_" }
        finally { $ErrorActionPreference = $probePreference }
        if ($probeSucceeded) {
            $python = $candidate.Python
            Write-Host "Selected $($candidate.Name) runtime."
            break
        }
        Write-Warning "$($candidate.Name) cannot see a supported discrete Radeon GPU. Trying the next runtime."
    }
    if (-not $python) {
        Restore-QproGpuVisibility
        [Environment]::SetEnvironmentVariable('ROCM_SDK_TARGET_FAMILY', $qproSavedGpuEnvironment['ROCM_SDK_TARGET_FAMILY'], 'Process')
        $sharedPython = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'QproFaceTracking\runtime\.venv\Scripts\python.exe'
        $candidates = @(
            $env:QPRO_PYTHON,
            $sharedPython,
            (Join-Path $PSScriptRoot '.venv\Scripts\python.exe'),
            (Join-Path $PSScriptRoot '.venv\Scripts\qpro-python-console.exe')
        )
        foreach ($candidate in $candidates) {
            if (Test-QproTrainingPython $candidate) { $python = $candidate; break }
        }
        if (-not $python) { throw 'No working Qpro Python 3.12 training runtime was found. Run Install runtime in the Hub.' }
    }
    Write-Host "PC Python runtime: $python"
    $latest = if (-not [string]::IsNullOrWhiteSpace($SessionPath)) {
        Get-Item -LiteralPath ([System.IO.Path]::GetFullPath($SessionPath)) -ErrorAction Stop
    } else {
        Get-ChildItem -LiteralPath .\captures -Filter "*.qpsession.json" |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                try {
                    $session = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                    if ($session.sessionType -in @("tongue-stereo-stills-v1", "lower-face-stills-v1") -and $session.completed) {
                        $_
                    }
                } catch {
                    # Ignore incomplete or partially written unrelated sessions.
                }
            } | Select-Object -First 1
    }
    if ($null -eq $latest) {
        throw "No completed manual tongue-still capture was found under .\captures."
    }
    $sessionMetadata = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    if ($sessionMetadata.sessionType -notin @("tongue-stereo-stills-v1", "lower-face-stills-v1") -or -not $sessionMetadata.completed) {
        throw "The selected dataset is not a completed full tongue-still capture."
    }
    $capture = $latest.FullName -replace '\.qpsession\.json$', '.qpcap'
    if (-not (Test-Path -LiteralPath $capture)) {
        throw "Matching capture is missing: $capture"
    }
    $cache = Join-Path $PSScriptRoot ("training\{0}-tongue-stills-224px" -f [System.IO.Path]::GetFileNameWithoutExtension($capture))
    # A failed run can leave a direction or TorchScript file without a gate.
    # Reserve a version only when none of its artifacts already exists.
    $occupiedVersions = @(Get-ChildItem .\models -Filter "qpro-stereo-tongue-v*" -File -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -match '^qpro-stereo-tongue-v(?<v>\d+)(?:[.-]|$)') { [int]$Matches.v }
        })
    if ($Version -eq 0) {
        $Version = if ($occupiedVersions.Count) { (($occupiedVersions | Measure-Object -Maximum).Maximum + 1) } else { 1 }
    } elseif ($occupiedVersions -contains $Version) {
        throw "Tongue model v$Version already has files. Choose an unused version to preserve existing weights."
    }
    $trainingRunRoot = Join-Path $PSScriptRoot ('training\lower-face-run-' + [guid]::NewGuid().ToString('N'))
    $stagedModels = Join-Path $trainingRunRoot 'models'
    New-Item -ItemType Directory -Path $stagedModels -Force | Out-Null
    $gateOutput = Join-Path $stagedModels "qpro-stereo-tongue-v$Version-gate.pt"
    $directionOutput = Join-Path $stagedModels "qpro-stereo-tongue-v$Version-direction.pt"
    Write-Host "TRAIN_STATUS phase=preparing"
    Write-Host "Preparing exact manually selected stereo stills from $capture"
    & $python .\prepare_tongue_stills.py $capture --session $latest.FullName --output $cache --size 224
    if ($LASTEXITCODE -ne 0) { throw "Preparing the manual still dataset failed." }
    $deviceOutput = @(& $python -c "import torch; from qpro_gpu import preferred_torch_device_name; print(preferred_torch_device_name(torch))" | Select-Object -Last 1)
    $deviceExitCode = $LASTEXITCODE
    if ($deviceExitCode -ne 0 -or $deviceOutput.Count -eq 0) {
        throw "Could not determine the PyTorch training device (Python exit code $deviceExitCode). Check the preceding Activity output, then run PC runtime setup again."
    }
    $device = $deviceOutput[0].ToString().Trim()
    if ($device -ne 'cpu' -and $device -notmatch '^cuda:\d+$') { throw "Unsupported PyTorch training device: $device" }
    $effectiveBatchSize = if ($device -eq "cpu") { [Math]::Min($BatchSize, 16) } else { $BatchSize }
    Write-Host "TRAIN_DEVICE device=$device batch=$effectiveBatchSize"
    if ($device -eq "cpu") { Write-Warning "CUDA is unavailable. CPU fallback is active; full-dataset training may take hours. NVIDIA users should rerun PC runtime setup after installing the current NVIDIA driver." }
    $totalStages = 2
    Write-Host "TRAIN_STAGE index=1 total=$totalStages name=visibility epochs=$Epochs device=$device"
    Write-Host "Training the visibility checkpoint on $device"
    & $python .\train_tongue_model.py $cache `
        --architecture spatial-stereo-resnet-v2 `
        --checkpoint-focus visibility `
        --epochs $Epochs `
        --batch-size $effectiveBatchSize `
        --device $device `
        --output $gateOutput
    if ($LASTEXITCODE -ne 0) { throw "Training the visibility checkpoint failed." }
    Write-Host "TRAIN_STAGE index=2 total=$totalStages name=direction epochs=$Epochs device=$device"
    Write-Host "Training the direction checkpoint on $device"
    & $python .\train_tongue_model.py $cache `
        --architecture spatial-stereo-resnet-v2 `
        --checkpoint-focus direction `
        --epochs $Epochs `
        --batch-size $effectiveBatchSize `
        --device $device `
        --output $directionOutput
    if ($LASTEXITCODE -ne 0) { throw "Training the direction checkpoint failed." }
    & $python .\lower_face_training.py publish --staged-root $trainingRunRoot --root $PSScriptRoot --version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the trained tongue model failed. Existing models were kept.' }
    Write-Host "MODEL_READY version=$Version gate=$(Join-Path $PSScriptRoot "models\qpro-stereo-tongue-v$Version-gate.pt") direction=$(Join-Path $PSScriptRoot "models\qpro-stereo-tongue-v$Version-direction.pt")"
} finally {
    try {
        if ($trainingRunRoot -and (Test-Path -LiteralPath $trainingRunRoot)) {
            $resolvedTemporary = [System.IO.Path]::GetFullPath($trainingRunRoot)
            $allowedTraining = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'training')).TrimEnd('\') + '\'
            if (-not $resolvedTemporary.StartsWith($allowedTraining, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary training path.' }
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    } finally {
        Restore-QproGpuVisibility
        foreach ($name in $qproSavedGpuEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $qproSavedGpuEnvironment[$name], 'Process')
        }
        Pop-Location
    }
}
