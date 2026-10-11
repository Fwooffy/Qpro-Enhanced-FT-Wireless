param(
    [ValidateSet('List', 'Restore')][string]$Action = 'List',
    [string]$BackupName = ''
)

# Runtime recovery is deliberately separate from headset and model recovery.
# A saved interpreter is only promoted after its imports and tensor checks pass.
function Assert-QproRecoveryPath([string]$SharedRoot, [string]$Path, [string]$NamePattern) {
    $managed = [IO.Path]::GetFullPath($SharedRoot).TrimEnd('\')
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not (Split-Path -Parent $absolute).Equals($managed, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $absolute) -notmatch $NamePattern) { throw 'Invalid Qpro runtime recovery path.' }
    return $absolute
}

function Assert-QproRecoveryWithoutLinks([string]$Path, [switch]$Tree) {
    for ($current = [IO.Path]::GetFullPath($Path); -not [string]::IsNullOrWhiteSpace($current); $current = Split-Path -Parent $current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runtime recovery will not follow a linked file or folder: $current"
        }
    }
    if ($Tree -and (Test-Path -LiteralPath $Path -PathType Container)) {
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($Path)
        while ($pending.Count -gt 0) {
            foreach ($entry in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Runtime recovery will not follow a linked file or folder: $($entry.FullName)" }
                if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            }
        }
    }
}

function Read-QproRecoveryJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 4096) {
        throw 'The saved runtime has an incomplete recovery record.'
    }
    Assert-QproRecoveryWithoutLinks $Path
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json
}

function Write-QproRecoveryJson([string]$Path, $Record) {
    Assert-QproRecoveryWithoutLinks $Path
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, ($Record | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path -PathType Leaf) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $Path) }
}

function Get-QproRecoveryInventory([string]$EnvironmentRoot) {
    Assert-QproRecoveryWithoutLinks $EnvironmentRoot -Tree
    $files = @(Get-ChildItem -LiteralPath $EnvironmentRoot -File -Force -Recurse)
    $bytes = [long]0
    foreach ($file in $files) { $bytes += $file.Length }
    return [pscustomobject]@{ Files = $files; Bytes = $bytes }
}

function Get-QproRuntimeBackup([string]$SharedRoot, [string]$Backup, [switch]$VerifyInventory) {
    $absolute = Assert-QproRecoveryPath $SharedRoot $Backup '^runtime-backup-[0-9a-f]{32}$'
    Assert-QproRecoveryWithoutLinks $absolute -Tree:$VerifyInventory
    $environment = Join-Path ([IO.Path]::GetFullPath($SharedRoot)) '.venv'
    $saved = Join-Path $absolute '.venv'
    $completion = Read-QproRecoveryJson (Join-Path $absolute 'backup-complete.json')
    if ($completion.format -ne 'qpro-runtime-backup-v1' -or $completion.environment -isnot [string] -or
        -not [IO.Path]::GetFullPath($completion.environment).Equals($environment, [StringComparison]::OrdinalIgnoreCase) -or
        $completion.files -isnot [ValueType] -or $completion.bytes -isnot [ValueType] -or
        $completion.files -lt 1 -or $completion.bytes -lt 1 -or
        -not (Test-Path -LiteralPath (Join-Path $saved 'Scripts\python.exe') -PathType Leaf)) {
        throw 'The saved runtime is incomplete or belongs to another Qpro installation.'
    }
    Assert-QproRecoveryWithoutLinks $saved
    $receipt = Read-QproRecoveryJson (Join-Path $absolute 'runtime-ready.json')
    if ($receipt.format -ne 'qpro-runtime-ready-v1' -or $receipt.python -isnot [string] -or
        -not [IO.Path]::GetFullPath($receipt.python).Equals((Join-Path $environment 'Scripts\python.exe'), [StringComparison]::OrdinalIgnoreCase) -or
        ($receipt.PSObject.Properties['backend'] -and $receipt.backend -notin @('cpu', 'cuda'))) {
        throw 'The saved runtime has no valid readiness record. Use Repair runtime instead.'
    }
    if ($VerifyInventory) {
        $inventory = Get-QproRecoveryInventory $saved
        if ($inventory.Files.Count -ne $completion.files -or $inventory.Bytes -ne $completion.bytes) {
            throw 'The saved runtime is incomplete or has changed. Use Repair runtime instead.'
        }
    }
    $date = [DateTimeOffset]::MinValue
    if ($completion.PSObject.Properties['completedUtc']) { [void][DateTimeOffset]::TryParse([string]$completion.completedUtc, [ref]$date) }
    if ($date -eq [DateTimeOffset]::MinValue) { $date = [DateTimeOffset](Get-Item -LiteralPath $absolute).LastWriteTimeUtc }
    return [pscustomobject]@{ Name = Split-Path -Leaf $absolute; Path = $absolute; Environment = $saved; Receipt = $receipt; CompletedUtc = $date.ToString('O'); Files = $completion.files; Bytes = $completion.bytes }
}

