param(
    [switch]$RestoreOnly,
    [switch]$RestoreIfActive,
    [switch]$InspectOnly,
    [switch]$ResetLegacySelection,
    [switch]$ConfirmLegacyReset,
    [switch]$Personalized,
    [switch]$Calibrate,
    [switch]$RuntimePreview,
    [switch]$VrcftOutput,
    [switch]$NoWindow,
    [string]$AdbTarget = "",
    [switch]$Wireless,
    [string]$CalibrationOutput = ".\calibration\qpro-independent-visual-axis-v2.json",
    [string]$OverlayPath = ".\third_party\BabbleCalibration-Windows-1.0.8\BabbleCalibration.x86_64.exe",
    [ValidateRange(20, 300)]
    [int]$GazeSeconds = 60,
    [ValidateRange(20, 300)]
    [int]$ConvergenceSeconds = 80,
    [ValidateRange(0, 30)]
    [int]$HeadlessSeconds = 0,
    [string]$StopFile = "",
    [switch]$DirectVrchat
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$adb = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_ADB) -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
    [System.IO.Path]::GetFullPath($env:QPRO_ADB)
} elseif (Test-Path -LiteralPath (Join-Path $root "platform-tools\adb.exe")) {
    Join-Path $root "platform-tools\adb.exe"
} else {
    Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
}
$hadAndroidSerial = Test-Path Env:ANDROID_SERIAL
$previousAndroidSerial = $env:ANDROID_SERIAL

function Resolve-WorkspacePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return "" }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

if ($Wireless -and [string]::IsNullOrWhiteSpace($AdbTarget)) {
    $wirelessConfig = Join-Path $root "config\wireless-headset.json"
    if (-not (Test-Path -LiteralPath $wirelessConfig)) {
        throw "No saved wireless headset exists. Run enable-quest-wireless.ps1 with USB connected first."
    }
    $AdbTarget = (Get-Content -LiteralPath $wirelessConfig -Raw | ConvertFrom-Json).adbTarget
}
$python = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) { $env:QPRO_PYTHON } else { Join-Path $root ".venv\Scripts\python.exe" }
$pythonFallback = Join-Path $root ".venv\Scripts\qpro-python-console.exe"
if (-not (Test-Path -LiteralPath $python) -and (Test-Path -LiteralPath $pythonFallback)) {
    $python = $pythonFallback
}
$localModel = Join-Path $root "research\seacliff_eye_model\bolt-independent-axes.ptl"
$modelManifest = Join-Path $root "research\seacliff_eye_model\bolt-independent-axes.manifest.json"
$modelPreflight = Join-Path $root "prepare_eye_model.py"
$gazeSessionId = [Guid]::NewGuid().ToString('N')
$remoteModel = "/data/local/tmp/qpro-seacliff-independent-axes-$gazeSessionId.ptl"
$targetModel = "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl"
$modelProperty = "persist.device_config.oculus_shared_vision.oculus_eyetracking_enable_experimental_model"
$gazeJournalPath = "/data/local/tmp/qpro-independent-gaze-session.json"
$inspectionAction = $RestoreOnly -or $RestoreIfActive -or $InspectOnly -or $ResetLegacySelection
$overlay = Resolve-WorkspacePath $OverlayPath
$calibrationOutputPath = Resolve-WorkspacePath $CalibrationOutput
$liveGazeOutput = $VrcftOutput -or $DirectVrchat

if ($VrcftOutput -and $DirectVrchat) { throw "Choose either -DirectVrchat or -VrcftOutput." }
if (-not (Test-Path -LiteralPath $adb)) { throw "ADB not found. Re-extract the release so platform-tools\adb.exe is present." }
if (-not $inspectionAction -and -not (Test-Path -LiteralPath $python)) { throw "Project Python environment not found under .venv\Scripts." }
if ($Calibrate -and -not $inspectionAction) {
    if (-not (Test-Path -LiteralPath $overlay)) { throw "BabbleCalibration not found: $overlay" }
    if (-not (Get-Process -Name "vrserver" -ErrorAction SilentlyContinue)) {
        throw "Start SteamVR before visual-axis calibration."
    }
}
if (($RuntimePreview -or $liveGazeOutput) -and -not $inspectionAction -and -not (Test-Path -LiteralPath $calibrationOutputPath)) {
    throw "Independent visual-axis calibration not found: $calibrationOutputPath"
}
if ($VrcftOutput -and -not $inspectionAction -and -not (Get-Process -Name "VRCFaceTracking" -ErrorAction SilentlyContinue)) {
    throw "Start VRCFaceTracking before enabling gaze-only output."
}

if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
    $env:ANDROID_SERIAL = $AdbTarget.Trim()
}

function Get-RootShellRequest([string]$Command) {
    # Windows PowerShell and adb both rebuild native arguments. Send complex
    # shell syntax as data so quotes, conditions and newlines reach root intact.
    $shellText = $Command.Replace("`r`n", "`n").Replace("`r", "`n") + "`n"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($shellText))
    return "printf %s $encoded | base64 -d | su -c sh"
}

