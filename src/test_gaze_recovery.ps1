$ErrorActionPreference = 'Stop'

# Run the actual recovery helpers against a stateful fake root/ADB boundary.
# No headset command, installed module, or live ADB server is used.
$tokens = $null
$errors = $null
$sourcePath = Join-Path $PSScriptRoot 'native-eye-local-branch-test.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }

# Exercise the live argument path without starting the headset gaze session.
& {
    function Get-Process { param($Name, $ErrorAction) }
    function Test-Path { param($LiteralPath) return $fixtureHasGazeProfile }
    $fixtureHasGazeProfile = $true
    $gazeGuards = @($ast.EndBlock.Statements | Where-Object {
        $_.Extent.Text -match '^\$liveGazeOutput =|^if \(\$VrcftOutput|^if \(\(\$RuntimePreview -or \$liveGazeOutput\)'
    })
    $gazeLaunch = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -eq '$RuntimePreview -or $liveGazeOutput'
    }, $true)
    if ($gazeGuards.Count -ne 4 -or -not $gazeLaunch) { throw 'Live gaze launcher guards were not found.' }
    $arguments = @($gazeLaunch.Clauses[0].Item2.Statements | Where-Object { $_.Extent.Text -notmatch '^& \$python' })
    $liveProbe = [scriptblock]::Create((@($ast.ParamBlock.Extent.Text,
        '$root = Split-Path -Parent $sourcePath; $adb = "fixture-adb"; $calibrationOutputPath = "fixture-profile"; $inspectionAction = $false') +
        @($gazeGuards | ForEach-Object { $_.Extent.Text }) +
        @($arguments | ForEach-Object { $_.Extent.Text }) + ',$runtimeArguments') -join "`n")
    $direct = & $liveProbe -DirectVrchat -NoWindow
    if (($direct -join '|') -notlike '*|fixture-profile|--direct-vrchat|--sample-timeout-seconds|20|--no-window') {
        throw 'Direct gaze did not enable the expected output and timeout without VRCFaceTracking.'
    }
    try { $null = & $liveProbe -VrcftOutput; throw 'Legacy gaze bypassed its VRCFaceTracking guard.' }
    catch { if ($_.Exception.Message -notlike 'Start VRCFaceTracking before*') { throw } }
    $fixtureHasGazeProfile = $false
    try { $null = & $liveProbe -DirectVrchat; throw 'Direct gaze bypassed its calibration guard.' }
    catch { if ($_.Exception.Message -notlike 'Independent visual-axis calibration not found:*') { throw } }
    Write-Host 'PASS: Direct gaze enables local output, keeps profile validation and preserves the legacy VRCFaceTracking guard.'
}

