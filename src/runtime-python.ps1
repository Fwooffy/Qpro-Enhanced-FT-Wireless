param(
    [switch]$LaunchRocmHub,
    [string]$RocmLaunchRoot = $PSScriptRoot,
    [string]$RocmHubExecutable = ''
)

# Interpreter discovery and Qpro-owned environments. Never change global Python.
function Test-QproPython312([string]$Python) {
    if ([string]::IsNullOrWhiteSpace($Python) -or -not (Test-Path -LiteralPath $Python -PathType Leaf)) { return $false }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        # Windows PowerShell 5.1 strips embedded double quotes from native
        # arguments. Keep Python string literals single-quoted in -c code.
        $check = "import sys,venv,platform; assert sys.version_info[:2] == (3,12) and sys.maxsize > 2**32 and platform.machine().lower() in ('amd64','x86_64'); print('QPRO_PYTHON312_OK')"
        $output = @(& $Python -c $check 2>$null)
        return $LASTEXITCODE -eq 0 -and $output.Count -gt 0 -and $output[-1].ToString().Trim() -eq 'QPRO_PYTHON312_OK'
    }
    catch { return $false }
    finally { $ErrorActionPreference = $previousPreference }
}

function Find-QproExistingPython312 {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:QPRO_BASE_PYTHON)) {
        $candidates.Add($env:QPRO_BASE_PYTHON)
    }
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'))

    foreach ($registryPath in @(
        'Registry::HKEY_CURRENT_USER\Software\Python\PythonCore\3.12\InstallPath',
        'Registry::HKEY_LOCAL_MACHINE\Software\Python\PythonCore\3.12\InstallPath',
        'Registry::HKEY_LOCAL_MACHINE\Software\WOW6432Node\Python\PythonCore\3.12\InstallPath'
    )) {
        $key = Get-Item -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($null -eq $key) { continue }
        $executable = $key.GetValue('ExecutablePath')
        if (-not [string]::IsNullOrWhiteSpace($executable)) { $candidates.Add($executable) }
        $pythonInstallDir = $key.GetValue('')
        if (-not [string]::IsNullOrWhiteSpace($pythonInstallDir)) { $candidates.Add((Join-Path $pythonInstallDir 'python.exe')) }
    }

    $pathPython = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($null -ne $pathPython -and $pathPython.Source -notmatch '[\\/]WindowsApps[\\/]') {
        $candidates.Add($pathPython.Source)
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-QproPython312 $candidate) { return [System.IO.Path]::GetFullPath($candidate) }
    }

    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($null -eq $launcher) { return $null }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $launcher.Source -3.12 -c 'import sys,platform; assert sys.version_info[:2] == (3,12) and sys.maxsize > 2**32 and platform.machine().lower() in ("amd64","x86_64"); print(sys.executable)' 2>$null)
        if ($LASTEXITCODE -eq 0 -and $output.Count -gt 0) {
            $candidate = $output[-1].ToString().Trim()
            if (Test-QproPython312 $candidate) { return [System.IO.Path]::GetFullPath($candidate) }
        }
    }
    finally { $ErrorActionPreference = $previousPreference }
    return $null
}

function Get-QproVenvBasePython312([string]$VenvRoot) {
    $config = Join-Path $VenvRoot 'pyvenv.cfg'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { return $null }
    foreach ($line in [System.IO.File]::ReadAllLines($config)) {
        $match = [regex]::Match($line, '^home\s*=\s*(.+)$')
        if (-not $match.Success) { continue }
        $candidate = Join-Path $match.Groups[1].Value.Trim() 'python.exe'
        if (Test-QproPython312 $candidate) { return [System.IO.Path]::GetFullPath($candidate) }
    }
    return $null
}

function Test-QproPython312Registration {
    if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe')) { return $true }
    foreach ($registryPath in @(
        'Registry::HKEY_CURRENT_USER\Software\Python\PythonCore\3.12\InstallPath',
        'Registry::HKEY_LOCAL_MACHINE\Software\Python\PythonCore\3.12\InstallPath',
        'Registry::HKEY_LOCAL_MACHINE\Software\WOW6432Node\Python\PythonCore\3.12\InstallPath'
    )) {
        if (Test-Path -LiteralPath $registryPath) { return $true }
    }
    return $false
}

