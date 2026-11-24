$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build-and-run.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }

# Exercise selected launcher paths in isolation. Do not run the full launcher,
# load installed runtimes, start ADB or connect to a headset.
$validation = $ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('if (($CompanionPid -gt 0)') }
$initial = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$receiverArguments' -and $node.Operator -eq 'Equals'
}, $true)
$forward = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($CompanionPid -gt 0)')
}, $true)
$directArgument = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($DirectVrchat) { $receiverArguments')
}, $true)
$sourceArgument = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($DirectVrchat -or $PSBoundParameters.ContainsKey(') -and $node.Extent.Text.Contains('--label-source')
}, $true)
if (-not $validation -or -not $initial -or -not $forward -or -not $directArgument -or -not $sourceArgument) { throw 'Companion launcher argument path was not found.' }
$probe = [scriptblock]::Create(($ast.ParamBlock.Extent.Text, $validation.Extent.Text, $initial.Extent.Text, $directArgument.Extent.Text, $sourceArgument.Extent.Text, $forward.Extent.Text, ',$receiverArguments') -join "`n")

$standalone = & $probe
if (($standalone -join '|') -ne '.\receiver.py|--port|27273') { throw 'Standalone launcher arguments changed.' }
$owned = & $probe -CompanionPid 45678 -CompanionStartFileTime 134041739581234567
if (($owned -join '|') -ne '.\receiver.py|--port|27273|--companion-pid|45678|--companion-start-filetime|134041739581234567') {
    throw 'Owner identifiers were lost or creation-time precision changed.'
}
foreach ($source in @('VirtualDesktop', 'SteamLink')) {
    $backup = & $probe -TrackingSource $source.ToLowerInvariant()
    if (($backup -join '|') -ne ".\receiver.py|--port|27273|--label-source|$source") {
        throw "VRCFaceTracking backup label source $source did not reach the receiver."
    }
    $direct = & $probe -DirectVrchat -TrackingSource $source.ToLowerInvariant()
    if (($direct -join '|') -ne ".\receiver.py|--port|27273|--direct-vrchat|--label-source|$source") {
        throw "Direct VRChat label source $source did not reach the receiver."
    }
}
foreach ($arguments in @(@{ CompanionPid = 45678 }, @{ CompanionStartFileTime = [long]134041739581234567 })) {
    $rejected = $false
    try { $null = & $probe @arguments } catch {
        if (-not $_.Exception.Message.Contains('must be supplied together')) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A partial owner identity was accepted.' }
}
Write-Host 'Companion launcher: standalone, exact FILETIME forwarding and incomplete identity checks passed.'

# Exercise the production policy and admission guards with no legacy config,
# module helper or VRCFaceTracking process. This intentionally omits ADB.
$launcherBody = ($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] }).Body.Statements
$guards = @($launcherBody | Where-Object { $_.Extent.Text -match '^if \(-not \$DirectVrchat\)|^\$(recordEnabled|labelsEnabled|steamLabelsEnabled|steamVrRequired|vrcftRequired) =|^if \(\$TrackingSource -eq|^if \(\$(steamLabelsEnabled|vrcftRequired|steamVrRequired|DirectVrchat)(\)| -and)' -and
    -not $_.Extent.Text.Contains('$receiverArguments') -and -not $_.Extent.Text.Contains('Camera worker for') })
if ($guards.Count -ne 11) { throw 'Launcher preflight guards were not found.' }
$admission = [scriptblock]::Create((@($ast.ParamBlock.Extent.Text, '$PSScriptRoot = $env:LOCALAPPDATA') +
    @($guards | ForEach-Object { $_.Extent.Text }) + '$labelsEnabled, $steamVrRequired, $vrcftRequired') -join "`n")
function Test-Path { param($LiteralPath, $PathType) return $false }
function Get-Process {
    param($Name, $ErrorAction)
    if ($Name -ne 'vrserver') { throw "Unexpected process check: $Name" }
    if ($script:fixtureSteamVrRunning) { return $true }
}
$fixtureSteamVrRunning = $true

foreach ($source in @('VirtualDesktop', 'SteamLink')) {
    $capture = & $admission -DirectVrchat -TrackingSource $source -TongueStillCalibration
    if (($capture -join '|') -ne 'True|True|False') { throw "Direct capture routing was incorrect for $source." }
    $pupil = & $admission -DirectVrchat -TrackingSource $source -PupilOutput
    if (($pupil -join '|') -ne 'False|True|False') { throw "Direct pupil routing was incorrect for $source." }
}