function Invoke-Root([string]$Command, [switch]$AllowFailure) {
    # Windows PowerShell can promote a native process's stderr to a terminating
    # NativeCommandError when the script-wide preference is Stop. Capture the
    # process result first, then apply our own exit-code policy.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $adb shell (Get-RootShellRequest $Command) 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "Headset command failed: $Command`n$output"
    }
    return ($output | Out-String).Trim()
}

function Wait-TrackingService {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ((Invoke-Root "getprop init.svc.trackingservice" -AllowFailure) -eq "running") {
            Start-Sleep -Milliseconds 1500
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "The headset tracking service did not return to running state."
}

function Read-PreparedModelPath {
    if (-not (Test-Path -LiteralPath $modelManifest)) { return }
    $manifest = Get-Content -LiteralPath $modelManifest -Raw | ConvertFrom-Json
    $path = [string]$manifest.modelPath
    if ($path -notmatch '^/odm/etc/eyetracking/runtime/models/[A-Za-z0-9_./-]+/bolt\.ptl$' -or $path.Contains('..')) {
        throw "The prepared gaze manifest has an invalid headset model path. Prepare independent gaze again."
    }
    return $path
}

function Get-TargetMount {
    $mountTable = Invoke-Root "cat /proc/mounts"
    foreach ($line in ($mountTable -split "`r?`n")) {
        $fields = $line -split '\s+'
        if ($fields.Length -ge 2 -and $fields[1] -eq $targetModel) { return $line }
    }
    return ""
}

function Get-GazeDeviceSerial {
    $serial = Invoke-Root "getprop ro.boot.serialno"
    if ([string]::IsNullOrWhiteSpace($serial) -or $serial -eq "unknown") {
        $serial = Invoke-Root "getprop ro.serialno"
    }
    if ($serial -notmatch '^[A-Za-z0-9_.:-]{1,128}$' -or $serial -eq "unknown") {
        throw "The headset identity could not be verified for eye-model recovery. No tracking was changed."
    }
    return $serial
}

function Get-GazeModules {
    # Read policy files; never execute a Magisk script. An enabled module may
    # alter the ordinary model while the experimental selector remains false.
    $scan = @'
if ! test -d /data/adb/modules || ! test -r /data/adb/modules; then exit 1; fi
for d in /data/adb/modules/*; do
  test -d "$d" || continue
  test -r "$d/module.prop" || exit 1
  test -e "$d/disable" && continue
  gaze=0
  for f in "$d/module.prop" "$d/system.prop" "$d/service.sh" "$d/post-fs-data.sh"; do
    test -f "$f" || continue
    test -r "$f" || exit 1
    grep -qiE 'independent.*(eye|gaze)|eyetracking|eye_tracking|bolt[.]ptl|libtrackingengines|social_filtering' "$f"
    result=$?
    test "$result" -gt 1 && exit 1
    test "$result" -eq 0 && gaze=1
  done
  if test "$gaze" -eq 1; then printf '__QPRO_GAZE_MODULE__=%s\n' "${d##*/}"; fi
done
echo __QPRO_GAZE_MODULE_SCAN_COMPLETE__
'@
    $output = Invoke-Root $scan
    $lines = @($output -split "`r?`n" | Where-Object { $_ })
    if ($lines.Count -eq 0 -or $lines[-1] -ne '__QPRO_GAZE_MODULE_SCAN_COMPLETE__') {
        throw 'The Magisk gaze-module scan did not complete. No eye-model selection was changed.'
    }
    foreach ($line in $lines[0..($lines.Count - 1)]) {
        if ($line -eq '__QPRO_GAZE_MODULE_SCAN_COMPLETE__') { continue }
        if ($line -notmatch '^__QPRO_GAZE_MODULE__=([A-Za-z][A-Za-z0-9._-]{0,127})$') {
            throw 'The Magisk gaze-module scan returned an unknown result. No tracking was changed.'
        }
        $Matches[1]
    }
}

function Get-GazeServicePid {
    $servicePid = Invoke-Root 'pidof trackingservice'
    if ($servicePid -notmatch '^[1-9][0-9]{0,9}$') {
        throw 'A single running trackingservice could not be identified. Keep the Quest awake and retry.'
    }
    return $servicePid
}