function Get-QproRocmStorageRoot([string]$StorageRoot = '', [string]$LocalAppData = $env:LOCALAPPDATA) {
    if ([string]::IsNullOrWhiteSpace($StorageRoot)) { $StorageRoot = $env:QPRO_ROCM_HOME }
    if ([string]::IsNullOrWhiteSpace($StorageRoot)) { $StorageRoot = Join-Path $LocalAppData 'QproFaceTracking\r' }
    if (-not [System.IO.Path]::IsPathRooted($StorageRoot) -or $StorageRoot.StartsWith('\\')) {
        throw 'ROCm storage must be an absolute path on a local Windows drive.'
    }
    return [System.IO.Path]::GetFullPath($StorageRoot).TrimEnd('\')
}

function Get-QproRocmRuntimeIndexPath([string]$LocalAppData = $env:LOCALAPPDATA) {
    return Join-Path $LocalAppData 'QproFaceTracking\r\rocm-runtimes.json'
}

function Read-QproRocmRuntimeIndex([string]$LocalAppData = $env:LOCALAPPDATA) {
    $path = Get-QproRocmRuntimeIndexPath $LocalAppData
    try {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -gt 16384) { return $null }
        $record = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json
        if ($record.schema -eq 1) { return $record }
    } catch { }
    return $null
}

function Get-QproRocmInstallEnvironment([string]$Prefix, [string]$GfxTarget, [string]$StorageRoot = '', [string]$LocalAppData = $env:LOCALAPPDATA) {
    $storage = Get-QproRocmStorageRoot $StorageRoot $LocalAppData
    # A successful custom installation or repaired slot should be reused when
    # Install is pressed again. Explicit storage selection takes precedence.
    if ([string]::IsNullOrWhiteSpace($StorageRoot) -and [string]::IsNullOrWhiteSpace($env:QPRO_ROCM_HOME)) {
        $index = Read-QproRocmRuntimeIndex $LocalAppData
        $field = if ($Prefix -eq '721') { 'legacyEnvironment' } else { 'latestEnvironment' }
        if ($null -ne $index -and $index.PSObject.Properties[$field]) {
            $path = [string]$index.$field
            if (-not [string]::IsNullOrWhiteSpace($path) -and [System.IO.Path]::IsPathRooted($path) -and
                -not $path.StartsWith('\\') -and (Split-Path -Leaf $path) -match "^$Prefix-$GfxTarget(-[0-9a-f]{8})?$") {
                return [System.IO.Path]::GetFullPath($path)
            }
        }
    }
    return Join-Path $storage "$Prefix-$GfxTarget"
}

function Assert-QproRocmPathBudget([string]$EnvironmentRoot) {
    # hipBLASLt installs long kernel names even before Python imports the GPU.
    # Reserve extra room beyond the longest name reported in the ROCm 10 wheel;
    # this check works without relying on Windows LongPathsEnabled or 8.3 names.
    $kernelPath = 'Lib\site-packages\_rocm_sdk_libraries\bin\hipblaslt\library\gfx1100\TensileLibrary_BB_BB_HA_Bias_Aux_SAV_UA_Type_BB_HPA_Contraction_l_Ailk_Bjlk_Cijk_Dijk_gfx1100.co'
    $requiredLength = (Join-Path $EnvironmentRoot $kernelPath).Length + 16
    if ($requiredLength -gt 259) {
        throw "ROCm's library paths would exceed the Windows path limit in $EnvironmentRoot (estimated $requiredLength characters). Choose a shorter Qpro-only location with Install-QproRocm.ps1 -RocmStorageRoot C:\QproRocm, using a folder you can write to. Existing environments were kept; no Windows settings were changed."
    }
}

