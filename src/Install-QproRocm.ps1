param(
    [string]$QproRoot = $PSScriptRoot,
    [switch]$UseLegacyRocm,
    [switch]$Update,
    [switch]$AllowExperimentalWindows10,
    [string]$RocmStorageRoot = ''
)

$ErrorActionPreference = 'Stop'
$qproPrivatePython = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\python-3.12.10\python.exe'
$qproSharedRuntime = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime'
$qproRequirements = Join-Path $QproRoot 'requirements-runtime.txt'
$qproGateModel = Join-Path $QproRoot 'models\qpro-stereo-tongue-v8-gate.pt'
$qproDirectionModel = Join-Path $QproRoot 'models\qpro-stereo-tongue-v8-direction.pt'
$qproStableRadeonNames = @(
    'Radeon RX 9070 XT', 'Radeon RX 9070', 'Radeon AI PRO R9700',
    'Radeon RX 9060 XT', 'Radeon RX 7900 XTX', 'Radeon PRO W7900',
    'Radeon PRO W7900 Dual Slot', 'Radeon RX 7700'
)
$qproRocm10Targets = @{
    'Radeon RX 6950 XT' = 'gfx1030'
    'Radeon RX 6900 XT' = 'gfx1030'
    'Radeon RX 6800 XT' = 'gfx1030'
    'Radeon RX 6800' = 'gfx1030'
    'Radeon RX 6750 XT' = 'gfx1031'
    'Radeon RX 6700 XT' = 'gfx1031'
    'Radeon RX 6700' = 'gfx1031'
    'Radeon RX 6650 XT' = 'gfx1032'
    'Radeon RX 6600 XT' = 'gfx1032'
    'Radeon RX 6600' = 'gfx1032'
    'Radeon RX 7900 XT' = 'gfx1100'
    'Radeon RX 7900 XTX' = 'gfx1100'
    'Radeon RX 7900 GRE' = 'gfx1100'
    'Radeon PRO W7900' = 'gfx1100'
    'Radeon PRO W7900 Dual Slot' = 'gfx1100'
    'Radeon RX 7800 XT' = 'gfx1101'
    'Radeon RX 7700 XT' = 'gfx1101'
    'Radeon RX 7700' = 'gfx1101'
    'Radeon RX 7600 XT' = 'gfx1102'
    'Radeon RX 7600' = 'gfx1102'
    'Radeon RX 9070 XT' = 'gfx1201'
    'Radeon RX 9070' = 'gfx1201'
    'Radeon RX 9070 GRE' = 'gfx1201'
    'Radeon AI PRO R9700' = 'gfx1201'
    'Radeon RX 9060 XT' = 'gfx1200'
    'Radeon RX 9060' = 'gfx1200'
}

function Test-QproRocmWindowsPolicy([int]$Build, [bool]$AllowWindows10, [bool]$Legacy) {
    return ($Build -ge 22000 -or (-not $Legacy -and $Build -ge 19045 -and $Build -lt 22000 -and $AllowWindows10))
}
$qproWindowsBuild = [Environment]::OSVersion.Version.Build
$qproWindows10Experiment = $qproWindowsBuild -ge 19045 -and $qproWindowsBuild -lt 22000
if (-not (Test-QproRocmWindowsPolicy $qproWindowsBuild ([bool]$AllowExperimentalWindows10) ([bool]$UseLegacyRocm))) {
    if ($qproWindows10Experiment -and -not $UseLegacyRocm) {
        throw 'Windows 10 ROCm support is experimental and disabled by default. On Windows 10 22H2, enable Try ROCm on Windows 10 in the Hub, or retry this script with -AllowExperimentalWindows10. GPU training and inference checks still must pass.'
    }
    throw 'ROCm setup needs Windows 11, or Windows 10 22H2 (build 19045 or later) with -AllowExperimentalWindows10 for ROCm 10.1. Legacy ROCm 7.2.1 remains Windows 11 only. Use the PC runtime on older Windows versions.'
}
if ($qproWindows10Experiment) {
    Write-Warning "Experimental Windows 10 ROCm setup explicitly enabled (build $qproWindowsBuild). AMD validates Windows 11; this card/driver combination may fail. Qpro will not enable this environment unless every GPU check passes."
}

