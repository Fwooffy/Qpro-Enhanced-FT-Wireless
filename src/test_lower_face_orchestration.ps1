# Execute the production training helpers against private script fixtures.
# The mock Python records commands and writes tiny files; no ML, install or ADB.
$ErrorActionPreference = 'Stop'
$fixtureBase = Join-Path $PSScriptRoot ('artifacts\lower-face-orchestration-' + [guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
$environmentNames = @('HIP_VISIBLE_DEVICES','CUDA_VISIBLE_DEVICES','ROCR_VISIBLE_DEVICES',
    'GPU_DEVICE_ORDINAL','ROCM_SDK_TARGET_FAMILY','QPRO_ROCM_INSTALL_SMOKE_TEST',
    'QPRO_ROCM_EXPECTED_GFX_TARGET','QPRO_PYTHON','QPRO_TEST_CALLS','QPRO_TEST_GPU_FAIL','QPRO_TEST_TRAIN_FAIL')
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
$checks = 0
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
$mockPython = @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
$global:LASTEXITCODE = 0
Add-Content -LiteralPath $env:QPRO_TEST_CALLS -Value (ConvertTo-Json -InputObject @($Arguments) -Compress)
function ArgumentAfter([string]$flag) { return $Arguments[[Array]::IndexOf($Arguments, $flag) + 1] }
if ($Arguments[0] -eq '-c') {
    if ($Arguments[1] -match 'require_rocm_device_name') {
        if ($env:QPRO_TEST_GPU_FAIL -eq '1') { $global:LASTEXITCODE = 1; return }
        Write-Output 'Tongue training GPU: cuda:1'; return
    }
    if ($Arguments[1] -match 'print\(json.dumps') { Write-Output '[224,224]'; return }
    if ($Arguments[1] -match 'preferred_torch_device_name') { Write-Output 'cpu'; return }
    return
}
$program = [System.IO.Path]::GetFileName($Arguments[0])
if ($program -eq 'lower_face_training.py' -and $Arguments[1] -eq 'unwrap') {
    $output = ArgumentAfter '--output'
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($output)) -Force | Out-Null
    [System.IO.File]::WriteAllText($output, 'unwrapped parent'); return
}
if ($program -eq 'prepare_tongue_stills.py') { return }
if ($program -eq 'train_tongue_model.py') {
    if ($env:QPRO_TEST_TRAIN_FAIL -eq '1') { $global:LASTEXITCODE = 1; return }
    [System.IO.File]::WriteAllText((ArgumentAfter '--output'), 'new tongue weights'); return
}
if ($program -eq 'lower_face_training.py' -and $Arguments[1] -eq 'publish') {
    $root = ArgumentAfter '--root'
    $staged = ArgumentAfter '--staged-root'
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $staged 'models') -File) {
        $target = Join-Path $root ('models\' + $source.Name)
        if (Test-Path -LiteralPath $target) { throw 'Would replace an existing model.' }
        Copy-Item -LiteralPath $source.FullName -Destination $target
    }
    return
}
throw ('Unexpected mock command: ' + ($Arguments -join ' '))
'@
try {
    foreach ($case in @(
        @{Name='full-combined'; Script='train-latest-tongue-stills.ps1'; Type='lower-face-stills-v1'; Fail=$false; GpuFail=$false},
        @{Name='quick-combined-cpu'; Script='train-latest-tongue-refinement.ps1'; Type='lower-face-refinement-v1'; Fail=$false; GpuFail=$true},
        @{Name='full-rollback'; Script='train-latest-tongue-stills.ps1'; Type='lower-face-stills-v1'; Fail=$true; GpuFail=$false},
        @{Name='full-tongue'; Script='train-latest-tongue-stills.ps1'; Type='tongue-stereo-stills-v1'; Fail=$false; GpuFail=$false},
        @{Name='quick-legacy'; Script='train-latest-tongue-refinement.ps1'; Type='tongue-stereo-refinement-v2'; Fail=$false; GpuFail=$true},
        @{Name='quick-developer'; Script='train-latest-tongue-refinement.ps1'; Type='tongue-stereo-refinement-v2'; Fail=$false; GpuFail=$true; Kind='developer-tongue'},
        @{Name='quick-mustachio'; Script='train-latest-tongue-refinement.ps1'; Type='tongue-stereo-refinement-v2'; Fail=$true; GpuFail=$true; Kind='mustachio-experimental'}
    )) {
        $root = Join-Path $fixtureBase $case.Name
        New-Item -ItemType Directory -Path (Join-Path $root 'models') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $root 'captures') -Force | Out-Null
        $python = Join-Path $root 'mock-python.ps1'
        Set-Content -LiteralPath $python -Value $mockPython
        Set-Content -LiteralPath (Join-Path $root 'ready') -Value 'ready'
        @'
function Get-QproRocmCandidates([string]$Root) {
    [pscustomobject]@{Python=(Join-Path $Root 'mock-python.ps1');ReadyMarker=(Join-Path $Root 'ready');TargetFamily='fixture-gfx';Name='fixture ROCm'}
}
'@ | Set-Content -LiteralPath (Join-Path $root 'runtime-python.ps1')
        $script = Join-Path $root $case.Script
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $case.Script) -Destination $script
        $session = Join-Path $root 'captures\fixture.qpsession.json'
        @{sessionType=$case.Type; completed=$true} | ConvertTo-Json | Set-Content -LiteralPath $session
        Set-Content -LiteralPath ($session -replace '\.qpsession\.json$', '.qpcap') -Value 'private fixture'
        foreach ($branch in @('gate','direction')) { Set-Content -LiteralPath (Join-Path $root "models\qpro-stereo-tongue-v1-$branch.pt") -Value 'keep original' }
        if ($case.Kind) {
            @{isExperimental=$true; modelKind=$case.Kind; origin='bundled developer model';
                bundledDeveloperModel=($case.Kind -eq 'developer-tongue')} | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $root 'models\qpro-stereo-tongue-v1.metadata.json')
        }
        foreach ($name in $environmentNames[0..6]) { [Environment]::SetEnvironmentVariable($name,('saved-' + $name),'Process') }
        $env:QPRO_PYTHON = $python
        $env:QPRO_TEST_CALLS = Join-Path $root 'calls.jsonl'
        $env:QPRO_TEST_GPU_FAIL = if ($case.GpuFail) { '1' } else { '0' }
        $env:QPRO_TEST_TRAIN_FAIL = if ($case.Fail) { '1' } else { '0' }
        $failed = $false
        try {
            $output = @(& $script -SessionPath $session -Epochs 1 6>&1)
        } catch { $failed = $true }
        Check ($failed -eq $case.Fail) ($case.Name + ': unexpected success/failure')
        foreach ($name in $environmentNames[0..6]) {
            Check ([Environment]::GetEnvironmentVariable($name,'Process') -eq ('saved-' + $name)) ($case.Name + ': environment not restored: ' + $name)
        }
        foreach ($branch in @('gate','direction')) {
            Check ((Get-Content -LiteralPath (Join-Path $root "models\qpro-stereo-tongue-v1-$branch.pt") -Raw).Trim() -eq 'keep original') ($case.Name + ': original weights changed')
        }
        $commands = @(Get-Content -LiteralPath $env:QPRO_TEST_CALLS | ForEach-Object { ConvertFrom-Json -InputObject $_ })
        $attach = @($commands | Where-Object { $_ -contains 'attach' })
        $publish = @($commands | Where-Object { $_ -contains 'publish' })
        Check ($attach.Count -eq 0) ($case.Name + ': removed cheek trainer was called')
        $train = @($commands | Where-Object { $_[0] -eq '.\train_tongue_model.py' })
        if ($case.Fail) {
            Check ($publish.Count -eq 0) ($case.Name + ': failed model was published')
            Check (@(Get-ChildItem -LiteralPath (Join-Path $root 'models') -Filter 'qpro-stereo-tongue-v2*').Count -eq 0) ($case.Name + ': failed model left visible files')
        } else {
            Check ($train.Count -eq 2) ($case.Name + ': expected visibility and direction stages')
            Check ((@($train | Where-Object { $_ -contains '--initial-checkpoint' }).Count -eq 2) -eq ($case.Script -eq 'train-latest-tongue-refinement.ps1')) ($case.Name + ': selected-parent versus scratch training changed')
            Check ($publish.Count -eq 1) ($case.Name + ': complete model was not published')
            Check (($output -join "`n") -match 'MODEL_READY version=2') ($case.Name + ': no final readiness message')
        }
        if (Test-Path -LiteralPath (Join-Path $root 'training')) {
            Check (@(Get-ChildItem -LiteralPath (Join-Path $root 'training') -Directory).Count -eq 0) ($case.Name + ': private run directory not cleaned')
        }
    }
    Write-Output "$checks lower-face orchestration checks passed."
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    if (Test-Path -LiteralPath $fixtureBase) {
        $absoluteFixture = [System.IO.Path]::GetFullPath($fixtureBase)
        $allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')).TrimEnd('\') + '\'
        if (-not $absoluteFixture.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
        Remove-Item -LiteralPath $absoluteFixture -Recurse -Force
    }
}