function Get-QproRuntimeRecoveryCopies([string]$SharedRoot) {
    Assert-QproRecoveryWithoutLinks $SharedRoot
    $copies = @(); $unavailable = 0
    if (Test-Path -LiteralPath $SharedRoot -PathType Container) {
        foreach ($folder in @(Get-ChildItem -LiteralPath $SharedRoot -Directory -Filter 'runtime-backup-*' -Force)) {
            try {
                $backup = Get-QproRuntimeBackup $SharedRoot $folder.FullName
                $copies += [pscustomobject]@{ name = $backup.Name; completedUtc = $backup.CompletedUtc; bytes = $backup.Bytes; backend = $backup.Receipt.backend }
            } catch { $unavailable++ }
        }
    }
    $interrupted = 0
    foreach ($journal in @(Get-ChildItem -LiteralPath $SharedRoot -File -Filter 'runtime-restore-*.json' -ErrorAction SilentlyContinue)) {
        try {
            $record = Read-QproRecoveryJson $journal.FullName
            if ($record.format -eq 'qpro-runtime-restore-v1' -and $record.phase -notin @('complete', 'rolled-back', 'recovered')) { $interrupted++ }
        } catch { $interrupted++ }
    }
    return [pscustomobject]@{ format = 'qpro-runtime-recovery-list-v1'; copies = @($copies | Sort-Object completedUtc -Descending); unavailable = $unavailable; interrupted = $interrupted }
}

function Test-QproRecoveredRuntime([string]$Python, [string]$Backend) {
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $Python
    # -I and an explicit prefix check prevent a relocated recovery copy from
    # certifying imports supplied by the installed or another Python environment.
    $code = "import sys,pathlib,platform,cv2,numpy,torch; assert sys.version_info[:2] == (3,12) and sys.maxsize > 2**32 and platform.machine().lower() in ('amd64','x86_64'); p=pathlib.Path(sys.prefix).resolve(); assert sys.prefix != sys.base_prefix and pathlib.Path(sys.executable).resolve().parent.parent == p; assert all(pathlib.Path(m.__file__).resolve().is_relative_to(p) for m in (cv2,numpy,torch)); assert hasattr(cv2,'namedWindow'); assert torch.arange(4).sum().item() == 6"
    if ($Backend -eq 'cuda') { $code += "; assert torch.version.cuda is not None and torch.version.hip is None and torch.cuda.is_available(); assert torch.arange(4,device='cuda').sum().item() == 6; torch.cuda.synchronize()" }
    $process.StartInfo.Arguments = '-I -c "' + $code + '"'
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($name in @($process.StartInfo.EnvironmentVariables.Keys)) {
        if ($name -match '^(PIP_|PYTHON)' -or $name -in @('CUDA_VISIBLE_DEVICES','HIP_VISIBLE_DEVICES','ROCR_VISIBLE_DEVICES','GPU_DEVICE_ORDINAL')) { $process.StartInfo.EnvironmentVariables.Remove($name) }
    }
    try {
        if (-not $process.Start()) { throw 'The saved Python runtime could not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Checking the saved runtime timed out. The current runtime was kept. Close tracking and training before retrying.' }
        $details = $stderr.GetAwaiter().GetResult().Trim()
        [void]$stdout.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "The saved runtime failed its import or tensor check (exit $($process.ExitCode)). The current runtime was kept. $details" }
    } finally { $process.Dispose() }
}

function Complete-QproRuntimeRecoveryJournals([string]$SharedRoot) {
    Assert-QproRecoveryWithoutLinks $SharedRoot
    foreach ($oldJournal in @(Get-ChildItem -LiteralPath $SharedRoot -File -Filter 'runtime-restore-*.json')) {
        try {
            $oldState = Read-QproRecoveryJson $oldJournal.FullName
            if ($oldState.format -eq 'qpro-runtime-restore-v1' -and $oldState.phase -in @('staging', 'prepared', 'previous-saved', 'runtime-restored')) {
                $oldState.phase = 'recovered'
                Write-QproRecoveryJson $oldJournal.FullName $oldState
            }
        } catch { Write-Warning "An old recovery record could not be updated: $($oldJournal.Name)" }
    }
}

