param(
    [string]$SessionPath = "",
    [ValidateRange(1, 100)]
    [int]$Epochs = 24,
    [ValidateRange(8, 256)]
    [int]$BatchSize = 64
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $rocmPython = Join-Path $PSScriptRoot ".venv-rocm\Scripts\python.exe"
    $useRocm = Test-Path -LiteralPath $rocmPython
    $python = if ($useRocm) { $rocmPython } elseif (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) { $env:QPRO_PYTHON } else { Join-Path $PSScriptRoot ".venv\Scripts\python.exe" }
    if ($useRocm) {
        & $python -c "import torch; assert torch.version.hip and torch.cuda.is_available(), 'AMD ROCm GPU is unavailable'; print('Tongue training GPU:', torch.cuda.get_device_name(0))"
        if ($LASTEXITCODE -ne 0) { throw "The local ROCm runtime exists, but the AMD GPU is unavailable. Training was not started on CPU." }
    }
    Write-Host "PC Python runtime: $python"
    $pythonFallback = Join-Path $PSScriptRoot ".venv\Scripts\qpro-python-console.exe"
    if (-not (Test-Path -LiteralPath $python) -and (Test-Path -LiteralPath $pythonFallback)) { $python = $pythonFallback }
    if (-not (Test-Path -LiteralPath $python)) { throw "Set up the PC runtime first." }

    $latest = if (-not [string]::IsNullOrWhiteSpace($SessionPath)) {
        Get-Item -LiteralPath ([System.IO.Path]::GetFullPath($SessionPath)) -ErrorAction Stop
    } else {
        Get-ChildItem .\captures -Filter "*.qpsession.json" -File |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                try {
                    $session = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                    if ($session.sessionType -in @("tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3") -and $session.completed) { $_ }
                } catch {}
            } | Select-Object -First 1
    }
    if ($null -eq $latest) { throw "No completed quick-refinement capture was found." }
    $sessionMetadata = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    if ($sessionMetadata.sessionType -notin @("tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3") -or -not $sessionMetadata.completed) {
        throw "The selected dataset is not a completed quick-refinement capture."
    }
    $capture = $latest.FullName -replace '\.qpsession\.json$', '.qpcap'
    if (-not (Test-Path -LiteralPath $capture)) { throw "Matching capture is missing: $capture" }
    $cache = Join-Path $PSScriptRoot ("training\{0}-personal-refinement-224px" -f [System.IO.Path]::GetFileNameWithoutExtension($capture))
    Write-Host "TRAIN_STATUS phase=preparing"
    & $python .\prepare_tongue_stills.py $capture --session $latest.FullName --output $cache --size 224
    if ($LASTEXITCODE -ne 0) { throw "Preparing the refinement frames failed." }

    $pairs = @(Get-ChildItem .\models -Filter "qpro-stereo-tongue-v*-gate.pt" -File | ForEach-Object {
        if ($_.Name -match '^qpro-stereo-tongue-v(?<v>\d+)-gate\.pt$') {
            $v = [int]$Matches.v
            $direction = Join-Path $_.DirectoryName "qpro-stereo-tongue-v$v-direction.pt"
            if (Test-Path -LiteralPath $direction) { [pscustomobject]@{ Version=$v; Gate=$_.FullName; Direction=$direction } }
        }
    } | Sort-Object Version -Descending)
    if (-not $pairs.Count) { throw "No paired base tongue model was found." }
    $base = $pairs[0]
    $version = $base.Version + 1
    $gateOutput = ".\models\qpro-stereo-tongue-v$version-gate.pt"
    $directionOutput = ".\models\qpro-stereo-tongue-v$version-direction.pt"

    $device = @(& $python -c "import torch; print('cuda:0' if torch.cuda.is_available() else 'cpu')" | Select-Object -Last 1)[0].Trim()
    if ($LASTEXITCODE -ne 0 -or $device -notin @("cuda:0", "cpu")) { throw "Could not determine the PyTorch training device. Run PC runtime setup again." }
    $effectiveBatchSize = if ($device -eq "cpu") { [Math]::Min($BatchSize, 16) } else { $BatchSize }
    Write-Host "TRAIN_DEVICE device=$device batch=$effectiveBatchSize"
    if ($device -eq "cpu") { Write-Warning "CUDA is unavailable. CPU fallback is active; training can take substantially longer. NVIDIA users should rerun PC runtime setup after installing the current NVIDIA driver." }

    Write-Host "TRAIN_STAGE index=1 total=2 name=visibility epochs=$Epochs device=$device"
    & $python .\train_tongue_model.py $cache --architecture spatial-stereo-resnet-v2 --checkpoint-focus visibility --initial-checkpoint $base.Gate --learning-rate 0.00005 --epochs $Epochs --batch-size $effectiveBatchSize --device $device --output $gateOutput
    if ($LASTEXITCODE -ne 0) { throw "Refining tongue visibility failed." }
    Write-Host "TRAIN_STAGE index=2 total=2 name=direction epochs=$Epochs device=$device"
    & $python .\train_tongue_model.py $cache --architecture spatial-stereo-resnet-v2 --checkpoint-focus direction --initial-checkpoint $base.Direction --learning-rate 0.00005 --epochs $Epochs --batch-size $effectiveBatchSize --device $device --output $directionOutput
    if ($LASTEXITCODE -ne 0) { throw "Refining tongue direction failed." }
    Write-Host "MODEL_READY version=$version parent=$($base.Version)"
}
finally {
    Pop-Location
}