function Assert-AdmissionFailure([scriptblock]$Attempt, [string]$Message) {
    try { $null = & $Attempt } catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw }
        return
    }
    throw "Expected launcher admission failure: $Message"
}
Assert-AdmissionFailure { & $admission -TrackingSource SteamLink -TongueStillCalibration } 'module is configured for virtual-desktop'
Assert-AdmissionFailure { & $admission -TrackingSource VirtualDesktop -TonguePreview } 'module check helper is missing'
Assert-AdmissionFailure { & $admission -DirectVrchat -TrackingSource SteamLink -TongueStillCalibration -LabelsPort 27280 } 'labels use port 27274'
Assert-AdmissionFailure { & $admission -DirectVrchat -TrackingSource SteamLink -EyeCalibration } 'provide combined gaze'
Assert-AdmissionFailure { & $admission -TrackingSource SteamLink -EyeCalibration } 'provide combined gaze'
Assert-AdmissionFailure { & $admission -TrackingSource SteamLink -HybridPreview } 'provide combined gaze'
$fixtureSteamVrRunning = $false
Assert-AdmissionFailure { & $admission -DirectVrchat -TrackingSource SteamLink -TongueStillCalibration } 'Start SteamVR first'
Write-Host 'Direct VRChat launcher: capture, pupil, Steam Link label port, SteamVR requirement and legacy module checks passed.'