function Get-GazeForeignMounts {
    $rootMounts = Invoke-Root 'cat /proc/mounts'
    $servicePid = Get-GazeServicePid
    $serviceMounts = Invoke-Root "cat /proc/$servicePid/mountinfo"
    $protected = @($targetModel, '/odm/lib64/libtrackingengines.so')
    foreach ($view in @(@{ Name = 'ADB'; Text = $rootMounts; Column = 1 }, @{ Name = 'trackingservice'; Text = $serviceMounts; Column = 4 })) {
        foreach ($line in ($view.Text -split "`r?`n")) {
            if (-not $line) { continue }
            $fields = $line -split '\s+'
            if ($fields.Length -le $view.Column) { throw 'The headset mount table is incomplete. No tracking was changed.' }
            $path = $fields[$view.Column]
            if ($path -eq '/') { continue }
            $fsType = ''; $options = ''; $mountFlags = ''; $source = ''; $mountRoot = '/'
            if ($view.Column -eq 1 -and $fields.Length -ge 4) {
                $fsType = $fields[2]; $options = $fields[3]; $mountFlags = $fields[3]; $source = $fields[0]
            }
            elseif ($view.Column -eq 4) {
                $separator = [Array]::IndexOf($fields, '-')
                if ($separator -ge 6 -and $fields.Length -gt $separator + 3) {
                    $fsType = $fields[$separator + 1]; $options = $fields[$separator + 3]
                    $mountFlags = $fields[5]; $source = $fields[$separator + 2]; $mountRoot = $fields[3]
                }
            }
            if (-not $fsType) { throw 'The headset mount table is incomplete. No tracking was changed.' }
            $optionList = @($options -split ',')
            $flagList = @($mountFlags -split ',')
            $readOnly = $flagList -contains 'ro' -and $flagList -notcontains 'rw' -and $optionList -notcontains 'rw'
            # Only the normal read-only firmware partition is exempt. A whole
            # /odm overlay or writable partition must remain a visible conflict.
            if ($path -eq '/odm' -and $fsType -eq 'ext4' -and $readOnly -and
                $mountRoot -eq '/' -and $source.StartsWith('/dev/block/', [StringComparison]::Ordinal)) { continue }
            # OverlayFS can remain installed after its gaze module is disabled.
            # Only the exact empty pass-through layer observed on this headset
            # is accepted; another lower layer or any upper entry is a conflict.
            if ($path -in @('/odm/etc', '/odm/lib64') -and $fsType -eq 'overlay' -and $readOnly -and
                @($optionList | Where-Object { $_.StartsWith('lowerdir=') }).Count -eq 1 -and
                @($optionList | Where-Object { $_.StartsWith('upperdir=') }).Count -eq 1 -and
                @($optionList | Where-Object { $_.StartsWith('workdir=') }).Count -eq 1 -and
                $optionList -contains 'lowerdir=/odm' -and
                $optionList -contains 'upperdir=/dev/mount_overlayfs/upper/odm' -and
                @($optionList | Where-Object { $_ -match '^workdir=/dev/mount_overlayfs/worker/[0-9]+/[0-9]+$' }).Count -eq 1) {
                # Directory names alone do not prove identical mount views.
                # Check the actual service-visible upper too, failing closed if
                # that directory is inaccessible in its mount namespace.
                $scope = if ($view.Name -eq 'trackingservice') { "/proc/$servicePid/root" } else { '' }
                $upperCheck = 'upper=/dev/mount_overlayfs/upper/odm; if ! test -d "$upper"; then test -r /data/adb/modules/magisk_overlayfs/module.prop && test ! -e /data/adb/modules/magisk_overlayfs/disable || exit 1; p=$(magisk --path) || exit 1; case "$p" in /debug_ramdisk|/sbin) upper="$p/overlayfs_mnt/upper/odm";; *) exit 1;; esac; fi; scope=__SCOPE__; upper="$scope$upper"; test -d "$upper" && test -r "$upper" && test -x "$upper" || exit 1; entries=$(find "$upper" -mindepth 1 -maxdepth 1 -print) || exit 1; if test -z "$entries"; then echo __QPRO_EMPTY_ODM_UPPER__; else echo __QPRO_CHANGED_ODM_UPPER__; fi'
                $empty = Invoke-Root ($upperCheck.Replace('__SCOPE__', "'$scope'"))
                if ($empty -eq '__QPRO_EMPTY_ODM_UPPER__') { continue }
            }
            foreach ($file in $protected) {
                if ($path -eq $file -or $file.StartsWith($path.TrimEnd('/') + '/', [StringComparison]::Ordinal)) {
                    "$($view.Name):$path"
                    break
                }
            }
        }
    }
}

function Assert-GazeServiceModel([string]$ExpectedHash, [string]$AlternateHash = '') {
    $servicePid = Get-GazeServicePid
    $servicePath = "/proc/$servicePid/root$targetModel"
    $serviceHash = Get-GazeFileHash $servicePath
    if ($serviceHash -ne $ExpectedHash -and $serviceHash -ne $AlternateHash) {
        throw 'The tracking service sees a different eye model than the ADB shell. Gaze restoration was not confirmed.'
    }
}