function Restore-QproSavedRuntime([string]$SharedRoot, [string]$BackupName) {
    $managed = [IO.Path]::GetFullPath($SharedRoot).TrimEnd('\')
    if ($BackupName -notmatch '^runtime-backup-[0-9a-f]{32}$') { throw 'Choose a saved Qpro runtime from the recovery list.' }
    $backup = Get-QproRuntimeBackup $managed (Join-Path $managed $BackupName) -VerifyInventory
    $active = Join-Path $managed '.venv'; $ready = Join-Path $managed 'runtime-ready.json'
    Assert-QproRecoveryWithoutLinks $active -Tree
    Assert-QproRecoveryWithoutLinks $ready
    $drive = New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($managed))
    if ($drive.AvailableFreeSpace -lt ([long]$backup.Bytes + 512MB)) { throw 'Not enough free disk space to verify a saved runtime while keeping the current one. Free disk space and retry; no runtime was changed.' }
    $token = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $managed ('runtime-restore-' + $token)
    $staged = Join-Path $stage '.venv'
    $previous = Join-Path $managed ('runtime-backup-' + $token)
    $journal = Join-Path $managed ('runtime-restore-' + $token + '.json')
    Assert-QproRecoveryPath $managed $stage '^runtime-restore-[0-9a-f]{32}$' | Out-Null
    Assert-QproRecoveryPath $managed $previous '^runtime-backup-[0-9a-f]{32}$' | Out-Null
    $hadActive = Test-Path -LiteralPath $active -PathType Container
    $hadMarker = Test-Path -LiteralPath $ready -PathType Leaf
    $state = @{ format = 'qpro-runtime-restore-v1'; phase = 'staging'; source = $BackupName; previous = Split-Path -Leaf $previous; stage = Split-Path -Leaf $stage; hadActive = $hadActive; hadMarker = $hadMarker }
    Write-QproRecoveryJson $journal $state
    try {
        New-Item -ItemType Directory -Path $staged | Out-Null
        Write-Host 'Checking the saved runtime in a separate recovery folder. The current runtime remains available...'
        foreach ($directory in @(Get-ChildItem -LiteralPath $backup.Environment -Directory -Recurse -Force)) {
            New-Item -ItemType Directory -Path (Join-Path $staged $directory.FullName.Substring($backup.Environment.Length + 1)) -Force | Out-Null
        }
        $copyTimer = [Diagnostics.Stopwatch]::StartNew()
        $copiedBytes = [long]0
        foreach ($file in @(Get-ChildItem -LiteralPath $backup.Environment -File -Recurse -Force)) {
            [IO.File]::Copy($file.FullName, (Join-Path $staged $file.FullName.Substring($backup.Environment.Length + 1)), $false)
            $copiedBytes += $file.Length
            if ($copyTimer.Elapsed.TotalSeconds -ge 5) {
                Write-Host ('Saved runtime verification copy: {0:N1}/{1:N1} MB.' -f ($copiedBytes / 1MB), ($backup.Bytes / 1MB))
                $copyTimer.Restart()
            }
        }
        $stagedInventory = Get-QproRecoveryInventory $staged
        if ($stagedInventory.Files.Count -ne $backup.Files -or $stagedInventory.Bytes -ne $backup.Bytes) { throw 'The staged recovery copy is incomplete. The current runtime was kept.' }
        Write-Host 'Verifying the saved Python runtime, tracking imports and tensor operations (up to 60 seconds)...'
        Test-QproRecoveredRuntime (Join-Path $staged 'Scripts\python.exe') $backup.Receipt.backend

        New-Item -ItemType Directory -Path $previous | Out-Null
        Write-Host 'Saved runtime checks passed. Keeping the current runtime before switching...'
        if ($hadMarker) { [IO.File]::Copy($ready, (Join-Path $previous 'runtime-ready.json'), $false) }
        if ($hadActive) {
            $oldInventory = Get-QproRecoveryInventory $active
            Write-QproRecoveryJson (Join-Path $previous 'backup-complete.json') @{ format = 'qpro-runtime-backup-v1'; environment = $active; files = $oldInventory.Files.Count; bytes = $oldInventory.Bytes; completedUtc = [DateTimeOffset]::UtcNow.ToString('O') }
        }
        $state.phase = 'prepared'; Write-QproRecoveryJson $journal $state
    }
    catch {
        $checkError = $_
        try {
            Assert-QproRecoveryPath $managed $stage '^runtime-restore-[0-9a-f]{32}$' | Out-Null
            Assert-QproRecoveryWithoutLinks $stage -Tree
            if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
            Assert-QproRecoveryPath $managed $previous '^runtime-backup-[0-9a-f]{32}$' | Out-Null
            Assert-QproRecoveryWithoutLinks $previous -Tree
            if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
            $state.phase = 'rolled-back'; Write-QproRecoveryJson $journal $state
        } catch { Write-Warning "The failed verification copy could not be removed. Its recovery record is at $journal" }
        throw $checkError
    }

    # Record both paths before the first rename. A killed helper leaves its
    # journal and complete old environment discoverable on the next Hub launch.
    $oldMoved = $false; $newMoved = $false
    try {
        if ($hadActive) { Move-Item -LiteralPath $active -Destination (Join-Path $previous '.venv'); $oldMoved = $true }
        $state.phase = 'previous-saved'; Write-QproRecoveryJson $journal $state
        Move-Item -LiteralPath $staged -Destination $active; $newMoved = $true
        $state.phase = 'runtime-restored'; Write-QproRecoveryJson $journal $state
        Write-QproRecoveryJson $ready $backup.Receipt
        $state.phase = 'complete'; Write-QproRecoveryJson $journal $state
        # A verified replacement resolves earlier interrupted swaps. Keep their
        # paths and files for investigation without warning on every later launch.
        try { Complete-QproRuntimeRecoveryJournals $managed }
        catch { Write-Warning "The runtime was restored, but an old recovery record could not be updated: $($_.Exception.Message)" }
        Write-Host 'The saved Qpro PC runtime passed verification and was restored. Models, recordings and settings stayed in place.'
        Write-Host ('QPRO_RUNTIME_RECOVERY ' + (@{ format = 'qpro-runtime-recovery-result-v1'; restored = $BackupName; previous = $(if ($hadActive) { Split-Path -Leaf $previous } else { $null }); verified = $true } | ConvertTo-Json -Compress))
    } catch {
        $restoreError = $_
        try {
            if ($newMoved) { Move-Item -LiteralPath $active -Destination $staged }
            if ($oldMoved) { Move-Item -LiteralPath (Join-Path $previous '.venv') -Destination $active }
            if ($hadMarker) {
                $savedMarker = Join-Path $previous 'runtime-ready.json'
                $temporaryMarker = $ready + '.' + $token + '.tmp'
                [IO.File]::Copy($savedMarker, $temporaryMarker, $true)
                if (Test-Path -LiteralPath $ready) { [IO.File]::Replace($temporaryMarker, $ready, [NullString]::Value) }
                else { [IO.File]::Move($temporaryMarker, $ready) }
            }
            elseif (Test-Path -LiteralPath $ready) { Remove-Item -LiteralPath $ready -Force }
            $state.phase = 'rolled-back'; Write-QproRecoveryJson $journal $state
        } catch { throw "Runtime restore failed: $($restoreError.Exception.Message) Recovery also needs attention: $($_.Exception.Message). Keep the saved copies and close Qpro tracking/training before retrying." }
        throw $restoreError
    }
}

function Invoke-QproRuntimeRecovery {
    $ErrorActionPreference = 'Stop'
    $shared = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime'
    $mutex = $null; $owned = $false
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $key = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($shared).ToLowerInvariant()))).Replace('-', '') }
        finally { $hasher.Dispose() }
        $mutex = New-Object Threading.Mutex($false, ('Local\QproRuntimeSetup-' + $key))
        try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { throw 'Another Qpro runtime setup or recovery is running. Wait for it to finish.' }
        if ($Action -eq 'Restore') { Restore-QproSavedRuntime $shared $BackupName }
        else { Write-Host ('QPRO_RUNTIME_RECOVERY ' + ((Get-QproRuntimeRecoveryCopies $shared) | ConvertTo-Json -Depth 5 -Compress)) }
    } finally { if ($owned) { $mutex.ReleaseMutex() }; if ($null -ne $mutex) { $mutex.Dispose() } }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-QproRuntimeRecovery }