try {
    $qproVideoControllers = @(Get-CimInstance Win32_VideoController -ErrorAction Stop)
} catch {
    throw "Could not verify an AMD GPU before installing ROCm. Check Windows Device Manager and retry. $($_.Exception.Message)"
}
function Get-QproNormalizedGpuName($gpu) {
    $name = ([regex]::Replace([regex]::Replace($gpu.Name, '(?i)\((?:TM|R)\)|[\u2122\u00ae]', ' '), '\s+', ' ')).Trim()
    $name = $name -replace '(?i)^AMD\s+', ''
    $rxMatch = [regex]::Match($name, '^(?:Radeon\s*)?RX\s*(\d{4})\s*(XTX|XT|GRE)?$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($rxMatch.Success) {
        return ('Radeon RX ' + $rxMatch.Groups[1].Value + ' ' + $rxMatch.Groups[2].Value.ToUpperInvariant()).Trim()
    }
    return $name
}

function Get-QproRocmRecipe([string]$Root, [bool]$Legacy) {
    $manifest = Join-Path $Root 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { return $null }
    $record = [System.IO.File]::ReadAllText($manifest) | ConvertFrom-Json
    $components = $record.componentUpdates
    if ($null -eq $components -or $components.schema -ne 1) { return $null }
    $recipe = if ($Legacy) { [string]$components.legacyRocmRecipe } else { [string]$components.rocmRecipe }
    if ($recipe -notmatch '^[a-fA-F0-9]{64}$') { return $null }
    return $recipe.ToLowerInvariant()
}

function Test-QproRocmReplacement([string]$EnvironmentRoot, [string]$Version, [switch]$Update) {
    if ($Update) { return $true }
    $marker = Join-Path $EnvironmentRoot 'qpro-rocm-ready.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    try {
        $record = [System.IO.File]::ReadAllText($marker) | ConvertFrom-Json
        return $record.schema -eq 1 -and [string]$record.rocmVersion -ne $Version
    } catch { return $false }
}

function Get-QproRocm10Packages([string]$GfxTarget) {
    # Request the concrete packages as well as the host wheels. An incomplete
    # installed torch METADATA can silently omit extras and make pip report
    # "already satisfied" while the target kernels are missing.
    $packages = @(
        'torch==2.14.0+rocm10.1.0',
        'torchvision==0.29.0a0+rocm10.1.0',
        'torchaudio==2.11.0.3+rocm10.1.0',
        "amd-torch-device-$GfxTarget==2.14.0+rocm10.1.0",
        "amd-torchvision-device-$GfxTarget==0.29.0a0+rocm10.1.0",
        'rocm==10.1.0',
        'rocm-sdk-core==10.1.0',
        'rocm-sdk-libraries==10.1.0',
        "rocm-sdk-device-$GfxTarget==10.1.0"
    )
    # These family packs are additional dependencies of AMD's Windows Torch
    # device extras, alongside the exact target's device pack.
    if ($GfxTarget -in @('gfx1100', 'gfx1101', 'gfx1102', 'gfx1103')) {
        $packages += 'amd-torch-device-gfx110x==2.14.0+rocm10.1.0'
    } elseif ($GfxTarget -in @('gfx1200', 'gfx1201')) {
        $packages += 'amd-torch-device-gfx12-0==2.14.0+rocm10.1.0'
    }
    return $packages
}