function Assert-NoExternalGazeMethod {
    $modules = @(Get-GazeModules)
    if ($modules.Count) {
        throw "Another headset gaze method is enabled in Magisk: $($modules -join ', '). Leave Independent Eye Gaze unchecked in the Hub, or disable that gaze module in Magisk and reboot before using the Hub method. No tracking was changed."
    }
    $mounts = @(Get-GazeForeignMounts)
    if ($mounts.Count) {
        throw "A model or tracking-engine overlay is active: $($mounts -join ', '). Disable its owning gaze method and reboot; Qpro will not overwrite it."
    }
    Assert-GazeServiceModel (Get-GazeFileHash $targetModel)
}

function Show-GazeSetup {
    $firmware = Invoke-Root 'getprop ro.build.version.incremental'
    $selection = Invoke-Root "getprop $modelProperty"
    $socialFiltering = Invoke-Root 'getprop debug.oculus.eye_tracking.social_filtering'
    Write-Host "Gaze firmware build: $firmware"
    Write-Host "Experimental eye-model selection: '$selection'"
    Write-Host "Social gaze filtering: '$socialFiltering'"
    $journal = Read-GazeJournal
    Write-Host $(if ($null -eq $journal) { 'No recorded Qpro gaze session.' } else { 'A recorded Qpro gaze session remains. Use Recover Qpro gaze after stopping its owning Hub.' })
    $modules = @(Get-GazeModules)
    if ($modules.Count) { Write-Host "Enabled Magisk gaze method: $($modules -join ', '). Hub Stop tracking does not disable it." }
    else { Write-Host 'No enabled Magisk gaze policy was found.' }
    $mounts = @(Get-GazeForeignMounts)
    if ($mounts.Count) { Write-Host "Model/engine mounts or overlays: $($mounts -join ', '). Their absence from an individual model mount check is not proof of original tracking." }
    else { Write-Host 'No unverified model/engine submounts were found. A verified empty OverlayFS layer may remain installed.' }
    Assert-GazeServiceModel (Get-GazeFileHash $targetModel)
    # Report observations separately from any decision about recovery. A recorded
    # session or normal selector alone cannot prove which gaze method is active.
    $setup = [ordered]@{
        schema = 1
        firmware = [string]$firmware
        experimentalSelection = [string]$selection
        qproSessionRecorded = ($null -ne $journal)
        magiskGazeModules = @($modules)
        unverifiedMounts = @($mounts)
        socialFiltering = [string]$socialFiltering
    }
    Write-Host ("QPRO_GAZE_SETUP " + ($setup | ConvertTo-Json -Compress -Depth 3))
    Write-Host 'QPRO_GAZE_INSPECTION complete'
}

function Reset-LegacyGazeSelection {
    if (-not $ConfirmLegacyReset) { throw 'Reset legacy gaze requires confirmation that you want the normal, nonexperimental eye-model selection.' }
    if ($null -ne (Read-GazeJournal)) { throw 'A Qpro recovery record exists. Use Recover Qpro gaze instead of a legacy reset.' }
    Assert-NoExternalGazeMethod
    $device = Get-GazeDeviceSerial
    $boot = Invoke-Root 'cat /proc/sys/kernel/random/boot_id'
    $before = Invoke-Root "getprop $modelProperty"
    if ($before -notin @('', 'false', '0', 'true', '1')) { throw 'The eye-model selection has an unknown value. No tracking was changed.' }
    $modelHash = Get-GazeFileHash $targetModel
    if ($before -in @('true', '1')) {
        # A legacy session has no original-state record. This is an explicit
        # choice of the normal selector, never an inferred original value.
        if ((Get-GazeDeviceSerial) -ne $device -or (Invoke-Root 'cat /proc/sys/kernel/random/boot_id') -ne $boot -or
            (Invoke-Root "getprop $modelProperty") -ne $before -or $null -ne (Read-GazeJournal)) {
            throw 'The headset gaze state changed during the check. No reset was applied.'
        }
        Assert-NoExternalGazeMethod
        Write-Host 'HEADSET_TRACKING_RESTART phase=legacy-reset status=begin'
        try {
            Invoke-Root 'stop trackingservice' | Out-Null
            Invoke-Root "setprop $modelProperty false" | Out-Null
        } finally { Invoke-Root 'start trackingservice' -AllowFailure | Out-Null }
        Wait-TrackingService
        Write-Host 'HEADSET_TRACKING_RESTART phase=legacy-reset status=running'
    }
    if ((Invoke-Root "getprop $modelProperty") -notin @('', 'false', '0') -or (Get-GazeFileHash $targetModel) -ne $modelHash) {
        throw 'The normal eye-model selection could not be verified. Check Activity before restarting gaze.'
    }
    Assert-NoExternalGazeMethod
    Write-Host 'The normal experimental-model selection is confirmed. This does not verify all factory tracking settings or uninstall the PC module.'
    Write-Host 'QPRO_GAZE_LEGACY_RESET complete'
}

