param([switch]$Update)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$sharedRoot = Join-Path $env:LOCALAPPDATA "QproFaceTracking\runtime"
$venvRoot = Join-Path $sharedRoot ".venv"
$venvPython = Join-Path $venvRoot "Scripts\python.exe"
$privatePythonRoot = Join-Path $sharedRoot "python-3.12.10"
$privatePython = Join-Path $privatePythonRoot "python.exe"
$bundledPythonArchive = Join-Path $root "python-runtime\python.3.12.10.nupkg"
$bundledPythonSha256 = "0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8"
$readyMarker = Join-Path $sharedRoot "runtime-ready.json"
$requirements = Join-Path $root "requirements-runtime.txt"
$torchRequirement = "torch>=2.7,<3"
$cudaIndex = "https://download.pytorch.org/whl/cu128"
$cpuIndex = "https://download.pytorch.org/whl/cpu"
Write-Host 'PC runtime setup script started. Checking bundled files and Windows environment...'
. (Join-Path $root 'runtime-python.ps1')

function Assert-QproManagedRuntimePath([string]$Candidate, [string]$ExpectedName) {
    $managedRoot = [System.IO.Path]::GetFullPath($sharedRoot).TrimEnd('\')
    $expected = [System.IO.Path]::Combine($managedRoot, $ExpectedName)
    if (-not [System.IO.Path]::GetFullPath($Candidate).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside Qpro's managed runtime: $Candidate"
    }
}

function Install-QproPrivatePython {
    if (-not (Test-Path -LiteralPath $bundledPythonArchive -PathType Leaf)) {
        throw 'The bundled private Python archive is missing. Re-extract the complete release ZIP.'
    }
    Write-Host 'Checking the bundled Python archive before extraction...'
    $actualHash = (Get-FileHash -LiteralPath $bundledPythonArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $bundledPythonSha256) {
        throw 'The bundled private Python archive failed its integrity check. Re-extract or download the release again.'
    }

    # CPython's NuGet archive contains an unregistered Python tree under tools/.
    # Extracting it avoids the Windows installer's Modify mode entirely.
    $stagingRoot = Join-Path $sharedRoot ('python-stage-' + [guid]::NewGuid().ToString('N'))
    Write-Host "Preparing Qpro's private Python 3.12 runtime. Existing Python installations are untouched."
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        Write-Host 'Extracting private Python. This may take longer while Windows Security scans the files...'
        [System.IO.Compression.ZipFile]::ExtractToDirectory($bundledPythonArchive, $stagingRoot)
        $stagedPythonRoot = Join-Path $stagingRoot 'tools'
        $stagedPython = Join-Path $stagedPythonRoot 'python.exe'
        if (-not (Test-QproPython312 $stagedPython)) {
            throw 'Qpro private Python could not start. If Activity shows a missing DLL or VCRUNTIME error, install or repair Microsoft Visual C++ Redistributable x64: https://aka.ms/vc14/vc_redist.x64.exe'
        }
        if (Test-Path -LiteralPath $privatePythonRoot) {
            Write-Host 'Replacing an incomplete Qpro private Python copy.'
            Assert-QproManagedRuntimePath $privatePythonRoot 'python-3.12.10'
            Remove-Item -LiteralPath $privatePythonRoot -Recurse -Force
        }
        Move-Item -LiteralPath $stagedPythonRoot -Destination $privatePythonRoot
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Assert-QproManagedRuntimePath $stagingRoot (Split-Path -Leaf $stagingRoot)
            Assert-QproRuntimeWithoutLinks $stagingRoot -Tree
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }
    if (-not (Test-QproPython312 $privatePython)) {
        throw "Qpro's private Python did not start after extraction: $privatePython. For missing DLL or VCRUNTIME errors, install or repair Microsoft Visual C++ Redistributable x64: https://aka.ms/vc14/vc_redist.x64.exe"
    }
}

if (-not (Test-Path -LiteralPath $requirements)) {
    throw "requirements-runtime.txt is missing. Reinstall the release package."
}