function Get-QproRocmPackageProbeCode {
    return @'
import base64, importlib, importlib.metadata as metadata, json, sys, traceback
packages = json.loads(base64.b64decode(sys.argv[1]))
target = sys.argv[2]
experimental = sys.argv[3] == '1'
repair_expected = len(sys.argv) > 4 and sys.argv[4] == 'repair'

def report_probe_error(context, error):
    if repair_expected:
        # First-time setup expects missing packages. Keep the exact reason,
        # but do not present a pre-install check as a final installation error.
        print(f'WARNING: Existing ROCm packages need installation or repair: {context} {type(error).__name__}: {error}. Setup will continue.', flush=True)
    else:
        print(context, flush=True)
        traceback.print_exc()

try:
    for spec in packages:
        name, expected = spec.split('==', 1)
        actual = metadata.version(name)
        if actual != expected:
            raise RuntimeError(f'{name}: expected {expected}, found {actual}')
except Exception as error:
    report_probe_error('AMD package set is incomplete or has a different version.', error)
    sys.exit(11)
if experimental:
    extras = metadata.metadata('torch').get_all('Provides-Extra') or []
    if f'device-{target}' not in extras:
        message = f'Installed torch metadata is incomplete: missing device-{target}.'
        print(f'WARNING: {message} Setup will repair this package.' if repair_expected else message, flush=True)
        sys.exit(12)
try:
    torch = importlib.import_module('torch')
    if experimental:
        # torchgen ships inside AMD's torch wheel; it is not a separate package
        # to download from PyPI or copy from another environment.
        for name in ('torchgen', 'torchvision', 'torchaudio'):
            importlib.import_module(name)
except Exception as error:
    report_probe_error('AMD PyTorch import failed before GPU detection.', error)
    sys.exit(12)
try:
    import cv2, numpy
    from qpro_gpu import is_rocm_10_torch_build, is_rocm_721_torch_build
except Exception as error:
    report_probe_error('Qpro runtime dependency import failed before GPU detection.', error)
    sys.exit(13)
build_matches = is_rocm_10_torch_build(torch) if experimental else is_rocm_721_torch_build(torch)
if not build_matches:
    prefix = 'WARNING: Existing PyTorch build will be replaced:' if repair_expected else 'Unexpected AMD PyTorch build:'
    print(prefix, torch.__version__, getattr(torch.version, 'rocm', None), torch.version.hip, flush=True)
    sys.exit(11)
print('AMD packages and Python imports verified.', flush=True)
'@
}

function Invoke-QproRocmPythonProbe([string]$Python, [string]$Code, [string[]]$ProbeArguments = @()) {
    $savedPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 can promote expected Python tracebacks to
        # NativeCommandError. Capture them while preserving the exit status.
        $ErrorActionPreference = 'Continue'
        $probeOutput = @(& $Python -c $Code @ProbeArguments 2>&1)
        $probeExitCode = $LASTEXITCODE
        return [PSCustomObject]@{ ExitCode = $probeExitCode; Output = @($probeOutput | ForEach-Object { $_.ToString() }) }
    } finally {
        $ErrorActionPreference = $savedPreference
    }
}

function Assert-QproRocmPythonProbe($Probe, [string]$FailureMessage) {
    foreach ($line in $Probe.Output) { Write-Host $line }
    if ($Probe.ExitCode -ne 0) { throw $FailureMessage }
}

function Get-QproRocmPipFailure([string]$Output, [string]$FailureMessage, [string]$EnvironmentRoot) {
    if ($Output -match '(?i)Windows Long Path|long[ -]?path support|filename or extension is too long|WinError\s*206|Errno\s*36|File name too long') {
        return "$FailureMessage Windows could not create a long ROCm library path in $EnvironmentRoot. Retry using a shorter Qpro-only folder with -RocmStorageRoot C:\QproRocm (choose a folder you can write to). Existing environments were kept. This is a path-length failure, not an unsupported-GPU result."
    }
    if ($Output -match '(?i)No matching distribution found|Could not find a version that satisfies the requirement') {
        return "$FailureMessage The package index did not provide the requested wheel. See the exact package and index error above; this does not establish that your GPU is unsupported."
    }
    return "$FailureMessage See the pip error above for the download, dependency or filesystem failure. GPU compatibility has not yet been checked."
}