function Get-GazeFileHash([string]$Path) {
    $hash = ((Invoke-Root "sha256sum '$Path'") -split '\s+')[0].ToLowerInvariant()
    if ($hash -notmatch '^[0-9a-f]{64}$') { throw "Could not verify the eye-model file hash. No tracking was changed." }
    return $hash
}

function Get-GazeOwnerHost {
    $bytes = [Text.Encoding]::UTF8.GetBytes([Environment]::MachineName + ':' + [Environment]::UserName)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}

function Assert-GazeOwnerStopped($Journal) {
    if ($Journal.ownerHost -ne (Get-GazeOwnerHost)) {
        throw "The recorded Qpro session belongs to another PC or user. Recovery ownership could not be confirmed; no tracking was changed."
    }
    try { $owner = [Diagnostics.Process]::GetProcessById([int]$Journal.ownerPid) }
    catch [ArgumentException] { return }
    try {
        if ($owner.HasExited -or $owner.StartTime.ToUniversalTime().Ticks.ToString() -ne $Journal.ownerStarted) { return }
        # The creating PowerShell's finally may restore its own session. A
        # second Hub must not recover a session whose owner is still running.
        if ($owner.Id -ne $PID) { throw "The recorded Qpro gaze process is still running. Stop its owning Hub before recovery; no tracking was changed." }
    } finally { $owner.Dispose() }
}

function Read-GazeJournal {
    $text = Invoke-Root "if test -e '$gazeJournalPath'; then cat '$gazeJournalPath'; else echo __QPRO_NO_GAZE_JOURNAL__; fi"
    if ($text -eq "__QPRO_NO_GAZE_JOURNAL__") { return $null }
    if ((Invoke-Root "stat -c '%u:%a' '$gazeJournalPath'") -ne "0:600") {
        throw "The eye-model recovery journal is not a private root-owned Qpro file. No tracking was changed."
    }
    if ($text.Length -gt 4096) { throw "The eye-model recovery journal is invalid. No tracking was changed." }
    try { $journal = $text | ConvertFrom-Json }
    catch { throw "The eye-model recovery journal is not valid JSON. No tracking was changed." }
    foreach ($name in @('format', 'owner', 'sessionId', 'ownerHost', 'ownerPid', 'ownerStarted', 'deviceSerial', 'bootId', 'targetModel', 'remoteModel', 'modelProperty', 'sourceHash', 'sourceIdentity', 'originalModelHash', 'originalProperty')) {
        if ($null -eq $journal.PSObject.Properties[$name] -or $journal.$name -isnot [string]) {
            throw "The eye-model recovery journal has missing or invalid fields. No tracking was changed."
        }
    }
    if ($journal.format -ne 'qpro-headset-gaze-session-v1' -or $journal.owner -ne 'QproFaceTracking' -or
        $journal.sessionId -notmatch '^[0-9a-f]{32}$' -or $journal.deviceSerial -notmatch '^[A-Za-z0-9_.:-]{1,128}$' -or
        $journal.ownerHost -notmatch '^[0-9a-f]{64}$' -or $journal.ownerPid -notmatch '^[1-9][0-9]{0,9}$' -or [long]$journal.ownerPid -gt [int]::MaxValue -or
        $journal.ownerStarted -notmatch '^[0-9]{1,19}$' -or
        $journal.bootId -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' -or
        $journal.targetModel -notmatch '^/odm/etc/eyetracking/runtime/models/[A-Za-z0-9_./-]+/bolt\.ptl$' -or $journal.targetModel.Contains('..') -or
        $journal.remoteModel -ne "/data/local/tmp/qpro-seacliff-independent-axes-$($journal.sessionId).ptl" -or $journal.modelProperty -ne $modelProperty -or
        $journal.sourceHash -notmatch '^[0-9a-f]{64}$' -or $journal.originalModelHash -notmatch '^[0-9a-f]{64}$' -or
        $journal.sourceIdentity -notmatch '^\d+:\d+$' -or $journal.originalProperty -notin @('', 'false', 'true', '0', '1')) {
        throw "The eye-model recovery journal is not a valid Qpro session. No tracking was changed."
    }
    if ($journal.deviceSerial -ne (Get-GazeDeviceSerial)) {
        throw "The eye-model recovery journal belongs to another headset. No tracking was changed."
    }
    return $journal
}