foreach ($name in @('Get-RootShellRequest', 'Get-TargetMount', 'Get-GazeDeviceSerial', 'Get-GazeModules', 'Get-GazeServicePid', 'Get-GazeForeignMounts', 'Assert-GazeServiceModel', 'Assert-NoExternalGazeMethod', 'Show-GazeSetup', 'Reset-LegacyGazeSelection', 'Get-GazeFileHash', 'Get-GazeOwnerHost', 'Assert-GazeOwnerStopped', 'Read-GazeJournal', 'New-GazeJournal', 'Restore-StockModel', 'Invoke-GazeRecovery')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $function) { throw "Missing gaze recovery helper: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}

function Assert-Equal($Expected, $Actual, [string]$Context) {
    if ($Expected -ne $Actual) { throw "$Context expected '$Expected', got '$Actual'" }
}

$quotedShell = "if test -f '/path with spaces/file'; then printf '%s' `"`$value`"; fi`r`nfor d in /data/adb/modules/*; do echo `"`$d`"; done"
$request = Get-RootShellRequest $quotedShell
if ($request -notmatch '^printf %s ([A-Za-z0-9+/=]+) \| base64 -d \| su -c sh$') { throw 'Root shell request exposes syntax to Windows argument processing' }
$decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
Assert-Equal ($quotedShell.Replace("`r`n", "`n") + "`n") $decoded 'Root command preserves quotes and normalizes Windows line endings'
Write-Host 'PASS: root shell request preserves quoted compound syntax and Unix line endings'

function Reset-Fixture([string]$Original = 'false') {
    $script:gazeSessionId = '1234567890abcdef1234567890abcdef'
    $script:gazeJournalPath = '/data/local/tmp/qpro-independent-gaze-session.json'
    $script:remoteModel = "/data/local/tmp/qpro-seacliff-independent-axes-$gazeSessionId.ptl"
    $script:targetModel = '/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl'
    $script:modelProperty = 'persist.device_config.oculus_shared_vision.oculus_eyetracking_enable_experimental_model'
    $script:cleanupNeeded = $false
    $script:ConfirmLegacyReset = $true
    $script:fixture = @{
        Journal = $null; JournalMode = '0:600'; Serial = 'FIXTUREQUEST'
        Firmware = '51503870024400340'; SocialFiltering = ''
        Property = $Original; Mount = $false; ForeignMount = $false; ModelChanged = $false
        SourceExists = $true; SourceIdentity = '100:200'; SourceHash = ('a' * 64); OriginalHash = ('b' * 64)
        BootId = '11111111-2222-3333-4444-555555555555'; Running = $true
        Failure = ''; IgnorePropertyWrite = $false; Commands = [Collections.Generic.List[string]]::new()
        Modules = @(); ScanIncomplete = $false; RootAncestor = ''; ServiceAncestor = ''; ServiceModelChanged = $false
        TransparentOverlay = $false; UpperEmpty = $true; ServiceUpperEmpty = $true; OdmWritable = $false; ServiceUnpatched = $false
        OverlayOptions = 'ro,lowerdir=/odm,upperdir=/dev/mount_overlayfs/upper/odm,workdir=/dev/mount_overlayfs/worker/64781/2'
        ServiceOverlayFlags = 'ro'
        Mutations = [Collections.Generic.List[string]]::new()
    }
    $script:journal = [PSCustomObject]@{
        format = 'qpro-headset-gaze-session-v1'; owner = 'QproFaceTracking'; sessionId = $gazeSessionId
        ownerHost = Get-GazeOwnerHost; ownerPid = '2147483647'; ownerStarted = '1'
        deviceSerial = $fixture.Serial; bootId = $fixture.BootId
        targetModel = $targetModel; remoteModel = $remoteModel; modelProperty = $modelProperty
        sourceHash = $fixture.SourceHash; sourceIdentity = $fixture.SourceIdentity
        originalModelHash = $fixture.OriginalHash; originalProperty = $Original
    }
}

function Save-FixtureJournal {
    $fixture.Journal = $journal | ConvertTo-Json -Compress
}

function Invoke-Root([string]$Command, [switch]$AllowFailure) {
    $fixture.Commands.Add($Command)
    if ($Command.Contains('__QPRO_GAZE_MODULE_SCAN_COMPLETE__')) {
        if ($fixture.ScanIncomplete) { return '__QPRO_MODULE_SCAN_FAILED__' }
        return ((@($fixture.Modules | ForEach-Object { "__QPRO_GAZE_MODULE__=$_" }) + '__QPRO_GAZE_MODULE_SCAN_COMPLETE__') -join "`n")
    }
    if ($Command -eq 'pidof trackingservice') {
        if ($fixture.Running) { return '1234' }
        throw 'Fixture tracking service stopped'
    }
    if ($Command -eq 'cat /proc/1234/mountinfo') {
        $flags = if ($fixture.OdmWritable) { 'rw' } else { 'ro' }
        $lines = @("40 1 253:13 / /odm $flags,relatime - ext4 /dev/block/dm-13 $flags")
        if ($fixture.ServiceAncestor) { $lines += "41 40 0:999 / $($fixture.ServiceAncestor) ro - overlay overlay ro" }
        if ($fixture.Mount) { $lines += "42 40 0:200 / $targetModel ro - ext4 /dev/block/dm-13 ro" }
        if ($fixture.TransparentOverlay) { $lines += "43 40 0:201 /etc /odm/etc $($fixture.ServiceOverlayFlags) - overlay overlay $($fixture.OverlayOptions)" }
        return $lines -join "`n"
    }
    if ($Command -eq "sha256sum '/proc/1234/root$targetModel'") {
        $hash = if ($fixture.ServiceModelChanged) { 'd' * 64 }
            elseif ($fixture.Mount -and -not $fixture.ServiceUnpatched) { $fixture.SourceHash } else { $fixture.OriginalHash }
        return "$hash /proc/1234/root$targetModel"
    }
    if ($Command -eq "if test -e '$gazeJournalPath'; then cat '$gazeJournalPath'; else echo __QPRO_NO_GAZE_JOURNAL__; fi") {
        if ($null -eq $fixture.Journal) { return '__QPRO_NO_GAZE_JOURNAL__' }
        return $fixture.Journal
    }
    if ($Command -eq "stat -c '%u:%a' '$gazeJournalPath'") { return $fixture.JournalMode }
    if ($Command -eq 'getprop ro.build.version.incremental') { return $fixture.Firmware }
    if ($Command -eq 'getprop debug.oculus.eye_tracking.social_filtering') { return $fixture.SocialFiltering }
    if ($Command -eq 'getprop ro.boot.serialno' -or $Command -eq 'getprop ro.serialno') { return $fixture.Serial }
    if ($Command -eq 'cat /proc/sys/kernel/random/boot_id') { return $fixture.BootId }
    if ($Command -eq "getprop $modelProperty") { return $fixture.Property }
    if ($Command -eq 'getprop init.svc.trackingservice') {
        if ($fixture.Running) { return 'running' }
        return 'stopped'
    }
    if ($Command -eq 'cat /proc/mounts') {
        $flags = if ($fixture.OdmWritable) { 'rw' } else { 'ro' }
        $lines = @("/dev/block/dm-13 /odm ext4 $flags,relatime 0 0")
        if ($fixture.RootAncestor) { $lines += "overlay $($fixture.RootAncestor) overlay ro 0 0" }
        if ($fixture.Mount) { $lines += "$remoteModel $targetModel ext4 rw,relatime 0 0" }
        if ($fixture.TransparentOverlay) { $lines += "overlay /odm/etc overlay $($fixture.OverlayOptions) 0 0" }
        return $lines -join "`n"
    }
    if ($Command.StartsWith('upper=/dev/mount_overlayfs/upper/odm;')) {
        if ($Command.Contains("scope='/proc/1234/root'") -and -not $fixture.ServiceUpperEmpty) { return '__QPRO_CHANGED_ODM_UPPER__' }
        if ($fixture.UpperEmpty) { return '__QPRO_EMPTY_ODM_UPPER__' }
        return '__QPRO_CHANGED_ODM_UPPER__'
    }
    if ($Command -eq "stat -c '%d:%i' '$remoteModel'" -or $Command -eq "sha256sum '$remoteModel'") {
        if (-not $fixture.SourceExists) { throw 'Fixture source missing' }
        if ($Command.StartsWith('stat')) { return $fixture.SourceIdentity }
        return "$($fixture.SourceHash)  $remoteModel"
    }
    if ($Command -eq "stat -c '%d:%i' '$targetModel'") {
        if ($fixture.ForeignMount) { return '999:888' }
        if ($fixture.Mount) { return $fixture.SourceIdentity }
        return '100:300'
    }
    if ($Command -eq "sha256sum '$targetModel'") {
        $hash = if ($fixture.ForeignMount -or $fixture.ModelChanged) { 'c' * 64 }
            elseif ($fixture.Mount) { $fixture.SourceHash } else { $fixture.OriginalHash }
        return "$hash  $targetModel"
    }
    if ($Command.StartsWith('umask 077;')) {
        if ($null -ne $fixture.Journal -or $fixture.Failure -eq 'journal-race') { throw 'Fixture journal already exists' }
        if ($Command -notmatch "printf '%s' '([A-Za-z0-9+/=]+)'" -or
            -not $Command.Contains("chmod 0600 '") -or -not $Command.Contains("&& ln '") -or -not $Command.EndsWith('&& sync')) {
            throw 'Journal was not committed privately and exclusively'
        }
        $fixture.Mutations.Add('journal')
        $fixture.Journal = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
        return ''
    }
    if ($Command -eq 'stop trackingservice') { $fixture.Mutations.Add('stop'); $fixture.Running = $false; return '' }
    if ($Command -eq 'start trackingservice') { $fixture.Mutations.Add('start'); $fixture.Running = $true; return '' }
    if ($Command -match "^setprop $([regex]::Escape($modelProperty)) '(.*)'$") {
        if ($fixture.Failure -eq 'property') { throw 'Fixture property write failed' }
        $fixture.Mutations.Add('property')
        if (-not $fixture.IgnorePropertyWrite) { $fixture.Property = $Matches[1] }
        return ''
    }
    if ($Command -eq "setprop $modelProperty false") {
        if ($fixture.Failure -eq 'property') { throw 'Fixture property write failed' }
        $fixture.Mutations.Add('property')
        if (-not $fixture.IgnorePropertyWrite) { $fixture.Property = 'false' }
        return ''
    }
    if ($Command -eq "umount '$targetModel'") {
        if ($fixture.Failure -eq 'unmount') { throw 'Fixture unmount failed' }
        $fixture.Mutations.Add('unmount'); $fixture.Mount = $false; return ''
    }
    if ($Command -eq "rm '$gazeJournalPath'") { $fixture.Mutations.Add('delete-journal'); $fixture.Journal = $null; return '' }
    if ($Command -eq "rm -f '$remoteModel'") { $fixture.Mutations.Add('delete-source'); $fixture.SourceExists = $false; return '' }
    throw "Unexpected fake ADB command: $Command"
}

function Wait-TrackingService {
    if (-not $fixture.Running) { throw 'Fixture tracking service did not restart' }
}

function Test-Recovery([string]$Name, [string]$Outcome, [string]$ErrorContains = '', [switch]$NoMutation) {
    $script:fixtureOutput = @()
    $failure = ''
    try { & { Invoke-GazeRecovery } 6>&1 | ForEach-Object { $script:fixtureOutput += $_.ToString() } }
    catch { $failure = $_.Exception.Message }
    Assert-Equal $true ($fixtureOutput -contains "QPRO_GAZE_RECOVERY $Outcome") "$Name machine outcome"
    Assert-Equal ($Outcome -eq 'unconfirmed') (-not [string]::IsNullOrWhiteSpace($failure)) "$Name failure status"
    if ($ErrorContains -and $failure -notlike "*$ErrorContains*") { throw "$Name lost its useful error: $failure" }
    if ($NoMutation) { Assert-Equal 0 $fixture.Mutations.Count "$Name leaves foreign/unknown state unchanged" }
    Write-Host "PASS: $Name"
}

function Test-GazeInspection([string]$Name, [bool]$RecordedSession, [int]$ModuleCount, [int]$MountCount) {
    $output = @(& { Show-GazeSetup } 6>&1 | ForEach-Object { $_.ToString() })
    $reports = @($output | Where-Object { $_.StartsWith('QPRO_GAZE_SETUP ') })
    Assert-Equal 1 $reports.Count "$Name has exactly one structured report"
    $report = $reports[0].Substring('QPRO_GAZE_SETUP '.Length) | ConvertFrom-Json
    Assert-Equal 1 $report.schema "$Name report schema"
    Assert-Equal $fixture.Firmware $report.firmware "$Name firmware"
    Assert-Equal $fixture.Property $report.experimentalSelection "$Name selector remains observational"
    Assert-Equal $fixture.SocialFiltering $report.socialFiltering "$Name social filtering"
    Assert-Equal $RecordedSession $report.qproSessionRecorded "$Name recorded session"
    Assert-Equal $ModuleCount @($report.magiskGazeModules).Count "$Name gaze module count"
    Assert-Equal $MountCount @($report.unverifiedMounts).Count "$Name unverified mount count"
    Assert-Equal $true ($reports[0] -match '"magiskGazeModules":\[') "$Name module field stays an array"
    Assert-Equal $true ($reports[0] -match '"unverifiedMounts":\[') "$Name mount field stays an array"
    Assert-Equal $true ($output -contains 'QPRO_GAZE_INSPECTION complete') "$Name completion marker retained"
    Assert-Equal 'QPRO_GAZE_INSPECTION complete' $output[-1] "$Name report precedes completion"
    Assert-Equal 0 $fixture.Mutations.Count "$Name inspection cannot change tracking"
    Write-Host "PASS: $Name"
    return $report
}

Reset-Fixture
$report = Test-GazeInspection 'Normal selector without a session is reported without a stock claim' $false 0 0
Assert-Equal $false $report.qproSessionRecorded 'No recovery record is invented'
Reset-Fixture 'false'; $fixture.Modules = @('questpro_independent_gaze'); $fixture.SocialFiltering = '0'
$report = Test-GazeInspection 'Magisk gaze remains visible with a false experimental selector' $false 1 0
Assert-Equal 'questpro_independent_gaze' $report.magiskGazeModules[0] 'Detected module id is preserved'
Reset-Fixture; Save-FixtureJournal; $fixture.Mount = $true; $fixture.Property = 'true'
$report = Test-GazeInspection 'Recorded Qpro session and its mount are reported separately' $true 0 2
Assert-Equal $true $report.qproSessionRecorded 'Journal presence is recorded without guessing whether it is active'
Reset-Fixture 'true'
$report = Test-GazeInspection 'Unrecorded experimental selector remains a legacy candidate observation' $false 0 0
Reset-Fixture; $fixture.RootAncestor = '/odm/etc'; $fixture.ServiceAncestor = '/odm/lib64'
$report = Test-GazeInspection 'Unverified root and service overlays are preserved in the report' $false 0 2
Reset-Fixture; $fixture.TransparentOverlay = $true
$report = Test-GazeInspection 'Verified empty OverlayFS does not become an unverified mount' $false 0 0

Reset-Fixture; $fixture.ServiceModelChanged = $true
$inspectionOutput = [Collections.Generic.List[string]]::new()
try {
    & { Show-GazeSetup } 6>&1 | ForEach-Object { $inspectionOutput.Add($_.ToString()) }
    throw 'Mismatched service model was accepted by inspection'
} catch { if ($_.Exception.Message -notlike '*different eye model*') { throw } }
Assert-Equal $false (@($inspectionOutput | Where-Object { $_.StartsWith('QPRO_GAZE_SETUP ') }).Count -gt 0) 'Unverified inspection has no success payload'
Assert-Equal $false ($inspectionOutput -contains 'QPRO_GAZE_INSPECTION complete') 'Unverified inspection cannot report completion'
Assert-Equal 0 $fixture.Mutations.Count 'Failed inspection leaves tracking unchanged'
Write-Host 'PASS: failed service verification cannot produce a completed inspection report'

foreach ($original in @('false', 'true', '', '0', '1')) {
    Reset-Fixture $original
    Save-FixtureJournal
    $fixture.Mount = $true; $fixture.Property = 'true'
    Test-Recovery "Interrupted session restores original '$original'" 'restored'
    Assert-Equal $original $fixture.Property 'Original property preserved'
    Assert-Equal $false $fixture.Mount 'Owned mount removed'
    Assert-Equal $null $fixture.Journal 'Journal removed only after verification'
}

Reset-Fixture; Save-FixtureJournal
$fixture.Property = 'true'; $fixture.BootId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
Test-Recovery 'Reboot removes mount but persistent property is recovered' 'restored'
Assert-Equal 'false' $fixture.Property 'Reboot property restored'
Assert-Equal $false ($fixture.Mutations -contains 'unmount') 'No blind unmount after reboot'

Reset-Fixture 'true'; Save-FixtureJournal
Test-Recovery 'Reboot preserves pre-existing true without restarting tracking' 'restored'
Assert-Equal 'true' $fixture.Property 'Pre-existing true preserved'
Assert-Equal $false ($fixture.Mutations -contains 'stop') 'No unnecessary tracking restart'

Reset-Fixture
Test-Recovery 'No journal and disabled property reports none' 'none' -NoMutation
Reset-Fixture 'true'
Test-Recovery 'Legacy unjournaled true is not overwritten' 'unconfirmed' 'without a Qpro recovery journal' -NoMutation
Reset-Fixture
$fixture.Mount = $true
Test-Recovery 'Unjournaled mount is not removed' 'unconfirmed' 'without a verified recovery journal' -NoMutation

Reset-Fixture; Save-FixtureJournal
$fixture.Mount = $true; $fixture.ForeignMount = $true; $fixture.Property = 'true'
Test-Recovery 'Foreign mount is not removed' 'unconfirmed' 'foreign eye-model mount' -NoMutation
Reset-Fixture; Save-FixtureJournal
$fixture.Serial = 'ANOTHERQUEST'
Test-Recovery 'Foreign device journal is rejected' 'unconfirmed' 'another headset' -NoMutation
Reset-Fixture; Save-FixtureJournal
$fixture.ModelChanged = $true
Test-Recovery 'Firmware/model replacement is rejected' 'unconfirmed' 'changed since' -NoMutation
Reset-Fixture; Save-FixtureJournal
$fixture.SourceIdentity = '100:201'
Test-Recovery 'Replaced temporary source is rejected' 'unconfirmed' 'changed or is missing' -NoMutation
Reset-Fixture; Save-FixtureJournal
$fixture.SourceExists = $false
Test-Recovery 'Missing source cannot authorize recovery' 'unconfirmed' 'source missing' -NoMutation
Reset-Fixture 'true'; Save-FixtureJournal
$fixture.Property = 'false'
Test-Recovery 'External property change is preserved' 'unconfirmed' 'changed outside' -NoMutation
Reset-Fixture; Save-FixtureJournal
$fixture.JournalMode = '2000:644'
Test-Recovery 'Non-private journal is rejected' 'unconfirmed' 'root-owned' -NoMutation
Reset-Fixture
$fixture.Journal = '{broken'
Test-Recovery 'Malformed JSON is rejected' 'unconfirmed' 'valid JSON' -NoMutation
Reset-Fixture
$journal.targetModel = "/odm/etc/eyetracking/runtime/models/../../unsafe/bolt.ptl"
Save-FixtureJournal
Test-Recovery 'Traversal target is rejected before shell use' 'unconfirmed' 'valid Qpro session' -NoMutation
Reset-Fixture
$journal.remoteModel = "/data/local/tmp/file'; reboot; '"
Save-FixtureJournal
Test-Recovery 'Injected source path is rejected before shell use' 'unconfirmed' 'valid Qpro session' -NoMutation
Reset-Fixture
$journal.ownerHost = 'd' * 64; Save-FixtureJournal
Test-Recovery 'Another PC/user cannot recover an unknown owner' 'unconfirmed' 'another PC or user' -NoMutation

foreach ($operation in @('property', 'unmount')) {
    Reset-Fixture; Save-FixtureJournal
    $fixture.Mount = $true; $fixture.Property = 'true'; $fixture.Failure = $operation
    Test-Recovery "Failed $operation retains journal and restarts service" 'unconfirmed' "Fixture $operation"
    Assert-Equal $true $fixture.Running 'Tracking service restarted after failure'
    Assert-Equal $true ($null -ne $fixture.Journal) 'Recovery record retained for retry'
    Assert-Equal $true $fixture.SourceExists 'Source retained for retry'
}
Reset-Fixture; Save-FixtureJournal
$fixture.Mount = $true; $fixture.Property = 'true'; $fixture.IgnorePropertyWrite = $true
Test-Recovery 'Silent property failure is caught by final verification' 'unconfirmed' 'could not be confirmed'
Assert-Equal $true ($null -ne $fixture.Journal) 'Journal retained after failed verification'

Reset-Fixture; Save-FixtureJournal
$fixture.Mount = $true; $fixture.Property = 'true'; $fixture.ServiceModelChanged = $true
Test-Recovery 'Service namespace mismatch refuses restoration before changing tracking' 'unconfirmed' 'different eye model' -NoMutation
Assert-Equal $true ($null -ne $fixture.Journal) 'Journal retained when trackingservice still sees another model'

Reset-Fixture; Save-FixtureJournal; $fixture.Mount = $true; $fixture.Property = 'true'; $fixture.ServiceUnpatched = $true
Test-Recovery 'Owned mount that never propagated into the service is safely removed' 'restored'

Reset-Fixture; Save-FixtureJournal; $fixture.Running = $false
Test-Recovery 'A stopped service from a failed restore can be restarted' 'restored'
Assert-Equal $true $fixture.Running 'Owned recovery restarts the stopped service'

function Test-LegacyReset([string]$Name, [string]$ErrorContains = '', [switch]$NoMutation) {
    $output = @(); $failure = ''
    try { $output = @(& { Reset-LegacyGazeSelection } 6>&1 | ForEach-Object { $_.ToString() }) }
    catch { $failure = $_.Exception.Message }
    Assert-Equal ([string]::IsNullOrWhiteSpace($ErrorContains)) ($output -contains 'QPRO_GAZE_LEGACY_RESET complete') "$Name machine outcome"
    if ($ErrorContains -and $failure -notlike "*$ErrorContains*") { throw "$Name lost useful refusal: $failure" }
    if ($NoMutation) { Assert-Equal 0 $fixture.Mutations.Count "$Name makes no tracking mutation" }
    Write-Host "PASS: $Name"
}

foreach ($legacy in @('true', '1')) {
    Reset-Fixture $legacy
    Test-LegacyReset "Explicit legacy '$legacy' reset chooses normal selection"
    Assert-Equal 'false' $fixture.Property 'Normal selection chosen deliberately'
    Assert-Equal $fixture.OriginalHash (Get-GazeFileHash $targetModel) 'Model bytes remain unchanged'
}
Reset-Fixture
Test-LegacyReset 'Already normal selector requires no service restart' -NoMutation
Reset-Fixture 'true'; $ConfirmLegacyReset = $false
Test-LegacyReset 'Missing explicit legacy confirmation refuses changes' 'requires confirmation' -NoMutation
Reset-Fixture 'true'; Save-FixtureJournal
Test-LegacyReset 'Recorded session cannot be replaced by legacy reset' 'recovery record exists' -NoMutation
Reset-Fixture 'true'; $fixture.Modules = @('questpro_independent_gaze')
Test-LegacyReset 'Enabled Magisk gaze blocks legacy reset' 'Another headset gaze method' -NoMutation
Reset-Fixture 'false'; $fixture.Modules = @('questpro_independent_gaze')
Test-LegacyReset 'Magisk gaze is detected even with normal selector' 'Another headset gaze method' -NoMutation
Reset-Fixture 'true'; $fixture.ScanIncomplete = $true
Test-LegacyReset 'Incomplete module scan cannot authorize a reset' 'did not complete' -NoMutation
Reset-Fixture 'true'; $fixture.RootAncestor = '/odm/etc'
Test-LegacyReset 'Ancestor model overlay blocks legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.ServiceAncestor = '/odm/lib64'
Test-LegacyReset 'Service-only engine overlay blocks legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.RootAncestor = '/odm'
Test-LegacyReset 'Whole firmware overlay is not mistaken for the stock partition' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.ServiceAncestor = '/odm'
Test-LegacyReset 'Service-only whole firmware overlay blocks legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.OdmWritable = $true
Test-LegacyReset 'Writable firmware partition blocks legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.ServiceModelChanged = $true
Test-LegacyReset 'Different service-visible model blocks legacy reset' 'different eye model' -NoMutation
Reset-Fixture 'true'; $fixture.TransparentOverlay = $true
Test-LegacyReset 'Verified empty OverlayFS can remain after gaze module is disabled'
Reset-Fixture 'true'; $fixture.TransparentOverlay = $true; $fixture.UpperEmpty = $false
Test-LegacyReset 'An OverlayFS upper entry blocks a legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.TransparentOverlay = $true; $fixture.ServiceUpperEmpty = $false
Test-LegacyReset 'A service-only OverlayFS upper entry blocks a legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.TransparentOverlay = $true; $fixture.ServiceOverlayFlags = 'rw'
Test-LegacyReset 'A writable service OverlayFS view cannot be treated as transparent' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.TransparentOverlay = $true; $fixture.OverlayOptions += ',upperdir=/foreign'
Test-LegacyReset 'Duplicate OverlayFS path options cannot hide a foreign upper' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.TransparentOverlay = $true; $fixture.OverlayOptions = $fixture.OverlayOptions.Replace('/worker/64781/2', '/worker/../foreign')
Test-LegacyReset 'Unexpected OverlayFS workdir blocks a legacy reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.Mount = $true
Test-LegacyReset 'Unrecorded bind is not removed by selector reset' 'overlay is active' -NoMutation
Reset-Fixture 'true'; $fixture.IgnorePropertyWrite = $true
Test-LegacyReset 'Failed legacy property verification cannot report success' 'could not be verified'
Reset-Fixture 'true'; $fixture.Failure = 'property'
Test-LegacyReset 'Failed legacy property write restarts trackingservice' 'property write failed'
Assert-Equal $true $fixture.Running 'Service restarted after legacy write failure'

Reset-Fixture ''
New-GazeJournal '' $fixture.SourceHash $fixture.OriginalHash
Assert-Equal $true $cleanupNeeded 'Committed journal enables own cleanup'
$created = $fixture.Journal | ConvertFrom-Json
Assert-Equal '' $created.originalProperty 'Empty original value recorded exactly'
Assert-Equal $false $fixture.Mount 'Journal exists before mount mutation'
Assert-Equal '' $fixture.Property 'Journal creation does not change the property'
Test-Recovery 'Canceled startup before mount restores its own recorded state' 'restored'

Reset-Fixture
$fixture.Failure = 'journal-race'
try { New-GazeJournal 'false' $fixture.SourceHash $fixture.OriginalHash; throw 'Concurrent journal commit was accepted' }
catch { if ($_.Exception.Message -notlike '*journal already exists*') { throw } }
Assert-Equal $false $cleanupNeeded 'Losing journal race does not claim the other session'
Assert-Equal 0 $fixture.Mutations.Count 'Losing journal race does not change tracking'
Write-Host 'PASS: exclusive journal commit refuses a concurrent owner'

Reset-Fixture
$fixture.ModelChanged = $true
try { New-GazeJournal 'false' $fixture.SourceHash $fixture.OriginalHash; throw 'Changed model was accepted' }
catch { if ($_.Exception.Message -notlike '*changed after compatibility checking*') { throw } }
Assert-Equal 0 $fixture.Mutations.Count 'A changed model cannot receive a recovery journal or tracking mutation'
Write-Host 'PASS: journal refuses a model changed after preflight'

$ownerStart = [Diagnostics.ProcessStartInfo]::new()
$ownerStart.FileName = Join-Path $PSHOME 'powershell.exe'
$ownerStart.Arguments = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 30"'
$ownerStart.UseShellExecute = $false; $ownerStart.CreateNoWindow = $true
$ownerProcess = [Diagnostics.Process]::Start($ownerStart)
try {
    Reset-Fixture
    $journal.ownerPid = $ownerProcess.Id.ToString()
    $journal.ownerStarted = $ownerProcess.StartTime.ToUniversalTime().Ticks.ToString()
    Save-FixtureJournal
    Test-Recovery 'Another live Qpro owner is not interrupted' 'unconfirmed' 'still running' -NoMutation
} finally {
    if (-not $ownerProcess.HasExited) { $ownerProcess.Kill(); $ownerProcess.WaitForExit() }
    $ownerProcess.Dispose()
}

Write-Host 'PASS: gaze recovery covers interruption, reboot, original values, ownership, concurrent sessions, malformed state and failed restoration.'