function Invoke-QproRocmPip([string]$Python, [string[]]$PipArguments, [string]$FailureMessage, [string]$EnvironmentRoot = '') {
    $savedPreference = $ErrorActionPreference
    $output = New-Object System.Collections.Generic.List[string]
    try {
        # Keep progress visible while retaining stderr to classify the actual
        # failure. NativeCommandError must not hide pip's long-path hint.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $Python -I -m pip --isolated @PipArguments --no-user 2>&1 | ForEach-Object {
            $line = $_.ToString()
            Write-Host $line
            $output.Add($line)
            if ($output.Count -gt 400) { $output.RemoveAt(0) }
        }
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    if ($null -eq $exitCode -or $exitCode -ne 0) {
        throw (Get-QproRocmPipFailure ($output -join "`n") $FailureMessage $EnvironmentRoot)
    }
}

function Enter-QproRocmEnvironmentScope {
    # pip and Python settings from another application must not redirect this
    # Qpro-only install. The caller restores them even when setup fails.
    $saved = @{}
    $scopedNames = @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES',
        'GPU_DEVICE_ORDINAL', 'ROCM_SDK_TARGET_FAMILY', 'QPRO_ROCM_EXPECTED_GFX_TARGET', 'QPRO_ROCM_INSTALL_SMOKE_TEST')
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(PIP_|PYTHON)' -or $scopedNames -contains $_.Name })) {
        $saved[$item.Name] = $item.Value
        if ($item.Name -in @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')) {
            Write-Host "Ignoring inherited $($item.Name) during AMD setup so all adapters can be checked."
        }
        [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process')
    }
    $env:PIP_CONFIG_FILE = 'nul'
    $env:PYTHONNOUSERSITE = '1'
    return $saved
}

function Exit-QproRocmEnvironmentScope([hashtable]$Saved) {
    $scopedNames = @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES',
        'GPU_DEVICE_ORDINAL', 'ROCM_SDK_TARGET_FAMILY', 'QPRO_ROCM_EXPECTED_GFX_TARGET', 'QPRO_ROCM_INSTALL_SMOKE_TEST')
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(PIP_|PYTHON)' -or $scopedNames -contains $_.Name })) {
        [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process')
    }
    foreach ($name in $Saved.Keys) { [Environment]::SetEnvironmentVariable($name, $Saved[$name], 'Process') }
}