function Test-PythonCommand([string]$Python, [string]$Code) {
    $previousPreference = $ErrorActionPreference
    try {
        # Import failures are expected while repairing a new/partial environment.
        # Do not let stderr become a terminating NativeCommandError.
        $ErrorActionPreference = "Continue"
        & $Python -I -c $Code *> $null
        return $LASTEXITCODE -eq 0
    }
    catch { return $false }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Get-QproRuntimeRecipe {
    $manifestPath = Join-Path $root 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 65536) { throw 'The release manifest is too large. Re-extract the release ZIP.' }
    try { $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json }
    catch { throw 'The release manifest could not be read. Re-extract the release ZIP.' }
    if (-not $manifest.PSObject.Properties['componentUpdates']) { return $null }
    $components = $manifest.componentUpdates
    if ($null -eq $components -or $components.schema -ne 1 -or
        $components.runtimeRecipe -isnot [string] -or $components.runtimeRecipe -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'The PC runtime update recipe is invalid. Re-extract the release ZIP.'
    }
    return $components.runtimeRecipe.ToLowerInvariant()
}

function Test-QproRuntimeReceipt([string]$Recipe) {
    if ([string]::IsNullOrWhiteSpace($Recipe)) { return $true }
    try {
        if (-not (Test-Path -LiteralPath $readyMarker -PathType Leaf) -or (Get-Item -LiteralPath $readyMarker).Length -gt 4096) { return $false }
        $record = [IO.File]::ReadAllText($readyMarker) | ConvertFrom-Json
        return $record.format -eq 'qpro-runtime-ready-v1' -and $record.recipeSha256 -eq $Recipe -and
            [IO.Path]::GetFullPath($record.python).Equals([IO.Path]::GetFullPath($venvPython), [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Write-ReadyMarker([string]$Python, [string]$Recipe, [string]$Backend) {
    $payload = @{
        format = "qpro-runtime-ready-v1"
        python = $Python
        completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        backend = $Backend
    }
    if (-not [string]::IsNullOrWhiteSpace($Recipe)) { $payload.recipeSha256 = $Recipe }
    $temporary = $readyMarker + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($payload | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $readyMarker -PathType Leaf) { [IO.File]::Replace($temporary, $readyMarker, $null) }
        else { [IO.File]::Move($temporary, $readyMarker) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Assert-QproRuntimeWithoutLinks([string]$Path, [switch]$Tree) {
    $absolute = [IO.Path]::GetFullPath($Path)
    for ($current = $absolute; -not [string]::IsNullOrWhiteSpace($current); $current = Split-Path -Parent $current) {
        if (-not (Test-Path -LiteralPath $current)) { continue }
        if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Qpro runtime setup will not follow a linked folder or file: $current"
        }
    }
    if ($Tree -and (Test-Path -LiteralPath $absolute -PathType Container)) {
        # Walk one level at a time so a junction is rejected before recursion.
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($absolute)
        while ($pending.Count -gt 0) {
            foreach ($entry in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Qpro runtime setup will not follow a linked folder or file: $($entry.FullName)"
                }
                if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            }
        }
    }
}

function Backup-QproRuntime {
    Assert-QproManagedRuntimePath $venvRoot '.venv'
    Assert-QproRuntimeWithoutLinks $venvRoot -Tree
    $files = @(Get-ChildItem -LiteralPath $venvRoot -File -Force -Recurse)
    $bytes = [long]0
    foreach ($file in $files) { $bytes += $file.Length }
    $drive = New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($venvRoot))
    if ($drive.AvailableFreeSpace -lt ($bytes + 512MB)) {
        throw ('Not enough free disk space to keep a recovery copy of the PC runtime. Free at least {0:N1} GB on {1} and retry; the existing runtime was kept.' -f (($bytes + 512MB) / 1GB), $drive.Name)
    }
    $backup = Join-Path $sharedRoot ('runtime-backup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    Write-Host ('Keeping a recovery copy of the PC runtime: {0:N0} files, {1:N1} MB.' -f $files.Count, ($bytes / 1MB))
    try {
        $savedEnvironment = Join-Path $backup '.venv'
        New-Item -ItemType Directory -Path $savedEnvironment | Out-Null
        $copiedBytes = [long]0
        $copiedFiles = 0
        $progressTimer = [Diagnostics.Stopwatch]::StartNew()
        foreach ($directory in @(Get-ChildItem -LiteralPath $venvRoot -Directory -Force -Recurse)) {
            New-Item -ItemType Directory -Path (Join-Path $savedEnvironment $directory.FullName.Substring($venvRoot.Length + 1)) -Force | Out-Null
        }
        foreach ($file in $files) {
            [IO.File]::Copy($file.FullName, (Join-Path $savedEnvironment $file.FullName.Substring($venvRoot.Length + 1)), $false)
            $copiedBytes += $file.Length
            $copiedFiles++
            if ($progressTimer.Elapsed.TotalSeconds -ge 5) {
                Write-Host ('Runtime recovery copy: {0}/{1} files, {2:N1}/{3:N1} MB.' -f $copiedFiles, $files.Count, ($copiedBytes / 1MB), ($bytes / 1MB))
                $progressTimer.Restart()
            }
        }
        if (Test-Path -LiteralPath $readyMarker -PathType Leaf) { [IO.File]::Copy($readyMarker, (Join-Path $backup 'runtime-ready.json'), $false) }
        # Completion describes the copy, not interpreter health: repair must
        # also be able to restore a pre-existing partial environment exactly.
        $completion = @{ format = 'qpro-runtime-backup-v1'; environment = $venvRoot; files = $copiedFiles; bytes = $copiedBytes }
        [IO.File]::WriteAllText((Join-Path $backup 'backup-complete.json'), ($completion | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Write-Host 'The recovery copy is complete. Updating the private Qpro environment...'
        return $backup
    } catch {
        # Nothing in the installed environment has changed if its copy failed.
        Write-Warning "The recovery copy did not complete; the current runtime was not changed. Incomplete copy: $backup"
        throw
    }
}

function Restore-QproRuntime([string]$Backup) {
    $expected = [IO.Path]::GetFullPath($sharedRoot).TrimEnd('\') + '\'
    $absolute = [IO.Path]::GetFullPath($Backup)
    if (-not $absolute.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $absolute) -notmatch '^runtime-backup-[0-9a-f]{32}$') { throw 'Invalid PC runtime recovery path.' }
    Assert-QproRuntimeWithoutLinks $absolute -Tree
    Assert-QproManagedRuntimePath $venvRoot '.venv'
    Assert-QproRuntimeWithoutLinks $venvRoot -Tree
    $savedEnvironment = Join-Path $absolute '.venv'
    $completionPath = Join-Path $absolute 'backup-complete.json'
    if (-not (Test-Path -LiteralPath $savedEnvironment -PathType Container) -or
        -not (Test-Path -LiteralPath $completionPath -PathType Leaf) -or (Get-Item -LiteralPath $completionPath).Length -gt 4096) {
        throw 'The runtime recovery copy is incomplete.'
    }
    $completion = [IO.File]::ReadAllText($completionPath) | ConvertFrom-Json
    if ($completion.format -ne 'qpro-runtime-backup-v1' -or
        -not [IO.Path]::GetFullPath($completion.environment).Equals([IO.Path]::GetFullPath($venvRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The runtime recovery copy belongs to a different environment.'
    }
    $savedFiles = @(Get-ChildItem -LiteralPath $savedEnvironment -File -Force -Recurse)
    $savedBytes = [long]0
    foreach ($file in $savedFiles) { $savedBytes += $file.Length }
    if ($savedFiles.Count -ne $completion.files -or $savedBytes -ne $completion.bytes) { throw 'The runtime recovery copy is incomplete or has changed.' }
    if (Test-Path -LiteralPath $venvRoot) { Remove-Item -LiteralPath $venvRoot -Recurse -Force }
    Move-Item -LiteralPath $savedEnvironment -Destination $venvRoot
    $savedMarker = Join-Path $absolute 'runtime-ready.json'
    if (Test-Path -LiteralPath $savedMarker) {
        if (Test-Path -LiteralPath $readyMarker) { [IO.File]::Replace($savedMarker, $readyMarker, $null) }
        else { [IO.File]::Move($savedMarker, $readyMarker) }
    } elseif (Test-Path -LiteralPath $readyMarker) { Remove-Item -LiteralPath $readyMarker -Force }
    Write-Host 'The previous Qpro PC runtime and its ready record were restored.'
}

function Invoke-QproRuntimePip([string[]]$Arguments) {
    # -I blocks Python path injection. --isolated and PIP_CONFIG_FILE=nul also
    # exclude pip user/global config and inherited target/prefix settings.
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $venvPython -I -m pip --isolated install --disable-pip-version-check --no-input --no-user @Arguments | ForEach-Object { Write-Host $_ }
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
}

function Reset-QproTrackingEnvironment {
    New-Item -ItemType Directory -Force -Path $sharedRoot | Out-Null
    Assert-QproRuntimeWithoutLinks $privatePythonRoot -Tree
    Assert-QproRuntimeWithoutLinks $venvRoot -Tree
    if (-not (Test-QproPython312 $privatePython)) { Install-QproPrivatePython }
    if (Test-Path -LiteralPath $venvRoot) {
        Write-Host "Rebuilding Qpro's incomplete tracking environment; other Python installations and Qpro settings are untouched."
        Assert-QproManagedRuntimePath $venvRoot '.venv'
        Remove-Item -LiteralPath $venvRoot -Recurse -Force
    }
    Write-Host "Creating Qpro's separate tracking environment from its private Python."
    & $privatePython -I -m venv $venvRoot
    if ($LASTEXITCODE -ne 0 -or -not (Test-QproPython312 $venvPython)) {
        throw 'Creating the local Python environment failed. Check Activity for the specific error. For missing DLL or VCRUNTIME errors, install or repair Microsoft Visual C++ Redistributable x64: https://aka.ms/vc14/vc_redist.x64.exe'
    }
}

function Get-QproRuntimeImportDiagnostic([string]$Python, [string]$Code = "import cv2,numpy,torch; assert hasattr(cv2,'namedWindow')") {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $details = @(& $Python -I -c $Code 2>&1 |
            ForEach-Object { $_.ToString() })
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($details -join "`n") }
    }
    catch {
        return [pscustomobject]@{ ExitCode = -1; Text = $_.Exception.Message }
    }
    finally { $ErrorActionPreference = $previousPreference }
}

function Get-QproNvidiaNames {
    $command = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) { return '' }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = $command.Source
    $process.StartInfo.Arguments = '--query-gpu=name --format=csv,noheader'
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        if (-not $process.Start()) { return '' }
        if (-not $process.WaitForExit(8000)) {
            $process.Kill()
            Write-Warning 'NVIDIA GPU detection took too long. Continuing with CPU setup; update the NVIDIA driver and rerun setup for CUDA.'
            return ''
        }
        if ($process.ExitCode -ne 0) { return '' }
        return $process.StandardOutput.ReadToEnd().Trim()
    }
    catch {
        Write-Warning "NVIDIA GPU detection failed: $($_.Exception.Message). Continuing with CPU setup."
        return ''
    }
    finally { $process.Dispose() }
}

function Invoke-QproRuntimeSetup {
    $recipe = Get-QproRuntimeRecipe
    if ($Update -and [string]::IsNullOrWhiteSpace($recipe)) { throw 'This release does not contain a PC runtime update recipe. Install the current complete release ZIP.' }
    Assert-QproRuntimeWithoutLinks $sharedRoot
    Assert-QproRuntimeWithoutLinks $readyMarker
    Assert-QproRuntimeWithoutLinks $venvRoot -Tree
    Write-Host 'Checking NVIDIA driver and GPU (up to 8 seconds)...'
    $nvidiaName = Get-QproNvidiaNames
    $nvidiaDetected = -not [string]::IsNullOrWhiteSpace($nvidiaName)
    if ($nvidiaDetected) { Write-Host "NVIDIA GPU detected: $nvidiaName" }

    if (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) {
        if ($Update) { Write-Host 'The update applies only to the shared Qpro runtime. The custom QPRO_PYTHON environment will not be modified.' }
        elseif ((Test-Path -LiteralPath $env:QPRO_PYTHON) -and
            (Test-PythonCommand $env:QPRO_PYTHON "import cv2,numpy,torch; assert hasattr(cv2,'namedWindow')")) {
            & $env:QPRO_PYTHON -I -c "import cv2,numpy,torch; print('Existing runtime ready:', torch.__version__, 'CUDA:', torch.cuda.is_available())"
            Write-Host 'The explicitly configured QPRO_PYTHON runtime is ready. Its packages are managed separately.'
            return
        }
    }

    $backup = $null
    $mutationStarted = $false
    $finished = $false
    $runtimeCheck = "import cv2,numpy,torch,sys,pathlib; p=pathlib.Path(sys.prefix).resolve(); assert all(pathlib.Path(m.__file__).resolve().is_relative_to(p) for m in (cv2,numpy,torch)), 'Runtime libraries must come from the private Qpro environment'; assert hasattr(cv2,'namedWindow'); assert torch.arange(4).sum().item() == 6"
    try {
        $existingPythonReady = Test-QproPython312 $venvPython
        $existingRuntimeReady = $false
        $existingCudaReady = $false
        $existingCudaBuild = $false
        if ($existingPythonReady) {
            $expectedPrefix = $venvRoot.Replace('\', '/').Replace("'", "\'")
            if (-not (Test-PythonCommand $venvPython "import os,sys; assert sys.prefix != sys.base_prefix and os.path.normcase(os.path.realpath(sys.prefix)) == os.path.normcase(os.path.realpath('$expectedPrefix'))")) {
                throw 'The shared Qpro interpreter is not its private virtual environment. No packages were changed. Re-extract the release and retry setup.'
            }
            Write-Host 'Checking OpenCV, NumPy, and PyTorch in the shared Qpro environment. The first import can take a while...'
            $existingRuntimeReady = Test-PythonCommand $venvPython $runtimeCheck
            $existingCudaBuild = $existingRuntimeReady -and (Test-PythonCommand $venvPython "import torch; assert torch.version.cuda is not None and torch.version.hip is None")
            $existingCudaReady = $existingCudaBuild -and (Test-PythonCommand $venvPython "import torch; assert torch.cuda.is_available(); assert torch.arange(4,device='cuda').sum().item() == 6; torch.cuda.synchronize()")
            if (-not $Update -and $existingRuntimeReady -and (-not $nvidiaDetected -or $existingCudaReady) -and (Test-QproRuntimeReceipt $recipe)) {
                & $venvPython -I -c "import cv2,numpy,torch; print('Existing shared runtime ready:', torch.__version__, 'CUDA:', torch.cuda.is_available())"
                # An import alone does not certify that a new release recipe ran.
                if ([string]::IsNullOrWhiteSpace($recipe) -and -not (Test-Path -LiteralPath $readyMarker)) {
                    Write-ReadyMarker $venvPython $null $(if ($existingCudaReady) { 'cuda' } else { 'cpu' })
                }
                Write-Host 'No runtime reinstall was needed.'
                return
            }
            if ($existingRuntimeReady) {
                Write-Host 'Updating the Qpro tracking requirements and PyTorch for this release.'
                if ($nvidiaDetected -and -not $existingCudaBuild) { Write-Host 'The existing runtime is CPU-only even though an NVIDIA GPU is present. Repairing its PyTorch installation.' }
            } else {
                $diagnostic = Get-QproRuntimeImportDiagnostic $venvPython $runtimeCheck
                Write-Host "The existing Qpro runtime failed its import check (exit $($diagnostic.ExitCode))."
                if (-not [string]::IsNullOrWhiteSpace($diagnostic.Text)) { Write-Host $diagnostic.Text }
            }
        }

        # Keep a byte-for-byte recovery copy before pip or environment repair can
        # modify an existing installation. Copy failure leaves it untouched.
        if (Test-Path -LiteralPath $venvRoot -PathType Container) { $backup = Backup-QproRuntime }
        $mutationStarted = $true
        if (Test-Path -LiteralPath $readyMarker) { Remove-Item -LiteralPath $readyMarker -Force }
        if (-not $existingPythonReady -or -not $existingRuntimeReady) {
            Write-Host 'Preparing the private shared Qpro Python environment...'
            Reset-QproTrackingEnvironment
        }
        Write-Host 'Installing the shared tracking runtime. PyTorch is large; this may take several minutes.'
        if ((Invoke-QproRuntimePip @('--upgrade', 'pip')) -ne 0) {
            throw 'Updating pip failed. Read the preceding Activity error. For VCRUNTIME or missing DLL errors, repair Microsoft Visual C++ Redistributable x64: https://aka.ms/vc14/vc_redist.x64.exe'
        }
        Write-Host 'Installing OpenCV, NumPy, and the other tracking requirements...'
        if ((Invoke-QproRuntimePip @('--upgrade', '-r', $requirements)) -ne 0) { throw 'Installing the tracking requirements failed. Read the preceding Activity error; existing Qpro models and captures are unchanged.' }

        # Driver detection can fail transiently. Retain an existing CUDA build
        # during updates rather than silently replacing it with CPU PyTorch.
        $useCuda = $nvidiaDetected -or $existingCudaBuild
        if ($useCuda) {
            Write-Host 'Installing the official CUDA 12.8 PyTorch wheel.'
            $cudaOptions = @('--upgrade')
            if ($existingRuntimeReady -and -not $existingCudaBuild) {
                $cudaOptions += '--force-reinstall'
                Write-Host 'Replacing the CPU-only PyTorch wheel with the CUDA build.'
            }
            $code = Invoke-QproRuntimePip ($cudaOptions + @($torchRequirement, '--index-url', $cudaIndex))
            if ($code -ne 0) {
                if ($existingCudaReady) { throw 'Updating CUDA PyTorch failed. The previous working GPU runtime will be restored.' }
                Write-Warning 'The CUDA PyTorch download failed. Installing the CPU build so tracking and training remain usable.'
                if ((Invoke-QproRuntimePip @('--upgrade', '--force-reinstall', $torchRequirement, '--index-url', $cpuIndex)) -ne 0) {
                    throw 'Installing both CUDA and CPU PyTorch builds failed. Check the internet connection and run setup again.'
                }
            }
        } else {
            Write-Host 'No NVIDIA driver/GPU was detected. Installing the official CPU PyTorch wheel.'
            if ((Invoke-QproRuntimePip @('--upgrade', $torchRequirement, '--index-url', $cpuIndex)) -ne 0) { throw 'Installing the CPU PyTorch build failed.' }
        }

        Write-Host 'Verifying OpenCV, NumPy, and a PyTorch tensor operation...'
        if (-not (Test-PythonCommand $venvPython $runtimeCheck)) {
            $diagnostic = Get-QproRuntimeImportDiagnostic $venvPython $runtimeCheck
            if (-not [string]::IsNullOrWhiteSpace($diagnostic.Text)) { Write-Host $diagnostic.Text }
            if ($diagnostic.Text -match '(?i)DLL load failed|VCRUNTIME|MSVCP140|Microsoft Visual C\+\+' -or
                $diagnostic.ExitCode -in @(-1073741515, 3221225781)) {
                throw 'A Windows native dependency could not load. Install or repair Microsoft Visual C++ Redistributable x64 from https://aka.ms/vc14/vc_redist.x64.exe, then retry. Qpro settings and captures can stay in place.'
            }
            throw "The installed runtime failed its final tensor/import check (exit $($diagnostic.ExitCode)). Send the complete Activity log."
        }
        $cudaReady = Test-PythonCommand $venvPython "import torch; assert torch.version.cuda is not None and torch.cuda.is_available(); assert torch.arange(4,device='cuda').sum().item() == 6; torch.cuda.synchronize()"
        if ($useCuda -and -not $cudaReady) {
            $gpuDiagnostic = Get-QproRuntimeImportDiagnostic $venvPython "import torch; print('PyTorch:', torch.__version__, 'CUDA build:', torch.version.cuda); assert torch.version.cuda is not None, 'Installed PyTorch has no CUDA build'; assert torch.cuda.is_available(), 'CUDA driver/device could not be initialized'; assert torch.arange(4,device='cuda').sum().item() == 6; torch.cuda.synchronize()"
            if (-not [string]::IsNullOrWhiteSpace($gpuDiagnostic.Text)) { Write-Host $gpuDiagnostic.Text }
        }
        if ($existingCudaReady -and -not $cudaReady) { throw 'The updated CUDA runtime did not pass its GPU tensor check. The previous working GPU runtime will be restored.' }
        if ($useCuda -and -not $cudaReady) { Write-Warning 'PyTorch could not initialize CUDA. CPU tracking/training is available; update the NVIDIA display driver and rerun PC runtime setup for GPU acceleration.' }
        & $venvPython -I -c "import cv2,numpy,torch; print('Runtime ready:', torch.__version__, 'CUDA:', torch.cuda.is_available())"
        Write-ReadyMarker $venvPython $recipe $(if ($cudaReady) { 'cuda' } else { 'cpu' })
        $finished = $true
        Write-Host 'PC runtime setup complete. The private runtime passed verification.'
    } catch {
        $originalError = $_
        if ($mutationStarted -and $null -ne $backup) {
            Write-Warning 'PC runtime setup failed. Restoring its previous environment...'
            try { Restore-QproRuntime $backup }
            catch {
                throw "PC runtime setup failed: $($originalError.Exception.Message) Recovery also failed: $($_.Exception.Message) Keep the recovery copy at $backup and close all Qpro tracking/training processes before retrying."
            }
        }
        throw $originalError
    } finally {
        if ($finished -and $null -ne $backup) {
            try {
                Assert-QproManagedRuntimePath $backup (Split-Path -Leaf $backup)
                Assert-QproRuntimeWithoutLinks $backup -Tree
                Remove-Item -LiteralPath $backup -Recurse -Force
            } catch { Write-Warning "The runtime update succeeded, but its recovery copy could not be removed: $backup" }
        }
    }
}

# Scope isolation to this setup process, then restore it for callers that
# dot-source the script. Neither pip config nor another app's Python variables
# may route Qpro's update into a system or user installation.
$savedEnvironment = @{}
$setupMutex = $null
$setupMutexOwned = $false
$gpuVisibilityVariables = @('CUDA_VISIBLE_DEVICES', 'HIP_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')
foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(PIP_|PYTHON)' -or $gpuVisibilityVariables -contains $_.Name })) {
    $savedEnvironment[$item.Name] = $item.Value
    [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process')
}
try {
    $env:PIP_CONFIG_FILE = 'nul'
    $env:PYTHONNOUSERSITE = '1'
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $runtimeKey = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($sharedRoot).ToLowerInvariant()))).Replace('-', '') }
    finally { $hasher.Dispose() }
    $setupMutex = New-Object Threading.Mutex($false, ('Local\QproRuntimeSetup-' + $runtimeKey))
    try { $setupMutexOwned = $setupMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $setupMutexOwned = $true }
    if (-not $setupMutexOwned) { throw 'Another Qpro PC runtime setup is already running. Wait for it to finish before retrying.' }
    Invoke-QproRuntimeSetup
} finally {
    if ($setupMutexOwned) { $setupMutex.ReleaseMutex() }
    if ($null -ne $setupMutex) { $setupMutex.Dispose() }
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(PIP_|PYTHON)' -or $gpuVisibilityVariables -contains $_.Name })) {
        [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process')
    }
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
}
