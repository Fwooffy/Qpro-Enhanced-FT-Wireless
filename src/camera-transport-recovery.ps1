# PC-only wireless recovery. Never inject, reconnect/pair ADB, or restart captures.
function Test-QproWirelessCameraTarget([string]$Value) {
    # Same supported endpoint contract as Connect-QproWireless Normalize-Target;
    # launchers receive an explicit endpoint, not the UI's optional bare IPv4.
    if ($Value.Trim() -notmatch '^(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d{2,5})$') { return $false }
    $address = $null
    $port = [int]$Matches['port']
    return [Net.IPAddress]::TryParse($Matches['ip'], [ref]$address) -and
        $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        $port -ge 1024 -and $port -le 65535
}

function Test-QproCameraRecoveryAllowed([string]$Target, [bool]$CaptureSession) {
    return -not $CaptureSession -and (Test-QproWirelessCameraTarget $Target)
}

function Test-QproCameraRecoveryCancelled {
    if ($StopFile -and (Test-Path -LiteralPath (Resolve-WorkspacePath $StopFile))) { return $true }
    if ($CompanionPid -gt 0) {
        try {
            $owner = [Diagnostics.Process]::GetProcessById($CompanionPid)
            try { return $owner.HasExited -or $owner.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $CompanionStartFileTime }
            finally { $owner.Dispose() }
        } catch { return $true }
    }
    return $false
}

function Assert-QproCameraRecoveryActive {
    if (Test-QproCameraRecoveryCancelled) { throw [OperationCanceledException]::new('Camera transport recovery cancelled.') }
}

function Wait-QproCameraRecovery([int]$Milliseconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $Milliseconds) {
        Assert-QproCameraRecoveryActive
        Start-Sleep -Milliseconds 100
    }
    Assert-QproCameraRecoveryActive
}

function Invoke-QproRecoveryAdb([string]$Arguments, [int]$TimeoutMilliseconds = 4000, [switch]$IgnoreCancellation) {
    if (-not $IgnoreCancellation) { Assert-QproCameraRecoveryActive }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $adbExecutable
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $false
    try {
        $null = $process.Start()
        $started = $true
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(100)) {
            if (-not $IgnoreCancellation) { Assert-QproCameraRecoveryActive }
            if ($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds) { throw [TimeoutException]::new('Wireless recovery ADB command timed out.') }
        }
        if (-not $IgnoreCancellation) { Assert-QproCameraRecoveryActive }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 1000)) {
            throw [TimeoutException]::new('Wireless recovery ADB output did not close.')
        }
        return @{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    } finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill()
            if (-not $process.WaitForExit(2000)) { throw 'Wireless recovery ADB client did not exit.' }
        }
        $process.Dispose()
    }
}

