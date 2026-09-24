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
    if (-not (Test-Path -LiteralPath $python)) {
        throw "Python environment missing. Run build-and-run.ps1 once first."
    }
    $latest = if (-not [string]::IsNullOrWhiteSpace($SessionPath)) {
        Get-Item -LiteralPath ([System.IO.Path]::GetFullPath($SessionPath)) -ErrorAction Stop
    } else {
        Get-ChildItem -LiteralPath .\captures -Filter "*.qpsession.json" |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                try {
                    $session = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                    if ($session.sessionType -eq "tongue-stereo-stills-v1" -and $session.completed) {
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
    if ($sessionMetadata.sessionType -ne "tongue-stereo-stills-v1" -or -not $sessionMetadata.completed) {
        throw "The selected dataset is not a completed full tongue-still capture."
    }
    $capture = $latest.FullName -replace '\.qpsession\.json$', '.qpcap'
    if (-not (Test-Path -LiteralPath $capture)) {
        throw "Matching capture is missing: $capture"
    }
    $cache = Join-Path $PSScriptRoot ("training\{0}-tongue-stills-224px" -f [System.IO.Path]::GetFileNameWithoutExtension($capture))
    if ($Version -eq 0) {
        $existingVersions = @(Get-ChildItem .\models -Filter "qpro-stereo-tongue-v*-gate.pt" -File -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -match '^qpro-stereo-tongue-v(?<v>\d+)-gate\.pt$') { [int]$Matches.v }
        })
        $Version = if ($existingVersions.Count) { (($existingVersions | Measure-Object -Maximum).Maximum + 1) } else { 1 }
    }
    $gateOutput = ".\models\qpro-stereo-tongue-v$Version-gate.pt"
    $directionOutput = ".\models\qpro-stereo-tongue-v$Version-direction.pt"
    Write-Host "TRAIN_STATUS phase=preparing"
    Write-Host "Preparing exact manually selected stereo stills from $capture"
    & $python .\prepare_tongue_stills.py $capture --output $cache --size 224
    if ($LASTEXITCODE -ne 0) { throw "Preparing the manual still dataset failed." }
    $device = @(& $python -c "import torch; print('cuda:0' if torch.cuda.is_available() else 'cpu')" | Select-Object -Last 1)[0].Trim()
    if ($LASTEXITCODE -ne 0 -or $device -notin @("cuda:0", "cpu")) { throw "Could not determine the PyTorch training device. Run PC runtime setup again." }
    $effectiveBatchSize = if ($device -eq "cpu") { [Math]::Min($BatchSize, 16) } else { $BatchSize }
    Write-Host "TRAIN_DEVICE device=$device batch=$effectiveBatchSize"
    if ($device -eq "cpu") { Write-Warning "CUDA is unavailable. CPU fallback is active; full-dataset training may take hours. NVIDIA users should rerun PC runtime setup after installing the current NVIDIA driver." }
    Write-Host "TRAIN_STAGE index=1 total=2 name=visibility epochs=$Epochs device=$device"
    Write-Host "Training the visibility checkpoint on $device"
    & $python .\train_tongue_model.py $cache `
        --architecture spatial-stereo-resnet-v2 `
        --checkpoint-focus visibility `
        --epochs $Epochs `
        --batch-size $effectiveBatchSize `
        --device $device `
        --output $gateOutput
    if ($LASTEXITCODE -ne 0) { throw "Training the visibility checkpoint failed." }
    Write-Host "TRAIN_STAGE index=2 total=2 name=direction epochs=$Epochs device=$device"
    Write-Host "Training the direction checkpoint on $device"
    & $python .\train_tongue_model.py $cache `
        --architecture spatial-stereo-resnet-v2 `
        --checkpoint-focus direction `
        --epochs $Epochs `
        --batch-size $effectiveBatchSize `
        --device $device `
        --output $directionOutput
    if ($LASTEXITCODE -ne 0) { throw "Training the direction checkpoint failed." }
    Write-Host "MODEL_READY version=$Version gate=$((Resolve-Path -LiteralPath $gateOutput).Path) direction=$((Resolve-Path -LiteralPath $directionOutput).Path)"
} finally {
    Pop-Location
}