function New-GazeJournal([string]$OriginalProperty, [string]$PatchedHash, [string]$ExpectedOriginalHash) {
    if ($OriginalProperty -notin @('', 'false', 'true', '0', '1') -or $PatchedHash -notmatch '^[0-9a-f]{64}$' -or $ExpectedOriginalHash -notmatch '^[0-9a-f]{64}$') {
        throw "The original eye-model state could not be recorded safely. No tracking was changed."
    }
    if ($null -ne (Read-GazeJournal) -or -not [string]::IsNullOrWhiteSpace((Get-TargetMount))) {
        throw "An eye-model session is already active. Recover it before starting another gaze session."
    }
    $identity = Invoke-Root "stat -c '%d:%i' '$remoteModel'"
    if ($identity -notmatch '^\d+:\d+$' -or (Get-GazeFileHash $remoteModel) -ne $PatchedHash) {
        throw "The temporary eye-model source could not be verified. No tracking was changed."
    }
    $bootId = Invoke-Root "cat /proc/sys/kernel/random/boot_id"
    if ($bootId -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') {
        throw "The headset boot identity could not be verified. No tracking was changed."
    }
    if ((Invoke-Root "getprop $modelProperty") -ne $OriginalProperty) {
        throw "The headset eye-model selection changed during startup. No tracking was changed."
    }
    $originalHash = Get-GazeFileHash $targetModel
    if ($originalHash -ne $ExpectedOriginalHash) {
        throw "The headset eye model changed after compatibility checking. No tracking was changed."
    }
    $journal = [PSCustomObject]@{
        format = 'qpro-headset-gaze-session-v1'; owner = 'QproFaceTracking'
        ownerHost = Get-GazeOwnerHost; ownerPid = $PID.ToString(); ownerStarted = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks.ToString()
        sessionId = $gazeSessionId; deviceSerial = Get-GazeDeviceSerial
        bootId = $bootId; targetModel = $targetModel; remoteModel = $remoteModel; modelProperty = $modelProperty
        sourceHash = $PatchedHash; sourceIdentity = $identity; originalModelHash = $originalHash
        originalProperty = $OriginalProperty
    }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($journal | ConvertTo-Json -Compress)))
    $temporary = $gazeJournalPath + '.' + $journal.sessionId + '.tmp'
    # Commit the restore state before any tracking change. A hard process stop
    # or reboot can remove the mount but leave the persist.* property enabled.
    Invoke-Root "umask 077; if test -e '$gazeJournalPath'; then exit 1; fi; printf '%s' '$encoded' | base64 -d > '$temporary' && chown root:root '$temporary' && chmod 0600 '$temporary' && ln '$temporary' '$gazeJournalPath' && rm '$temporary' && sync" | Out-Null
    $script:cleanupNeeded = $true
    $saved = Read-GazeJournal
    if ($null -eq $saved -or $saved.sessionId -ne $journal.sessionId) {
        throw "The eye-model recovery journal could not be committed. No tracking was changed."
    }
}

function Restore-StockModel($Journal) {
    Assert-GazeOwnerStopped $Journal
    $script:targetModel = $Journal.targetModel
    $script:remoteModel = $Journal.remoteModel
    $sourceIdentity = Invoke-Root "stat -c '%d:%i' '$remoteModel'"
    if ($sourceIdentity -ne $Journal.sourceIdentity -or (Get-GazeFileHash $remoteModel) -ne $Journal.sourceHash) {
        throw "The recorded temporary eye-model source has changed or is missing. Recovery was not applied."
    }
    $mounted = -not [string]::IsNullOrWhiteSpace((Get-TargetMount))
    if ($mounted) {
        if ((Invoke-Root "stat -c '%d:%i' '$targetModel'") -ne $Journal.sourceIdentity -or
            (Get-GazeFileHash $targetModel) -ne $Journal.sourceHash) {
            throw "A foreign eye-model mount is active. Remove it through its own module; Qpro did not change it."
        }
    } elseif ((Get-GazeFileHash $targetModel) -ne $Journal.originalModelHash) {
        throw "The headset eye model changed since the recorded Qpro session. Recovery was not applied."
    }
    $property = Invoke-Root "getprop $modelProperty"
    if ($property -ne $Journal.originalProperty -and $property -ne 'true') {
        throw "The headset eye-model selection changed outside the recorded Qpro session. Recovery was not applied."
    }
    $serviceState = Invoke-Root 'getprop init.svc.trackingservice'
    if ($serviceState -eq 'running') {
        # Refuse a conflicting service namespace before changing its selector.
        # A prior failed restart may leave the service stopped; in that case
        # validate the owned root state here and its service view after restart.
        $expectedServiceHash = if ($mounted) { $Journal.sourceHash } else { $Journal.originalModelHash }
        # A mount that never propagated into the service can still be removed
        # safely when the service sees the journal's unchanged original bytes.
        $alternateHash = if ($mounted) { $Journal.originalModelHash } else { '' }
        Assert-GazeServiceModel $expectedServiceHash $alternateHash
    } elseif ($serviceState -ne 'stopped') {
        throw 'The tracking service state is unknown. Gaze recovery was not applied.'
    }
    if ($mounted -or $property -ne $Journal.originalProperty -or $serviceState -eq 'stopped') {
        Write-Host "HEADSET_TRACKING_RESTART phase=restore status=begin"
        try {
            Invoke-Root "stop trackingservice" | Out-Null
            Invoke-Root "setprop $modelProperty '$($Journal.originalProperty)'" | Out-Null
            if ($mounted) { Invoke-Root "umount '$targetModel'" | Out-Null }
        } finally {
            # A failed property/unmount operation retains the journal for retry.
            Invoke-Root "start trackingservice" -AllowFailure | Out-Null
        }
        Wait-TrackingService
        Write-Host "HEADSET_TRACKING_RESTART phase=restore status=running"
    }
    if (-not [string]::IsNullOrWhiteSpace((Get-TargetMount)) -or
        (Invoke-Root "getprop $modelProperty") -ne $Journal.originalProperty -or
        (Get-GazeFileHash $targetModel) -ne $Journal.originalModelHash) {
        throw "The recorded pre-Qpro eye-model state could not be confirmed. Keep the Hub open and check Activity."
    }
    Assert-GazeServiceModel $Journal.originalModelHash
    Invoke-Root "rm '$gazeJournalPath'" | Out-Null
    Invoke-Root "rm -f '$remoteModel'" -AllowFailure | Out-Null
}

