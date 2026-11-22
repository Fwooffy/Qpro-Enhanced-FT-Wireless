$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'camera-transport-recovery.ps1')
$StopFile = ''; $CompanionPid = 0; $CompanionStartFileTime = 0
$script:checks = 0
function Assert-Recovery([bool]$Value, [string]$Message) {
    $script:checks++
    if (-not $Value) { throw $Message }
}
Assert-Recovery (Test-QproCameraRecoveryAllowed '192.168.1.50:5555' $false) 'Explicit supported Wi-Fi target was excluded'
Assert-Recovery (-not (Test-QproCameraRecoveryAllowed '2G0YC1USB12345' $false)) 'USB serial enabled wireless recovery'
Assert-Recovery (-not (Test-QproCameraRecoveryAllowed '192.168.1.50:5555' $true)) 'Capture session enabled recovery'
foreach ($target in @('', '192.168.1.50', '999.168.1.50:5555', '192.168.1.50:80', 'headset:5555', '192.168.1.50:65536')) {
    Assert-Recovery (-not (Test-QproWirelessCameraTarget $target)) "Unsupported wireless target accepted: $target"
}
# Execute the launcher's actual assignments in isolation. This catches optional
# capture paths activating recording without the explicit -Record switch.
$parseTokens = $null; $parseErrors = $null
$launcherAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build-and-run.ps1'), [ref]$parseTokens, [ref]$parseErrors)
Assert-Recovery ($parseErrors.Count -eq 0) 'Launcher parse error'
$assignments = @('recordEnabled', 'captureSession', 'allowCameraRecovery') | ForEach-Object {
    $name = $_
    $nodes = @($launcherAst.FindAll({param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq $name
    }, $true))
    if ($nodes.Count -ne 1) { throw "Expected one launcher assignment for $name" }
    $nodes[0].Extent.Text
}
$evaluateAssignments = [scriptblock]::Create(($assignments -join "`n") + "`n return `$allowCameraRecovery")
function Test-LauncherRecoveryCase([hashtable]$Options) {
    $AdbTarget = '192.168.1.50:5555'; $RecordPath = ''
    $wirelessCameraIdentity = 'approved-private-serial'
    $Record = $Calibration = $TongueCalibration = $TongueStillCalibration = $false
    $TongueCorrectionCalibration = $TongueRefinementCalibration = $TongueArcCalibration = $EyeCalibration = $false
    $ModelPreview = $TonguePreview = $HybridPreview = $false
    foreach ($key in $Options.Keys) { Set-Variable -Name $key -Value $Options[$key] }
    return & $evaluateAssignments
}
Assert-Recovery (-not (Test-LauncherRecoveryCase @{RecordPath='capture.qpc'})) 'Standalone RecordPath enabled capture restart'
foreach ($flag in @('Record','Calibration','TongueCalibration','TongueStillCalibration','TongueCorrectionCalibration','TongueRefinementCalibration','TongueArcCalibration','EyeCalibration')) {
    Assert-Recovery (-not (Test-LauncherRecoveryCase @{$flag=$true})) "Finite mode enabled recovery: $flag"
}
Assert-Recovery (-not (Test-LauncherRecoveryCase @{AdbTarget='2G0YC1USB12345'})) 'Launcher enabled USB recovery'
Assert-Recovery (Test-LauncherRecoveryCase @{TonguePreview=$true}) 'Live tongue preview unnecessarily excluded'
Assert-Recovery (Test-LauncherRecoveryCase @{HybridPreview=$true}) 'Live hybrid preview unnecessarily excluded'