function Initialize-QproRocmEnvironment([string]$EnvironmentRoot, [string]$BasePython, [switch]$ForceReplacement) {
    Assert-QproRocmPathBudget $EnvironmentRoot
    $python = Join-Path $EnvironmentRoot 'Scripts\python.exe'
    $owner = Join-Path $EnvironmentRoot 'qpro-rocm-environment.json'
    $owned = $false
    try {
        if (Test-Path -LiteralPath $owner -PathType Leaf) {
            $record = [System.IO.File]::ReadAllText($owner) | ConvertFrom-Json
            $owned = $record.format -eq 'qpro-rocm-environment-v1' -and
                [System.IO.Path]::GetFullPath($record.environment).Equals([System.IO.Path]::GetFullPath($EnvironmentRoot), [StringComparison]::OrdinalIgnoreCase)
        }
    } catch { }
    if (-not $ForceReplacement -and $owned -and (Test-QproPython312 $python)) { return $EnvironmentRoot }

    if (Test-Path -LiteralPath $EnvironmentRoot) {
        # A broken base or a foreign folder must not trigger a recursive delete.
        # Keep it for recovery and create an independently routed replacement.
        Write-Host "Keeping the existing ROCm folder: $EnvironmentRoot"
        $slotBase = $EnvironmentRoot -replace '-[0-9a-f]{8}$', ''
        do { $replacement = $slotBase + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) }
        while (Test-Path -LiteralPath $replacement)
        $EnvironmentRoot = $replacement
        Assert-QproRocmPathBudget $EnvironmentRoot
        $owner = Join-Path $EnvironmentRoot 'qpro-rocm-environment.json'
    }
    & $BasePython -m venv $EnvironmentRoot | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw 'Creating the separate ROCm Python environment failed. Existing environments were kept.' }
    @{ format = 'qpro-rocm-environment-v1'; environment = $EnvironmentRoot } |
        ConvertTo-Json | Set-Content -LiteralPath $owner -Encoding UTF8
    return $EnvironmentRoot
}

function Register-QproRocmEnvironment([string]$EnvironmentRoot, [bool]$Legacy, [string]$LocalAppData = $env:LOCALAPPDATA) {
    $path = Get-QproRocmRuntimeIndexPath $LocalAppData
    $previous = Read-QproRocmRuntimeIndex $LocalAppData
    $record = @{ schema = 1 }
    foreach ($field in @('latestEnvironment', 'legacyEnvironment')) {
        if ($null -ne $previous -and $previous.PSObject.Properties[$field] -and
            -not [string]::IsNullOrWhiteSpace([string]$previous.$field)) { $record[$field] = [string]$previous.$field }
    }
    $field = if ($Legacy) { 'legacyEnvironment' } else { 'latestEnvironment' }
    foreach ($tier in @('latest', 'legacy')) {
        $activeField = $tier + 'Environment'
        $fallbackField = $tier + 'FallbackEnvironments'
        $fallbacks = New-Object 'System.Collections.Generic.List[string]'
        if ($activeField -eq $field -and $record.ContainsKey($activeField) -and $record[$activeField] -ne $EnvironmentRoot) {
            $fallbacks.Add($record[$activeField])
        }
        if ($null -ne $previous -and $previous.PSObject.Properties[$fallbackField]) {
            foreach ($fallback in @($previous.$fallbackField)) {
                if ([string]::IsNullOrWhiteSpace([string]$fallback) -or $fallback -eq $EnvironmentRoot -or $fallbacks.Contains([string]$fallback)) { continue }
                if ($fallbacks.Count -lt 8) { $fallbacks.Add([string]$fallback) }
            }
        }
        if ($fallbacks.Count) { $record[$fallbackField] = @($fallbacks.ToArray()) }
    }
    $record[$field] = [System.IO.Path]::GetFullPath($EnvironmentRoot)
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $record | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $path -Force
    } finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function Get-QproRocmCandidates([string]$ReleaseRoot, [string]$LocalAppData = $env:LOCALAPPDATA) {
    $index = Read-QproRocmRuntimeIndex $LocalAppData
    $storage = Join-Path $LocalAppData 'QproFaceTracking\r'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($legacy in @($false, $true)) {
        $field = if ($legacy) { 'legacyEnvironment' } else { 'latestEnvironment' }
        $prefix = if ($legacy) { '721' } else { '10' }
        $paths = New-Object System.Collections.Generic.List[string]
        if ($null -ne $index -and $index.PSObject.Properties[$field]) { $paths.Add([string]$index.$field) }
        $fallbackField = if ($legacy) { 'legacyFallbackEnvironments' } else { 'latestFallbackEnvironments' }
        if ($null -ne $index -and $index.PSObject.Properties[$fallbackField]) {
            foreach ($fallback in @($index.$fallbackField) | Select-Object -First 8) { $paths.Add([string]$fallback) }
        }
        # The index keeps custom short storage and repaired slots discoverable.
        # Directory fallback also permits recovery from damaged index metadata.
        if (Test-Path -LiteralPath $storage -PathType Container) {
            Get-ChildItem -LiteralPath $storage -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match "^$prefix-gfx\d{4}(-[0-9a-f]{8})?$" } |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 32 |
                ForEach-Object { $paths.Add($_.FullName) }
        }
        $localName = if ($legacy) { '.venv-rocm' } else { '.venv-rocm-experimental' }
        $paths.Add((Join-Path $ReleaseRoot $localName))
        foreach ($path in $paths) {
            if ([string]::IsNullOrWhiteSpace($path) -or -not [System.IO.Path]::IsPathRooted($path) -or $path.StartsWith('\\')) { continue }
            try { $absolute = [System.IO.Path]::GetFullPath($path) } catch { continue }
            if (-not $seen.Add($absolute)) { continue }
            $version = if ($legacy) { '7.2.1' } else { '10' }
            try {
                $readyPath = Join-Path $absolute 'qpro-rocm-ready.json'
                if ((Test-Path -LiteralPath $readyPath -PathType Leaf) -and (Get-Item -LiteralPath $readyPath).Length -le 16384) {
                    $ready = [System.IO.File]::ReadAllText($readyPath) | ConvertFrom-Json
                    if ($ready.schema -eq 1 -and $ready.rocmVersion -in @('7.2.1', '10.0.0', '10.1.0')) { $version = [string]$ready.rocmVersion }
                }
            } catch { }
            [PSCustomObject]@{
                Name = if ($legacy) { 'ROCm 7.2.1 fallback' } else { "AMD ROCm $version" }
                EnvironmentRoot = $absolute
                Python = Join-Path $absolute 'Scripts\python.exe'
                ReadyMarker = Join-Path $absolute 'qpro-rocm-ready.json'
                TargetFamily = if ($legacy) { 'custom' } else { $null }
            }
        }
    }
}