function Invoke-GazeRecovery {
    try {
        $journal = Read-GazeJournal
        if ($null -ne $journal) {
            Restore-StockModel $journal
            Write-Host "Recorded pre-Qpro eye-model state restored. Other modules were not disabled."
            Write-Host "QPRO_GAZE_RECOVERY restored"
            return
        }
        if (-not [string]::IsNullOrWhiteSpace((Get-TargetMount))) {
            throw "An eye-model mount exists without a verified recovery journal. Qpro did not remove it or change the headset selection."
        }
        $property = Invoke-Root "getprop $modelProperty"
        if ($property -notin @('', 'false', '0')) {
            throw "The experimental eye-model selection is enabled without a Qpro recovery journal. It may belong to Magisk or an interrupted older session. Qpro did not change it; stock gaze cannot be confirmed."
        }
        Write-Host 'No recorded Qpro gaze session remains. This does not prove factory tracking or disable other headset methods.'
        $modules = @(Get-GazeModules)
        if ($modules.Count) { Write-Host "Enabled Magisk gaze method remains: $($modules -join ', '). Stop tracking does not turn it off; disable it in Magisk and reboot to compare original headset tracking." }
        Write-Host "QPRO_GAZE_RECOVERY none"
    } catch {
        Write-Host "QPRO_GAZE_RECOVERY unconfirmed"
        throw
    }
}