# Exercise the launcher's receiver invocation scriptblock and the real supervisor
# together, while replacing the native process and headset operations.
$receiverLaunch = $launcherAst.Find({param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Invoke-QproCameraReceiverSession'
}, $true)
$runReceiver = $receiverLaunch.CommandElements[2].ScriptBlock.GetScriptBlock()
$receiverArguments = @('.\receiver.py', '--no-window')
$python = 'Invoke-OfflineCameraReceiver'
function Invoke-OfflineCameraReceiver {
    $global:LASTEXITCODE = if ($script:receiverInvocations.Count -eq 0 -and $script:simulateIdle) { 75 } else { 0 }
    $script:receiverInvocations.Add([string[]]$args)
}
$script:receiverInvocations = [Collections.Generic.List[string[]]]::new()
$allowCameraRecovery = Test-LauncherRecoveryCase @{}
$script:simulateIdle = $true
Invoke-QproCameraReceiverSession $allowCameraRecovery $runReceiver { }
Assert-Recovery ($script:receiverInvocations.Count -eq 2) 'Initial wireless idle exit failed to restart'
Assert-Recovery ($script:receiverInvocations[0] -contains '--recover-idle-camera') 'Initial wireless receiver did not opt into idle recovery'
Assert-Recovery ($script:receiverInvocations[0] -notcontains '--recovered-camera-connection') 'Initial wireless receiver was marked recovered'
Assert-Recovery ($script:receiverInvocations[1] -contains '--recover-idle-camera') 'Restarted wireless receiver did not opt into idle recovery'
Assert-Recovery ($script:receiverInvocations[1] -contains '--recovered-camera-connection') 'Restarted wireless receiver was not marked recovered'
foreach ($options in @(@{AdbTarget='2G0YC1USB12345'}, @{RecordPath='capture.qpc'}, @{EyeCalibration=$true})) {
    $script:receiverInvocations.Clear()
    $allowCameraRecovery = Test-LauncherRecoveryCase $options
    $script:simulateIdle = $false
    Invoke-QproCameraReceiverSession $allowCameraRecovery $runReceiver { throw 'Unexpected camera repair' }
    Assert-Recovery ($script:receiverInvocations[0] -notcontains '--recover-idle-camera') 'USB, recording or calibration receiver opted into idle recovery'
}

# Exercise real bounded process I/O with local PowerShell, never ADB.
$adbExecutable = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })
$localResult = Invoke-QproRecoveryAdb '-NoProfile -Command "[Console]::WriteLine(''device'')"'
Assert-Recovery ($localResult.ExitCode -eq 0 -and $localResult.Output.Trim() -eq 'device') 'Bounded command output failed'
$clock = [Diagnostics.Stopwatch]::StartNew(); $timedOut = $false
try { $null = Invoke-QproRecoveryAdb '-NoProfile -Command "Start-Sleep -Seconds 30"' 200 }
catch [TimeoutException] { $timedOut = $true }
Assert-Recovery ($timedOut -and $clock.ElapsedMilliseconds -lt 5000) 'ADB substitute did not time out and reap promptly'
$adbExecutable = Join-Path $PSScriptRoot 'nonexistent-offline-adb.exe'
$startFailure = ''
try { $null = Invoke-QproRecoveryAdb 'get-state' } catch { $startFailure = $_.Exception.Message }
Assert-Recovery ($startFailure -ne '' -and $startFailure -notmatch 'associated with this object') 'Process.Start failure was masked by disposal'
function Run-Case([int[]]$Codes, [bool]$Allowed, [int]$ExpectedRuns, [int]$ExpectedRepairs, [bool]$Fails) {
    $script:codes = $Codes; $script:runs = 0; $script:repairs = 0; $failed = $false
    $script:recoveredConnections = [Collections.Generic.List[bool]]::new()
    try {
        Invoke-QproCameraReceiverSession $Allowed {
            param([bool]$RecoveredConnection)
            $script:recoveredConnections.Add($RecoveredConnection)
            $value = $script:codes[$script:runs]; $script:runs++; return $value
        } { $script:repairs++ }
    } catch { $failed = $true }
    Assert-Recovery ($script:runs -eq $ExpectedRuns) 'Unexpected receiver restart count'
    Assert-Recovery (-not $script:recoveredConnections[0]) 'Dropped the first frame on the initial receiver'
    Assert-Recovery (@($script:recoveredConnections | Select-Object -Skip 1 | Where-Object { -not $_ }).Count -eq 0) 'Kept the possibly stale first frame on a recovered receiver'
    Assert-Recovery ($script:repairs -eq $ExpectedRepairs) 'Unexpected repair count'
    Assert-Recovery ($failed -eq $Fails) 'Unexpected supervisor outcome'
}
Run-Case @(75,0) $true 2 1 $false
Run-Case @(75,75,75,0) $true 3 2 $true
Run-Case @(1,0) $true 1 0 $true
Run-Case @(75,0) $false 1 0 $true
Run-Case @(0) $true 1 0 $false
# Cancellation between receiver exit and repair must prevent any restart.
function Test-QproCameraRecoveryCancelled { return $script:runs -ge 1 }
Run-Case @(75,0) $true 1 0 $true
function Test-QproCameraRecoveryCancelled { return $false }
# Repair failure must never create another receiver.
$script:runs = 0; $failed = $false
try { Invoke-QproCameraReceiverSession $true { $script:runs++; return 75 } { throw 'stop failed' } }
catch { $failed = $true }
Assert-Recovery ($failed -and $script:runs -eq 1) 'Unsafe restart after failed relay repair'

