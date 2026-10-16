param(
    [string]$SessionPath = "",
    [ValidateRange(1, 100)]
    [int]$Epochs = 24,
    [ValidateRange(8, 256)]
    [int]$BatchSize = 64,
    [ValidateRange(0, 2147483647)]
    [int]$BaseVersion = 0
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
$parentTemporaryRoot = $null
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
        Get-ChildItem .\captures -Filter "*.qpsession.json" -File |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                try {
                    $session = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                    if ($session.sessionType -in @("tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3", "lower-face-refinement-v1") -and $session.completed) { $_ }
                } catch {}
            } | Select-Object -First 1
    }
    if ($null -eq $latest) { throw "No completed quick-refinement capture was found." }
    $sessionMetadata = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    if ($sessionMetadata.sessionType -notin @("tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3", "lower-face-refinement-v1") -or -not $sessionMetadata.completed) {
        throw "The selected dataset is not a completed quick-refinement capture."
    }
    $capture = $latest.FullName -replace '\.qpsession\.json$', '.qpcap'
    if (-not (Test-Path -LiteralPath $capture)) { throw "Matching capture is missing: $capture" }
    $pairs = @(Get-ChildItem .\models -Filter "qpro-stereo-tongue-v*-gate.pt" -File | ForEach-Object {
        if ($_.Name -match '^qpro-stereo-tongue-v(?<v>\d+)-gate\.pt$') {
            $v = [int]$Matches.v
            $direction = Join-Path $_.DirectoryName "qpro-stereo-tongue-v$v-direction.pt"
            if (Test-Path -LiteralPath $direction) {
                $experimental = $false
                $bundledDeveloper = $false
                $metadataPath = Join-Path $_.DirectoryName "qpro-stereo-tongue-v$v.metadata.json"
                if (Test-Path -LiteralPath $metadataPath) {
                    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
                    $experimental = $metadata.isExperimental -eq $true -or $metadata.modelKind -eq 'mustachio-experimental'
                    $bundledDeveloper = $metadata.bundledDeveloperModel -eq $true -and
                        $metadata.origin -eq 'bundled developer model' -and
                        $metadata.modelKind -in @('developer-tongue', 'developer-lower-face')
                }
                [pscustomobject]@{ Version=$v; Gate=$_.FullName; Direction=$direction; Experimental=$experimental; BundledDeveloper=$bundledDeveloper }
            }
        }
    } | Sort-Object Version -Descending)
    if (-not $pairs.Count) { throw "No paired base tongue model was found." }
    # The Hub passes its selected parent. When this helper is run directly,
    # allow the release's Developer model but require explicit selection for
    # optional experiments such as Mustachio.
    $base = if ($BaseVersion -gt 0) {
        $pairs | Where-Object Version -eq $BaseVersion | Select-Object -First 1
    } else {
        $pairs | Where-Object { -not $_.Experimental -or $_.BundledDeveloper } | Select-Object -First 1
    }
    if ($null -eq $base) { throw 'The selected refinement base is missing, or no ordinary base model is available.' }
    Write-Host "Refinement base: tongue model v$($base.Version)"

    $parentTemporaryRoot = Join-Path $PSScriptRoot ('training\tongue-parent-' + [guid]::NewGuid().ToString('N'))
    $trainingGate = Join-Path $parentTemporaryRoot 'gate.pt'
    $trainingDirection = Join-Path $parentTemporaryRoot 'direction.pt'
    & $python .\lower_face_training.py unwrap $base.Gate --output $trainingGate
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the selected tongue parent. Original model kept.' }
    & $python .\lower_face_training.py unwrap $base.Direction --output $trainingDirection
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the selected direction parent. Original model kept.' }


    $sizeOutput = @(& $python -c "import json,sys,torch; print(json.dumps([int(torch.load(p,map_location='cpu',weights_only=True)['imageSize']) for p in sys.argv[1:]]))" $base.Gate $base.Direction | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or $sizeOutput.Count -eq 0) { throw 'Could not read the selected model input sizes.' }
    # Assign directly: Windows PowerShell 5.1 otherwise wraps a JSON array as
    # one nested item when ConvertFrom-Json is inside an array expression.
    $sizes = ConvertFrom-Json -InputObject $sizeOutput[0].ToString()
    if ($sizes.Count -ne 2 -or @($sizes | Where-Object { $_ -lt 128 -or $_ -gt 320 }).Count -gt 0) {
        throw 'The selected model has unsupported input sizes.'
    }
    $caches = @{}
    Write-Host "TRAIN_STATUS phase=preparing"
    foreach ($size in ($sizes | Select-Object -Unique)) {
        $cache = Join-Path $PSScriptRoot ("training\{0}-personal-refinement-{1}px" -f [System.IO.Path]::GetFileNameWithoutExtension($capture), $size)
        # Resize once from the recorded camera image at each branch resolution.
        & $python .\prepare_tongue_stills.py $capture --session $latest.FullName --output $cache --size $size
        if ($LASTEXITCODE -ne 0) { throw "Preparing the $size px refinement frames failed." }
        $caches[[int]$size] = $cache
    }
    $gateCache = $caches[[int]$sizes[0]]
    $directionCache = $caches[[int]$sizes[1]]
    # A failed earlier run may have left an unpaired checkpoint or TorchScript
    # companion. Allocate beyond every existing tongue-model artifact so a
    # retry cannot replace any personal weights or metadata.
    $occupiedVersions = @(Get-ChildItem .\models -Filter 'qpro-stereo-tongue-v*' -File |
        ForEach-Object {
            if ($_.Name -match '^qpro-stereo-tongue-v(?<v>\d+)(?:[.-]|$)') { [int]$Matches.v }
        })
    $version = ((@($base.Version) + $occupiedVersions | Measure-Object -Maximum).Maximum) + 1
    $trainingRunRoot = Join-Path $PSScriptRoot ('training\lower-face-run-' + [guid]::NewGuid().ToString('N'))
    $stagedModels = Join-Path $trainingRunRoot 'models'
    New-Item -ItemType Directory -Path $stagedModels -Force | Out-Null
    $gateOutput = Join-Path $stagedModels "qpro-stereo-tongue-v$version-gate.pt"
    $directionOutput = Join-Path $stagedModels "qpro-stereo-tongue-v$version-direction.pt"

    $deviceOutput = @(& $python -c "import torch; from qpro_gpu import preferred_torch_device_name; print(preferred_torch_device_name(torch))" | Select-Object -Last 1)
    $deviceExitCode = $LASTEXITCODE
    if ($deviceExitCode -ne 0 -or $deviceOutput.Count -eq 0) {
        throw "Could not determine the PyTorch training device (Python exit code $deviceExitCode). Check the preceding Activity output, then run PC runtime setup again."
    }
    $device = $deviceOutput[0].ToString().Trim()
    if ($device -ne 'cpu' -and $device -notmatch '^cuda:\d+$') { throw "Unsupported PyTorch training device: $device" }
    $effectiveBatchSize = if ($device -eq "cpu") { [Math]::Min($BatchSize, 16) } else { $BatchSize }
    Write-Host "TRAIN_DEVICE device=$device batch=$effectiveBatchSize"
    if ($device -eq "cpu") { Write-Warning "CUDA is unavailable. CPU fallback is active; training can take substantially longer. NVIDIA users should rerun PC runtime setup after installing the current NVIDIA driver." }

    $totalStages = 2
    Write-Host "TRAIN_STAGE index=1 total=$totalStages name=visibility epochs=$Epochs device=$device"
    & $python .\train_tongue_model.py $gateCache --architecture spatial-stereo-resnet-v2 --checkpoint-focus visibility --initial-checkpoint $trainingGate --learning-rate 0.00005 --epochs $Epochs --batch-size $effectiveBatchSize --device $device --output $gateOutput
    if ($LASTEXITCODE -ne 0) { throw "Refining tongue visibility failed." }
    Write-Host "TRAIN_STAGE index=2 total=$totalStages name=direction epochs=$Epochs device=$device"
    & $python .\train_tongue_model.py $directionCache --architecture spatial-stereo-resnet-v2 --checkpoint-focus direction --initial-checkpoint $trainingDirection --learning-rate 0.00005 --epochs $Epochs --batch-size $effectiveBatchSize --device $device --output $directionOutput
    if ($LASTEXITCODE -ne 0) { throw "Refining tongue direction failed." }
    & $python .\lower_face_training.py publish --staged-root $trainingRunRoot --root $PSScriptRoot --version $version
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the trained tongue model failed. Original model kept.' }
    Write-Host "MODEL_READY version=$version parent=$($base.Version)"
}
finally {
    try {
        foreach ($temporaryRoot in @($parentTemporaryRoot, $trainingRunRoot)) {
            if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
                $resolvedTemporary = [System.IO.Path]::GetFullPath($temporaryRoot)
                $allowedTraining = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'training')).TrimEnd('\') + '\'
                if (-not $resolvedTemporary.StartsWith($allowedTraining, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary training path.' }
                Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
            }
        }
    } finally {
        Restore-QproGpuVisibility
        foreach ($name in $qproSavedGpuEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $qproSavedGpuEnvironment[$name], 'Process')
        }
        Pop-Location
    }
}
