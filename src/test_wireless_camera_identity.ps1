$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'camera-transport-recovery.ps1')
$StopFile=''; $CompanionPid=0; $CompanionStartFileTime=0
$script:wirelessCameraIdentity = 'approved-private-serial'
$script:checks=0
function Assert-Identity([bool]$Value,[string]$Message) { $script:checks++; if (-not $Value) { throw $Message } }
function Invoke-QproRecoveryAdb($Arguments,$TimeoutMilliseconds,[switch]$IgnoreCancellation) {
    $script:commands.Add($Arguments)
    if ($Arguments -eq 'get-state') { return @{ExitCode=0; Output='device'; Error=''} }
    if ($Arguments -eq 'shell getprop ro.serialno') {
        if ($script:unreachable) { throw [TimeoutException]::new('offline identity probe timed out') }
        return @{ExitCode=0; Output=$script:identity; Error=''}
    }
    throw 'Unexpected remote mutation in identity test'
}
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build-and-run.ps1'),[ref]$tokens,[ref]$errors)
$cleanupNodes=@($ast.FindAll({param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '$relayStarted'
},$true))
Assert-Identity ($errors.Count -eq 0 -and $cleanupNodes.Count -eq 1) 'Could not isolate actual launcher cleanup'
$runLauncherCleanup=[scriptblock]::Create($cleanupNodes[0].Extent.Text)
function Complete-QproRelayClientLogs { }
foreach ($case in @('same','different','empty','unreachable','unpinned')) {
    $script:commands=[Collections.Generic.List[string]]::new()
    $script:identity = if ($case -eq 'different') {'replacement-private-serial'} elseif ($case -eq 'empty') {''} else {'approved-private-serial'}
    $script:unreachable = $case -eq 'unreachable'
    $script:wirelessCameraIdentity = if ($case -eq 'unpinned') {''} else {'approved-private-serial'}
    $failed=$false
    try { Assert-QproWirelessCameraIdentity } catch { $failed=$true }
    Assert-Identity ($failed -eq ($case -ne 'same')) "Unexpected identity result: $case"
    $allowed=Test-QproWirelessCameraCleanupIdentity
    Assert-Identity ($allowed -eq ($case -eq 'same')) "Unsafe final cleanup authorization: $case"
    if ($case -ne 'same') {
        $failed=$false
        try { Repair-QproWirelessCameraTransport } catch { $failed=$true }
        Assert-Identity $failed "Unsafe recovery accepted: $case"
        # Execute the launcher's actual finally-owned relay cleanup in isolation.
        # A mismatched endpoint may only receive the read-only identity probe.
        $relayStarted=$true; $wirelessCameraTransport=$true
        $relayProcess=[pscustomobject]@{HasExited=$true}
        $relayProcess | Add-Member ScriptMethod WaitForExit { param($timeout) return $true }
        $relayProcess | Add-Member ScriptMethod Dispose { }
        & $runLauncherCleanup
    }
    Assert-Identity (@($script:commands | Where-Object {$_ -ne 'get-state' -and $_ -ne 'shell getprop ro.serialno'}).Count -eq 0) "Remote mutation sent before identity approval: $case"
}
# Optional identity failure must not prevent normal launch or capture. Execute
# the launcher's actual recording/recovery assignments after initial pin setup.
$assignmentText = @('recordEnabled','captureSession','allowCameraRecovery') | ForEach-Object {
    $name=$_
    $nodes=@($ast.FindAll({param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq $name
    },$true))
    if ($nodes.Count -ne 1) { throw "Missing launcher assignment: $name" }
    $nodes[0].Extent.Text
}
$evaluate=[scriptblock]::Create(($assignmentText -join "`n") + "`nreturn @{ Recording=`$recordEnabled; Recovery=`$allowCameraRecovery }")
foreach ($unreachable in @($false,$true)) {
    $script:identity=''; $script:unreachable=$unreachable
    Initialize-QproWirelessCameraIdentity
    Assert-Identity ([string]::IsNullOrWhiteSpace($script:wirelessCameraIdentity)) 'Optional initial pin failure retained stale identity'
    foreach ($capture in @($false,$true)) {
        $AdbTarget='192.168.1.50:5555'; $RecordPath=if($capture){'session.qpc'}else{''}
        $Record=$Calibration=$TongueCalibration=$TongueStillCalibration=$false
        $TongueCorrectionCalibration=$TongueRefinementCalibration=$TongueArcCalibration=$EyeCalibration=$false
        $result=& $evaluate
        $normalRunReached=$true
        Assert-Identity ($normalRunReached -and $result.Recording -eq $capture -and -not $result.Recovery) 'Missing initial identity blocked normal run/capture or allowed recovery'
    }
}
# Never downgrade Stop or owner exit into the optional-identity warning.
function Invoke-QproRecoveryAdb { throw [OperationCanceledException]::new('offline owner exited') }
$cancelled=$false
try { Initialize-QproWirelessCameraIdentity } catch [OperationCanceledException] { $cancelled=$true }
Assert-Identity $cancelled 'Initial identity setup masked cancellation'
Write-Host "PASS $script:checks offline wireless identity checks"