# Exercise real recovery ordering with offline ADB/process fakes.
$script:events = [Collections.Generic.List[string]]::new()
$script:probes = 0; $script:relayProcess = $null
$script:wirelessCameraIdentity = 'approved-private-serial'
$adbExecutable = 'offline-adb'; $relayArguments = 'shell su -c relay'; $StreamPort = 27273
$relayClientOutputPath = 'offline-output'; $relayClientErrorPath = 'offline-error'
function Invoke-QproRecoveryAdb($Arguments, $TimeoutMilliseconds) {
    $script:events.Add($Arguments)
    if ($Arguments -eq 'shell getprop ro.serialno') { return @{ExitCode=0; Output='approved-private-serial'; Error=''} }
    if ($Arguments -eq 'get-state') {
        $script:probes++
        if ($script:probes -eq 2) { throw [TimeoutException]::new('offline timed out probe') }
        return @{ ExitCode = $(if ($script:probes -lt 4) {1} else {0}); Output = 'device'; Error = '' }
    }
    return @{ ExitCode = 0; Output = 'RELAY_STOPPED capture=idle'; Error = '' }
}
function Wait-QproCameraRecovery($Milliseconds) { Assert-QproCameraRecoveryActive }
function Start-Process {
    param($FilePath,$ArgumentList,[switch]$PassThru,$WindowStyle,$RedirectStandardOutput,$RedirectStandardError,$ErrorAction)
    $script:events.Add('start-relay')
    return [pscustomobject]@{ HasExited = $false }
}
function New-QproRelayClientLogs { $script:events.Add('fresh-relay-logs') }
function Complete-QproRelayClientLogs { $script:events.Add('relay-logs-completed') }
function Wait-QproRelayListening { $script:events.Add('listener-verified') }
Repair-QproWirelessCameraTransport
Assert-Recovery ($script:probes -eq 4) 'Did not tolerate delayed ADB return'
$stopIndex = $script:events.IndexOf('shell su -c "/data/local/tmp/questpro-camera-relay-v8 --stop"')
Assert-Recovery ($stopIndex -ge 0 -and $stopIndex -lt $script:events.IndexOf('start-relay')) 'Started relay before remote stop'
Assert-Recovery ($script:events[$script:events.Count-1] -eq 'forward tcp:27273 tcp:27273') 'Missing replacement forward'
# Replay a mid-payload receiver exit, three unavailable probes (including a
# timed-out ADB call), old relay reaping, forwarding repair, and a fresh receiver.
$script:events.Clear(); $script:probes = 0; $script:runs = 0
$script:relayProcess = [pscustomobject]@{}
$script:relayProcess | Add-Member ScriptMethod WaitForExit { param($timeout) $script:events.Add('old-relay-exited'); return $true }
$script:relayProcess | Add-Member ScriptMethod Dispose { $script:events.Add('old-relay-disposed') }
Invoke-QproCameraReceiverSession $true {
    $script:runs++; $script:events.Add("receiver-$script:runs")
    if ($script:runs -eq 1) { return 75 }; return 0
} { Repair-QproWirelessCameraTransport }
Assert-Recovery ($script:runs -eq 2 -and $script:probes -eq 4) 'Interrupted stream did not recover through delayed ADB'
Assert-Recovery ($script:events.IndexOf('old-relay-disposed') -lt $script:events.IndexOf('start-relay')) 'Overlapping relay clients'
Assert-Recovery ($script:events.IndexOf('forward tcp:27273 tcp:27273') -lt $script:events.IndexOf('receiver-2')) 'Receiver restarted before forward restoration'
function Invoke-QproRecoveryAdb($Arguments, $TimeoutMilliseconds) {
    if ($Arguments -eq 'shell getprop ro.serialno') { return @{ExitCode=0; Output='approved-private-serial'; Error=''} }
    if ($Arguments -eq 'get-state') { return @{ExitCode=0; Output='device'; Error=''} }
    return @{ExitCode=1; Output=''; Error='stop failed'}
}
$script:events.Clear(); $failed = $false
try { Repair-QproWirelessCameraTransport } catch { $failed = $true }
Assert-Recovery ($failed -and -not $script:events.Contains('start-relay')) 'Started replacement after failed remote stop'
Write-Host "PASS $script:checks offline camera recovery checks"