try {
    & $adb get-state | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No authorized Quest was found over ADB." }
    $rootProbe = & $adb shell su -c id 2>&1
    if ($LASTEXITCODE -ne 0 -or ($rootProbe -join "`n") -notmatch 'uid=0\(root\)') {
        throw "Magisk root is not granted to Android Shell. On the headset open Magisk > Superuser and enable Shell (or ADB Shell), then retry."
    }
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        Write-Host "Independent-eye ADB target: $AdbTarget"
    }

    $preparedPath = ""
    try { $preparedPath = Read-PreparedModelPath }
    catch {
        if (-not $inspectionAction) { throw }
        Write-Host "Prepared gaze manifest is invalid; checking the known Qpro mount path for cleanup."
    }
    if (-not [string]::IsNullOrWhiteSpace($preparedPath)) { $targetModel = $preparedPath }

    if ($InspectOnly) { Show-GazeSetup; exit 0 }
    if ($ResetLegacySelection) { Reset-LegacyGazeSelection; exit 0 }

    if ($RestoreOnly) {
        Invoke-GazeRecovery
        exit 0
    }
    if ($RestoreIfActive) {
        Invoke-GazeRecovery
        exit 0
    }
    if ($null -ne (Read-GazeJournal)) {
        throw "A recorded Qpro gaze session remains. Stop its owning Hub first, or use gaze recovery after that process has ended. No new tracking was started."
    }
    $existingMount = Get-TargetMount
    if (-not [string]::IsNullOrWhiteSpace($existingMount)) {
        throw "An eye-model mount is already active. Close its viewer with Q and wait for restoration. If the Qpro process is gone, use -RestoreOnly to remove a verified Qpro mount; other modules must remove their own mount."
    }
    Assert-NoExternalGazeMethod
    if (-not (Test-Path -LiteralPath $localModel)) {
        throw "Patched research model not found: $localModel"
    }
    if (-not (Test-Path -LiteralPath $modelManifest)) {
        throw "The prepared gaze manifest is missing. Use First-time setup to prepare independent gaze again."
    }
    if (-not (Test-Path -LiteralPath $modelPreflight)) {
        throw "The gaze compatibility check is missing. Re-extract the complete Qpro release."
    }
    Write-Host "Checking prepared eye model and headset firmware before changing tracking..."
    & $python $modelPreflight --check-prepared --adb $adb
    if ($LASTEXITCODE -ne 0) {
        throw "Independent gaze cannot start safely on this headset. See the compatibility reason above, then prepare independent gaze again if the headset was updated. No headset tracking was changed."
    }
    Write-Host "Prepared eye model matches this headset and supported tracking engine."

    $originalProperty = Invoke-Root "getprop $modelProperty"
    if ($originalProperty -notin @('', 'false', '0')) {
        throw "The experimental eye-model selection is already enabled without a recorded Qpro session. It may belong to Magisk or an interrupted older Qpro version. No new tracking was applied; identify that method before starting Qpro gaze."
    }
    $cleanupNeeded = $false
    try {
    & $adb push $localModel $remoteModel | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the research model to the headset." }
    Invoke-Root "chown root:root '$remoteModel'" | Out-Null
    Invoke-Root "chmod 0644 '$remoteModel'" | Out-Null
    Invoke-Root "chcon u:object_r:vendor_configs_file:s0 '$remoteModel'" | Out-Null
    $localHash = (Get-FileHash -LiteralPath $localModel -Algorithm SHA256).Hash.ToLowerInvariant()
    $preparedManifest = Get-Content -LiteralPath $modelManifest -Raw | ConvertFrom-Json
    if ($localHash -ne $preparedManifest.patchedSha256) { throw "The prepared eye-model patch changed during startup. No tracking was changed." }
    New-GazeJournal $originalProperty $localHash ([string]$preparedManifest.sourceSha256)
    Write-Host "QPRO_GAZE_SESSION applied"
    Invoke-Root "mount --bind '$remoteModel' '$targetModel'" | Out-Null

    $remoteHash = ((Invoke-Root "sha256sum '$targetModel'") -split "\s+")[0].ToLowerInvariant()
    if ($localHash -ne $remoteHash) { throw "The temporary model failed its headset hash check." }
    Assert-GazeServiceModel $localHash

    Invoke-Root "setprop $modelProperty true" | Out-Null
    Write-Host "HEADSET_TRACKING_RESTART phase=apply status=begin"
    Invoke-Root "stop trackingservice" | Out-Null
    Invoke-Root "start trackingservice" | Out-Null
    Wait-TrackingService
    Assert-GazeServiceModel $localHash
    Write-Host "HEADSET_TRACKING_RESTART phase=apply status=running"
    Write-Host "Temporary independent Meta gaze branch active. Q restores the recorded pre-Qpro eye state."

    if ($RuntimePreview -or $liveGazeOutput) {
        $runtimeArguments = @(
            (Join-Path $root "independent_visual_axis_runtime.py"),
            "--adb", $adb,
            "--calibration", $calibrationOutputPath
        )
        if ($liveGazeOutput) {
            $outputSwitch = if ($DirectVrchat) { "--direct-vrchat" } else { "--output-vrcft" }
            $runtimeArguments += @($outputSwitch, "--sample-timeout-seconds", "20")
        }
        if ($NoWindow) { $runtimeArguments += "--no-window" }
        if ($HeadlessSeconds -gt 0) {
            $runtimeArguments += @("--headless-seconds", $HeadlessSeconds)
        }
        if (-not [string]::IsNullOrWhiteSpace($StopFile)) {
            $runtimeArguments += @(
                "--stop-file",
                (Resolve-WorkspacePath $StopFile)
            )
        }
        & $python @runtimeArguments
    }
    elseif ($Calibrate) {
        & $python (Join-Path $root "native_raw_eye_probe.py") `
            --adb $adb `
            --title "Quest Pro independent visual-axis calibration" `
            --notice "TEMPORARY MODEL OVERRIDE - Meta personalization after local branch; Q restores stock" `
            --calibration-overlay $overlay `
            --calibration-output $calibrationOutputPath `
            --gaze-seconds $GazeSeconds `
            --convergence-seconds $ConvergenceSeconds
    }
    elseif ($Personalized) {
        & $python (Join-Path $root "native_raw_eye_probe.py") `
            --adb $adb `
            --title "Quest Pro independent personalized visual axes" `
            --notice "TEMPORARY MODEL OVERRIDE - Meta per-eye calibration after local branch; Q restores stock" `
            --instruction "Test a fixed target around the center and corners, then drift or close one eye; the other ray must remain fixed."
    }
    else {
        & $python (Join-Path $root "native_eye_stage_probe.py") `
            --adb $adb `
            --title "Quest Pro Meta local-branch gaze test" `
            --notice "TEMPORARY MODEL OVERRIDE - stock model is restored when Q quits" `
            --instruction "Close one eye or drift only the right eye; the two bottom axes should now remain independent."
    }
    if ($LASTEXITCODE -ne 0) { throw "The local-branch gaze viewer failed." }
    }
    finally {
        if ($cleanupNeeded) {
            Invoke-GazeRecovery
        }
    }
}
finally {
    if ($hadAndroidSerial) {
        $env:ANDROID_SERIAL = $previousAndroidSerial
    }
    else {
        Remove-Item Env:ANDROID_SERIAL -ErrorAction SilentlyContinue
    }
}