# Run the actual camera startup and relay-owned cleanup statements with inert
# process/ADB doubles. This catches transport branch and lifetime regressions
# without launching a child or touching a headset.
$startupFirst = @($launcherBody | Where-Object { $_.Extent.Text.StartsWith('$injectCommand =') })
$startupLast = @($launcherBody | Where-Object { $_.Extent.Text.StartsWith('if (-not $wirelessCameraTransport)') -and $_.Extent.Text.Contains('$injectOutput') })
$ownerTry = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
$relayCleanup = @($ownerTry.Finally.Statements | Where-Object {
    $_.Extent.Text.StartsWith('if ($relayStarted)') -or $_.Extent.Text.StartsWith('if ($null -eq $relayProcess)')
})
if ($startupFirst.Count -ne 1 -or $startupLast.Count -ne 1 -or $relayCleanup.Count -ne 2) {
    throw 'Camera relay startup or owned cleanup was not found.'
}
$startupStatements = @($launcherBody | Where-Object {
    $_.Extent.StartOffset -ge $startupFirst[0].Extent.StartOffset -and
    $_.Extent.EndOffset -le $startupLast[0].Extent.EndOffset
})
$cameraStartup = [scriptblock]::Create(($startupStatements.Extent.Text -join "`n"))
$cameraCleanup = [scriptblock]::Create(($relayCleanup.Extent.Text -join "`n"))
function Assert-CameraFixture([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-FixtureAdb {
    Assert-CameraFixture (($args -join '|') -eq 'shell|su|-c|/data/local/tmp/questpro-camera-injector /data/local/tmp/libquestpro-camera-streamer-v8.so') 'Unexpected startup ADB command'
    $script:cameraEvents.Add('inject')
    $global:LASTEXITCODE = if ($script:cameraFailure -eq 'inject') { 7 } else { 0 }
    'synthetic injector output'
}
function Tee-Object { param([Parameter(ValueFromPipeline)]$InputObject, $FilePath) process { $InputObject } }
function New-QproRelayClientLogs {
    $script:cameraEvents.Add('new-logs')
    $script:relayClientOutputPath = 'fixture-output'; $script:relayClientErrorPath = 'fixture-error'
}
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$PassThru, $WindowStyle, $RedirectStandardOutput, $RedirectStandardError, $ErrorAction)
    Assert-CameraFixture ($FilePath -eq 'Invoke-FixtureAdb' -and $PassThru -and $WindowStyle -eq 'Hidden' -and $ErrorAction -eq 'Stop') 'Relay child ownership options changed'
    Assert-CameraFixture ($ArgumentList -eq 'shell su -c "/data/local/tmp/questpro-camera-relay-v8 --mode eyes --max-fps 24"') 'Relay is not a foreground ADB/su command'
    Assert-CameraFixture ($RedirectStandardOutput -eq 'fixture-output' -and $RedirectStandardError -eq 'fixture-error') 'Relay diagnostics were not redirected'
    $script:cameraEvents.Add('start')
    if ($script:cameraFailure -eq 'start') { throw 'synthetic process creation failure' }
    $child = [pscustomobject]@{ HasExited = $false }
    $child | Add-Member ScriptMethod WaitForExit {
        param($timeout)
        $script:cameraEvents.Add('wait')
        return $this.HasExited
    }
    $child | Add-Member ScriptMethod Kill { $script:cameraEvents.Add('kill'); $this.HasExited = $true }
    $child | Add-Member ScriptMethod Dispose { $script:cameraEvents.Add('dispose') }
    return $child
}
function Wait-QproRelayListening {
    param($Process, $OutputPath, $ErrorPath, $Port, $Mode)
    Assert-CameraFixture ($null -ne $Process -and $OutputPath -eq 'fixture-output' -and $ErrorPath -eq 'fixture-error' -and $Port -eq 27273 -and $Mode -eq 'eyes') 'Wrong readiness probe'
    $script:cameraEvents.Add('ready')
    if ($script:cameraFailure -eq 'listener') { throw 'SHARED_MEMORY_FAILED operation=fchmod error=Operation not permitted' }
}
function Test-QproWirelessCameraCleanupIdentity { $script:cameraEvents.Add('identity'); return $true }
function Invoke-QproRecoveryAdb {
    param($Arguments, [switch]$IgnoreCancellation)
    Assert-CameraFixture ($Arguments -eq 'shell su -c "/data/local/tmp/questpro-camera-relay-v8 --stop"' -and $IgnoreCancellation) 'Unexpected relay cleanup command'
    $script:cameraEvents.Add('stop')
    if ($script:cameraFailure -eq 'cleanup') { throw 'synthetic unreachable headset' }
    if ($script:cameraFailure -eq '') { $relayProcess.HasExited = $true }
    return @{ ExitCode = 0; Output = ''; Error = '' }
}
function Complete-QproRelayClientLogs {
    param($Process)
    $script:cameraEvents.Add($(if ($null -eq $Process) { 'complete-null' } else { 'complete-child' }))
}
foreach ($wireless in @($false, $true)) {
    foreach ($failure in @('', 'start', 'listener', 'inject', 'cleanup')) {
        $script:cameraEvents = [Collections.Generic.List[string]]::new()
        $script:cameraFailure = $failure
        $wirelessCameraTransport = $wireless; $relayStarted = $false; $relayProcess = $null
        $adbExecutable = 'Invoke-FixtureAdb'; $CameraMode = 'eyes'; $MaxFps = 24; $StreamPort = 27273
        $startupFailure = ''
        # Match the launcher's surrounding preference; its process creation
        # must still explicitly request terminating errors.
        $ErrorActionPreference = 'Continue'
        try { . $cameraStartup } catch { $startupFailure = $_.Exception.Message } finally { . $cameraCleanup }
        $ErrorActionPreference = 'Stop'
        $eventText = $cameraEvents -join '|'
        $expectedStartupFailure = $failure -in @('start', 'listener', 'inject')
        Assert-CameraFixture (($startupFailure -ne '') -eq $expectedStartupFailure) "Startup error was lost: wireless=$wireless failure=$failure events=$eventText"
        if ($failure -eq 'listener') { Assert-CameraFixture ($startupFailure.Contains('Operation not permitted')) 'Listener permission diagnostic was lost' }
        if ($failure -eq 'start') { Assert-CameraFixture ($startupFailure.Contains('synthetic process creation failure')) 'Child creation diagnostic was lost' }
        if ($failure -eq 'inject') { Assert-CameraFixture ($startupFailure.Contains('Injection failed with exit code 7')) 'Injection error was lost' }
        if ($wireless) {
            Assert-CameraFixture ($cameraEvents[0] -eq 'inject') 'Wi-Fi injection must precede the persistent relay session'
        } elseif ($failure -notin @('start', 'listener')) {
            Assert-CameraFixture ($cameraEvents.IndexOf('ready') -lt $cameraEvents.IndexOf('inject')) 'USB injection must follow verified listener readiness'
        } else {
            Assert-CameraFixture (-not $cameraEvents.Contains('inject')) 'USB injected after relay startup failure'
        }
        $childExpected = $failure -ne 'start' -and -not ($wireless -and $failure -eq 'inject')
        if ($childExpected) {
            $expectedCleanup = if ($failure -eq '') { 'stop|wait|complete-child|dispose' } else { 'stop|wait|kill|wait|complete-child|dispose' }
            Assert-CameraFixture ($eventText.EndsWith($expectedCleanup)) "Owned child was not reaped after stop: $eventText"
            Assert-CameraFixture ($cameraEvents.Contains('identity') -eq $wireless) 'Wireless cleanup identity guard changed'
        } else {
            Assert-CameraFixture ($eventText.EndsWith('complete-null') -and -not $cameraEvents.Contains('stop') -and -not $cameraEvents.Contains('dispose')) "Cleanup targeted a child that was never created: $eventText"
        }
    }
}
Write-Host 'Camera launcher: USB/Wi-Fi live relay ownership, injection order, startup failures and cleanup checks passed.'