function Repair-QproRocm10HostWheels([string]$Python, [string[]]$Packages) {
    Write-Host 'Repairing damaged AMD PyTorch host wheels, including torchgen and package metadata...'
    # pip's "already satisfied" checks metadata, not files. Reinstall only
    # the host wheels once; keep the already-downloaded SDK/device packs.
    $environmentRoot = Split-Path -Parent (Split-Path -Parent $Python)
    Invoke-QproRocmPip $Python @('install', '--no-input', '--disable-pip-version-check', '--no-cache-dir', '--force-reinstall', '--no-deps', '--index-url', 'https://stable.repo.amd.com/rocm/whl-next/', 'torch==2.14.0+rocm10.1.0', 'torchvision==0.29.0a0+rocm10.1.0', 'torchaudio==2.11.0.3+rocm10.1.0') 'Repairing AMD PyTorch host wheels failed.' $environmentRoot
    # Restore dependencies that damaged host metadata may have skipped on
    # the first install, without reinstalling the large SDK/device wheels.
    Invoke-QproRocmPip $Python (@('install', '--no-input', '--disable-pip-version-check', '--no-cache-dir', '--index-url', 'https://stable.repo.amd.com/rocm/whl-next/') + $Packages) 'Installing the repaired AMD PyTorch dependencies failed.' $environmentRoot
}
$qproExperimental = -not $UseLegacyRocm
if ($qproExperimental) {
    $qproAmdGpu = $qproVideoControllers | Where-Object {
        $_.PNPDeviceID -match 'VEN_1002' -and $qproStableRadeonNames -contains (Get-QproNormalizedGpuName $_)
    } | Select-Object -First 1
    if ($null -eq $qproAmdGpu) {
        $qproAmdGpu = $qproVideoControllers | Where-Object {
            $_.PNPDeviceID -match 'VEN_1002' -and $qproRocm10Targets.ContainsKey((Get-QproNormalizedGpuName $_))
        } | Select-Object -First 1
    }
} else {
    $qproAmdGpu = $qproVideoControllers | Where-Object {
        $_.PNPDeviceID -match 'VEN_1002' -and $qproStableRadeonNames -contains (Get-QproNormalizedGpuName $_)
    } | Select-Object -First 1
}
if ($null -eq $qproAmdGpu) {
    $qproDetectedNames = ($qproVideoControllers | ForEach-Object { $_.Name }) -join ', '
    if ($UseLegacyRocm) {
        throw "No Radeon on AMD's ROCm 7.2.1 Windows card list was found ($qproDetectedNames). Retry without -UseLegacyRocm for the latest ROCm 10.1 device package."
    }
    throw "No eligible discrete Radeon GPU was found ($qproDetectedNames). Ryzen integrated graphics cannot run Qpro ROCm. Qpro supports selected RX 6000, 7000 and 9000 models with an exact ROCm device package; use Install runtime for NVIDIA CUDA or CPU."
}
$qproGfxTarget = $qproRocm10Targets[(Get-QproNormalizedGpuName $qproAmdGpu)]
$qproRocmVersion = if ($qproExperimental) { '10.1.0' } else { '7.2.1' }
. (Join-Path $QproRoot 'runtime-python.ps1')
$qproRocmPrefix = if ($qproExperimental) { '10' } else { '721' }
$qproRocmEnv = Get-QproRocmInstallEnvironment $qproRocmPrefix $qproGfxTarget $RocmStorageRoot
Assert-QproRocmPathBudget $qproRocmEnv
if ($qproExperimental) {
    Write-Host "Discrete AMD GPU detected: $($qproAmdGpu.Name) ($qproGfxTarget)."
    Write-Host 'Installing the latest stable ROCm 10.1 GPU-specific packages in a separate Qpro environment. Setup reports ready only after GPU training and inference checks pass.'
    if ((Get-QproNormalizedGpuName $qproAmdGpu) -match '^Radeon RX 6') {
        Write-Warning 'Radeon RX 6000 support on Windows is experimental for Qpro. AMD does not list these RX gaming cards in its ROCm 10.1 Windows compatibility matrix.'
    }
    if (-not $qproWindows10Experiment -and $qproWindowsBuild -lt 26200) {
        Write-Warning 'AMD validates ROCm 10.1 on Windows 11 25H2. This Windows build is outside that validation; the GPU checks may fail.'
    }
} else {
    Write-Host "Legacy ROCm 7.2.1 explicitly selected for supported discrete AMD GPU: $($qproAmdGpu.Name)"
}