function Start-QproRocmHub([string]$ReleaseRoot, [string]$Executable) {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'QproFaceTracking.exe is missing. Extract the full ZIP again.' }
    foreach ($name in @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL', 'QPRO_ROCM_INSTALL_SMOKE_TEST', 'QPRO_ROCM_EXPECTED_GFX_TARGET')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    Push-Location $ReleaseRoot
    try {
        foreach ($candidate in @(Get-QproRocmCandidates $ReleaseRoot)) {
            if (-not (Test-Path -LiteralPath $candidate.Python -PathType Leaf) -or
                -not (Test-Path -LiteralPath $candidate.ReadyMarker -PathType Leaf)) { continue }
            if ($candidate.TargetFamily) { $env:ROCM_SDK_TARGET_FAMILY = $candidate.TargetFamily }
            else { Remove-Item Env:ROCM_SDK_TARGET_FAMILY -ErrorAction SilentlyContinue }
            $savedPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $global:LASTEXITCODE = $null
                & $candidate.Python -c "import torch; from qpro_gpu import require_rocm_device_name; from train_tongue_model import create_model; d=require_rocm_device_name(torch); m=torch.nn.Sequential(torch.nn.Conv2d(2,8,3,padding=1),torch.nn.BatchNorm2d(8)).to(d); x=torch.rand(2,2,32,32,device=d); m(x).mean().backward(); torch.cuda.synchronize(device=d); print('AMD GPU ready:',torch.cuda.get_device_name(int(d.split(':')[1])), 'on', d)"
                if ($LASTEXITCODE -ne 0) { continue }
            } finally { $ErrorActionPreference = $savedPreference }
            $env:QPRO_PYTHON = $candidate.Python
            Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable)
            return
        }
        throw 'No validated AMD GPU runtime is available. Use Install AMD ROCm in the Hub first.'
    } finally { Pop-Location }
}

if ($LaunchRocmHub) {
    $ErrorActionPreference = 'Stop'
    Start-QproRocmHub $RocmLaunchRoot $RocmHubExecutable
}