function Repair-QproWirelessCameraTransport {
    # A transient outage gets a twelve-second elapsed readiness budget, plus
    # bounded child teardown. Never change device selection or authentication.
    $ready = $false
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    for ($probe = 0; $probe -lt 12 -and $deadline.ElapsedMilliseconds -lt 12000; $probe++) {
        $remaining = [Math]::Min(2000, 12000 - $deadline.ElapsedMilliseconds)
        try { $state = Invoke-QproRecoveryAdb 'get-state' ([int]$remaining) }
        catch [TimeoutException] { $state = @{ ExitCode = 1; Output = '' } }
        if ($state.ExitCode -eq 0 -and $state.Output.Trim() -eq 'device') { $ready = $true; break }
        if ($deadline.ElapsedMilliseconds -lt 12000) { Wait-QproCameraRecovery ([int][Math]::Min(1000, 12000 - $deadline.ElapsedMilliseconds)) }
    }
    if (-not $ready) { throw 'The selected wireless headset did not return during camera recovery.' }
    Assert-QproWirelessCameraIdentity
    $stopped = Invoke-QproRecoveryAdb 'shell su -c "/data/local/tmp/questpro-camera-relay-v8 --stop"'
    if ($stopped.ExitCode -ne 0 -or $stopped.Output -notmatch 'RELAY_STOPPED' -or
        ($stopped.Output + $stopped.Error) -match 'STALE_RELAY_STOP_FAILED') {
        throw 'The previous wireless relay could not be stopped safely.'
    }
    # Reap the previous live adb/su client before creating its replacement.
    if ($null -ne $script:relayProcess) {
        if (-not $script:relayProcess.WaitForExit(2000)) {
            $script:relayProcess.Kill()
            if (-not $script:relayProcess.WaitForExit(2000)) { throw 'The previous relay client did not exit.' }
        }
        Complete-QproRelayClientLogs $script:relayProcess
        $script:relayProcess.Dispose()
        $script:relayProcess = $null
    }
    Assert-QproCameraRecoveryActive
    Assert-QproWirelessCameraIdentity
    New-QproRelayClientLogs
    $script:relayProcess = Start-Process -FilePath $adbExecutable -ArgumentList $relayArguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $relayClientOutputPath -RedirectStandardError $relayClientErrorPath -ErrorAction Stop
    Wait-QproRelayListening $script:relayProcess $relayClientOutputPath $relayClientErrorPath $StreamPort $CameraMode
    $forward = Invoke-QproRecoveryAdb "forward tcp:$StreamPort tcp:$StreamPort"
    if ($forward.ExitCode -ne 0) { throw 'Wireless camera port forwarding could not be restored.' }
    Assert-QproCameraRecoveryActive
}

function Get-QproWirelessCameraIdentity {
    $result = Invoke-QproRecoveryAdb 'shell getprop ro.serialno' 2000 -IgnoreCancellation:$script:cameraIdentityCleanupProbe
    $value = $result.Output.Trim()
    if ($result.ExitCode -ne 0 -or $value -notmatch '^[A-Za-z0-9._-]{4,128}$' -or $value -in @('unknown', 'null', 'none')) {
        throw 'The wireless headset identity could not be verified.'
    }
    return $value
}

function Initialize-QproWirelessCameraIdentity {
    $script:wirelessCameraIdentity = $null
    try { $script:wirelessCameraIdentity = Get-QproWirelessCameraIdentity }
    catch [OperationCanceledException] { throw }
    catch {
        # Firmware may hide ro.serialno. Ordinary tracking/capture still works;
        # never turn a missing optional recovery identity into startup failure.
        Assert-QproCameraRecoveryActive
        Write-Host 'Wireless headset identity is unavailable. Automatic camera recovery is disabled for this session; if transport drops, stop and reconnect the approved headset manually.'
    }
}

function Assert-QproWirelessCameraIdentity {
    if ([string]::IsNullOrWhiteSpace($script:wirelessCameraIdentity)) { throw 'No approved wireless headset identity is available for recovery.' }
    $current = Get-QproWirelessCameraIdentity
    if ($current -cne $script:wirelessCameraIdentity) { throw 'The wireless endpoint now identifies another headset; camera recovery was stopped.' }
}

function Test-QproWirelessCameraCleanupIdentity {
    $script:cameraIdentityCleanupProbe = $true
    try { Assert-QproWirelessCameraIdentity; return $true }
    catch { Write-Host 'Wireless relay remote cleanup skipped because the approved headset identity could not be verified.'; return $false }
    finally { $script:cameraIdentityCleanupProbe = $false }
}

function New-QproRelayClientLogs {
    # Never reuse a previous run's listener line, even if removal/truncation
    # fails or Start-Process cannot create the replacement adb client.
    if ($null -ne $script:relayClientOwnedLogs -and @($script:relayClientOwnedLogs).Count -gt 0) { throw 'Previous relay temporary logs could not be cleaned; refusing another relay start.' }
    $directory = [IO.Path]::GetTempPath()
    $token = [guid]::NewGuid().ToString('N')
    $script:relayClientOutputPath = Join-Path $directory "questpro-relay-$token-output.txt"
    $script:relayClientErrorPath = Join-Path $directory "questpro-relay-$token-error.txt"
    $script:relayClientOwnedLogs = @($script:relayClientOutputPath, $script:relayClientErrorPath)
}