$qproSetupEnvironment = Enter-QproRocmEnvironmentScope
try {
    $qproBasePython = if (Test-QproPython312 $qproPrivatePython) {
        $qproPrivatePython
    } elseif (Test-Path -LiteralPath (Join-Path $qproSharedRuntime 'runtime-ready.json')) {
        Get-QproVenvBasePython312 (Join-Path $qproSharedRuntime '.venv')
    } else { $null }
    if ([string]::IsNullOrWhiteSpace($qproBasePython)) {
        throw "Open QproFaceTracking.exe and complete PC runtime setup first. The AMD installer needs its Python 3.12 base, which may be an existing installation or Qpro's private copy."
    }
    if (-not (Test-Path -LiteralPath $qproRequirements)) {
        throw "The QproFaceTracking release was not found: $QproRoot"
    }
    $env:PYTHONPATH = $QproRoot
    # A parent's visibility mask can expose only Ryzen integrated graphics. Clear
    # it in this setup process so PyTorch can enumerate and select the actual
    # discrete card; Win32 adapter order is not a HIP device index.
    foreach ($qproMaskName in @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')) {
        if (Test-Path -LiteralPath "Env:$qproMaskName") {
            Write-Host "Ignoring inherited $qproMaskName during AMD setup so all adapters can be checked."
            Remove-Item -LiteralPath "Env:$qproMaskName"
        }
    }
    if ($qproExperimental) {
        Remove-Item Env:ROCM_SDK_TARGET_FAMILY -ErrorAction SilentlyContinue
        $env:QPRO_ROCM_EXPECTED_GFX_TARGET = $qproGfxTarget
        $env:QPRO_ROCM_INSTALL_SMOKE_TEST = '1'
    } else {
        # The pinned 7.2.1 wheels provide the "custom" target family. Selecting it
        # avoids their offload-arch trampoline on install paths containing spaces.
        $env:ROCM_SDK_TARGET_FAMILY = 'custom'
        Remove-Item Env:QPRO_ROCM_EXPECTED_GFX_TARGET -ErrorAction SilentlyContinue
        Remove-Item Env:QPRO_ROCM_INSTALL_SMOKE_TEST -ErrorAction SilentlyContinue
    }
    $qproSiteCustomize = Join-Path $qproRocmEnv 'Lib\site-packages\sitecustomize.py'
    $qproReplaceCustomizedEnvironment = Test-Path -LiteralPath $qproSiteCustomize
    $qproReplaceForUpdate = Test-QproRocmReplacement $qproRocmEnv $qproRocmVersion -Update:$Update
    if ($qproReplaceForUpdate) {
        Write-Host 'Preparing a separate ROCm update. The current environment stays available until the new GPU checks pass.'
    } elseif ($qproReplaceCustomizedEnvironment) {
        Write-Host 'Keeping a ROCm folder that contains an earlier GPU-discovery shim; a clean replacement will be created.'
    }

    & $qproBasePython -c 'import sys; assert sys.version_info[:2] == (3, 12), sys.version'
    if ($LASTEXITCODE -ne 0) { throw 'QproFaceTracking requires Python 3.12 for these AMD wheels.' }

    $qproRocmEnv = Initialize-QproRocmEnvironment $qproRocmEnv $qproBasePython -ForceReplacement:($qproReplaceCustomizedEnvironment -or $qproReplaceForUpdate)
    $qproRocmPython = Join-Path $qproRocmEnv 'Scripts\python.exe'
    $qproReadyMarker = Join-Path $qproRocmEnv 'qpro-rocm-ready.json'
    Write-Host "Separate Qpro ROCm environment: $qproRocmEnv"
    Write-Host 'Older extracted-release ROCm environments are kept as fallbacks; global Python and Windows path settings are unchanged.'
    # A failed installation must not leave a stale readiness claim behind.
    if (Test-Path -LiteralPath $qproReadyMarker) { Remove-Item -LiteralPath $qproReadyMarker -Force }

    $qproRocm10Packages = @(if ($qproExperimental) { Get-QproRocm10Packages $qproGfxTarget })
    $qproPackageProbeCode = Get-QproRocmPackageProbeCode
    # Windows PowerShell 5.1's native argument parser removes quotes inside JSON
    # and drops empty arguments. Use a quote-free encoding and a legacy sentinel.
    $qproEncodedPackageSpecs = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $qproRocm10Packages -Compress)))
    $qproPackageProbeArguments = @(
        $qproEncodedPackageSpecs,
        $(if ($qproExperimental) { $qproGfxTarget } else { '-' }),
        $(if ($qproExperimental) { '1' } else { '0' })
    )
    Write-Host 'Checking existing ROCm packages and Python imports...'
    $qproPackageProbe = Invoke-QproRocmPythonProbe $qproRocmPython $qproPackageProbeCode ($qproPackageProbeArguments + @('repair'))
    $qproRuntimeReady = $qproPackageProbe.ExitCode -eq 0
    if (-not $qproRuntimeReady) {
        foreach ($line in $qproPackageProbe.Output) { Write-Host $line }
        Write-Host 'Repairing the AMD package set in the separate Qpro environment.'
    }

    if (-not $qproRuntimeReady) {
        Invoke-QproRocmPip $qproRocmPython @('install', '--no-input', '--disable-pip-version-check', '--upgrade', 'pip') 'Updating pip in the ROCm environment failed.' $qproRocmEnv

        if ($qproExperimental) {
            # Exact device and family packages are required even when old torch
            # metadata would otherwise cause pip to silently skip its extras.
            Write-Host "Downloading latest stable ROCm 10.1 $qproGfxTarget PyTorch packages; this may take several gigabytes."
            Invoke-QproRocmPip $qproRocmPython (@('install', '--no-input', '--disable-pip-version-check', '--no-cache-dir', '--index-url', 'https://stable.repo.amd.com/rocm/whl-next/') + $qproRocm10Packages) "Installing AMD ROCm 10.1 $qproGfxTarget PyTorch packages failed." $qproRocmEnv
        } else {
            $qproAmdSdkPackages = @(
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl',
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl',
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl',
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz'
            )
            Invoke-QproRocmPip $qproRocmPython (@('install', '--no-input', '--disable-pip-version-check', '--no-cache-dir') + $qproAmdSdkPackages) 'Installing AMD ROCm 7.2.1 components failed.' $qproRocmEnv

            $qproAmdTorchPackages = @(
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl',
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl',
                'https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl'
            )
            Invoke-QproRocmPip $qproRocmPython (@('install', '--no-input', '--disable-pip-version-check', '--no-cache-dir') + $qproAmdTorchPackages) 'Installing AMD ROCm PyTorch 2.9.1 failed.' $qproRocmEnv
        }

        Invoke-QproRocmPip $qproRocmPython @('install', '--no-input', '--disable-pip-version-check', '-r', $qproRequirements) 'Installing QproFaceTracking Python requirements failed.' $qproRocmEnv

        $qproPackageProbe = Invoke-QproRocmPythonProbe $qproRocmPython $qproPackageProbeCode $qproPackageProbeArguments
        if ($qproExperimental -and $qproPackageProbe.ExitCode -eq 12) {
            foreach ($line in $qproPackageProbe.Output) { Write-Host $line }
            Repair-QproRocm10HostWheels $qproRocmPython $qproRocm10Packages
            $qproPackageProbe = Invoke-QproRocmPythonProbe $qproRocmPython $qproPackageProbeCode $qproPackageProbeArguments
        }
        Assert-QproRocmPythonProbe $qproPackageProbe 'AMD package/import verification failed before GPU detection. See the exact missing package, import or DLL error above. This does not mean the discrete GPU is unsupported.'
    }

    if ($qproRuntimeReady) {
        Write-Host 'Required ROCm packages are already installed. Running GPU training and inference checks.'
    }

    & $qproRocmPython -I -m pip --isolated check
    if ($LASTEXITCODE -ne 0) { throw 'AMD package dependency verification failed before GPU detection. See the missing or conflicting dependency above.' }

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

    $qproGpuProbe = Invoke-QproRocmPythonProbe $qproRocmPython "import torch; from qpro_gpu import require_rocm_device_name,rocm_device_diagnostics; print('PyTorch:',torch.__version__,flush=True); print('ROCm release:',getattr(torch.version,'rocm',None),flush=True); print('HIP build:',torch.version.hip,flush=True); print('GPU available:',torch.cuda.is_available(),flush=True); print('GPU devices:',rocm_device_diagnostics(torch),flush=True); device=require_rocm_device_name(torch); print('GPU:',torch.cuda.get_device_name(int(device.split(':')[1])),flush=True); print('Device:',device,flush=True)"
    Assert-QproRocmPythonProbe $qproGpuProbe 'AMD packages imported successfully, but the discrete GPU check failed. See the detected adapter, driver or device error above.'

    & $qproRocmPython -c "import sys,torch; from qpro_gpu import require_rocm_device_name; from train_tongue_model import create_model; device=require_rocm_device_name(torch); assert not torch.backends.cudnn.enabled; c=torch.load(sys.argv[1],map_location='cpu',weights_only=True); m=create_model(c['architecture'],list(c['targetNames'])).to(device); x=torch.rand(2,2,c['imageSize'],c['imageSize'],device=device); m(x).float().square().mean().backward(); torch.cuda.synchronize(device); print('GPU training smoke test passed on',device)" $qproGateModel
    if ($LASTEXITCODE -ne 0) { throw 'The Qpro tongue model failed a GPU training step.' }

    & $qproRocmPython -c "import sys,numpy as np,torch; from qpro_gpu import require_rocm_device_name; from tongue_model_preview import LiveTongueModelPreview; assert not torch.backends.cudnn.enabled; p=LiveTongueModelPreview(sys.argv[1],direction_checkpoint_path=sys.argv[2]); assert str(p.device)==require_rocm_device_name(torch); p.predict(np.zeros((400,800),dtype=np.uint8),None,[]); torch.cuda.synchronize(p.device); print('GPU inference smoke test passed on',p.device)" $qproGateModel $qproDirectionModel
    if ($LASTEXITCODE -ne 0) { throw 'The Qpro tongue model failed a GPU inference step.' }

    Remove-Item Env:QPRO_ROCM_INSTALL_SMOKE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:QPRO_ROCM_EXPECTED_GFX_TARGET -ErrorAction SilentlyContinue

    @{
        schema = 1
        rocmVersion = $qproRocmVersion
        supportTier = if ($qproExperimental) { 'experimental-rocm-10' } else { 'amd-windows-7.2.1' }
        gfxTarget = $qproGfxTarget
        windowsBuild = $qproWindowsBuild
        experimentalWindows10 = $qproWindows10Experiment
        python = $qproRocmPython
        gpu = $qproAmdGpu.Name
        verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $qproReadyMarker -Encoding UTF8
    try {
        & $qproRocmPython -c "import torch; from qpro_gpu import require_rocm_device_name; print('Final readiness check:', require_rocm_device_name(torch))"
        if ($LASTEXITCODE -ne 0) { throw 'The ROCm environment failed its final readiness check.' }
    } catch {
        Remove-Item -LiteralPath $qproReadyMarker -Force -ErrorAction SilentlyContinue
        throw
    }
    $qproRecipe = Get-QproRocmRecipe $QproRoot ([bool]$UseLegacyRocm)
    if ($qproRecipe) {
        # Record the release recipe only after the final GPU validation succeeds.
        $qproReceipt = [System.IO.File]::ReadAllText($qproReadyMarker) | ConvertFrom-Json
        $qproReceipt | Add-Member -NotePropertyName recipeSha256 -NotePropertyValue $qproRecipe -Force
        $qproReceipt | ConvertTo-Json | Set-Content -LiteralPath $qproReadyMarker -Encoding UTF8
    }
    Register-QproRocmEnvironment $qproRocmEnv ([bool]$UseLegacyRocm)
    Write-Host "ROCm runtime ready: $qproRocmPython"
    Write-Host 'Open QproFaceTracking.exe. Tongue tracking and training will select this ROCm runtime automatically.'
} finally {
    Exit-QproRocmEnvironmentScope $qproSetupEnvironment
}