function Complete-QproRelayClientLogs($Writer, [string]$SummaryPath = (Join-Path $PSScriptRoot 'questpro-live-relay.txt')) {
    if ($null -eq $script:relayClientOwnedLogs -or @($script:relayClientOwnedLogs).Count -eq 0) { return }
    # Null means Start-Process failed before returning a writer. Never remove
    # logs while a known writer is alive, including failed termination.
    if ($null -ne $Writer -and -not $Writer.HasExited) {
        Write-Host 'Relay temporary logs retained because its writer has not exited.'
        return
    }
    try {
        $text = Read-QproRelayClientLogs $relayClientOutputPath $relayClientErrorPath -Strict
        if ($text) { Set-Content -LiteralPath $SummaryPath -Value $text -ErrorAction Stop }
    } catch {
        Write-Host "Relay final log could not be saved; temporary logs retained: $($_.Exception.Message)"
        return
    }
    $remaining = @()
    foreach ($path in $script:relayClientOwnedLogs) {
        try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop } }
        catch { $remaining += $path; Write-Host "Relay temporary log retained at ${path}: $($_.Exception.Message)" }
    }
    $script:relayClientOwnedLogs = $remaining
}

function Read-QproRelayClientLogs([string]$OutputPath, [string]$ErrorPath, [switch]$Strict) {
    $parts = foreach ($path in @($OutputPath, $ErrorPath)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try { Get-Content -LiteralPath $path -Raw -ErrorAction Stop }
            catch [IO.IOException] { if ($Strict) { throw } } # Startup writer can briefly hold its new file.
        }
    }
    return ($parts -join "`n").Trim()
}

function Wait-QproRelayListening($Process, [string]$OutputPath, [string]$ErrorPath,
    [int]$Port, [string]$Mode, [int]$TimeoutMilliseconds = 8000,
    [scriptblock]$ReadLogs = { Read-QproRelayClientLogs $OutputPath $ErrorPath },
    [scriptblock]$Pause = { Wait-QproCameraRecovery 100 }, [scriptblock]$Elapsed = $null) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if ($null -eq $Elapsed) { $Elapsed = { $clock.ElapsedMilliseconds } }
    $pattern = '(?m)^RELAY_LISTENING address=127\.0\.0\.1 port=' + $Port +
        ' mode=' + [regex]::Escape($Mode) + ' max_fps=\d+\r?$'
    $text = ''
    while ($true) {
        Assert-QproCameraRecoveryActive
        $text = & $ReadLogs
        # An exited client loses the required su lifetime, even if it managed
        # to print a listener line. Android/ADB daemon chatter is not readiness.
        if ($null -eq $Process -or $Process.HasExited) {
            throw "The camera root relay exited before listener startup completed. $text"
        }
        if ($text -match $pattern) { Write-Host $Matches[0]; return }
        if ((& $Elapsed) -ge $TimeoutMilliseconds) {
            throw "The camera root relay did not report its listener within ${TimeoutMilliseconds}ms. $text"
        }
        & $Pause
    }
}

function Invoke-QproCameraReceiverSession([bool]$AllowRecovery, [scriptblock]$RunReceiver, [scriptblock]$RepairTransport) {
    $retries = 0
    while ($true) {
        Assert-QproCameraRecoveryActive
        $code = & $RunReceiver ($retries -gt 0)
        if ($code -eq 0) { return }
        if ($code -ne 75 -or -not $AllowRecovery -or $retries -ge 2) {
            throw "The PC tracking runtime exited with code $code. Wireless recovery attempts: $retries."
        }
        Assert-QproCameraRecoveryActive
        $retries++
        Write-Host "CAMERA_TRANSPORT_RECOVERY attempt=$retries limit=2 outputs=closed"
        & $RepairTransport
        # Receiver starts afresh: no partial payload, cached prediction, or
        # prior sequence/timestamp can survive a recovered connection.
    }
}
