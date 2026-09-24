param([string]$AdbTarget = "")

$ErrorActionPreference = "Stop"
$adb = Join-Path $PSScriptRoot "platform-tools\adb.exe"
$hub = Join-Path $PSScriptRoot "QproFaceTracking.exe"
if (-not (Test-Path -LiteralPath $adb) -or -not (Test-Path -LiteralPath $hub)) {
    throw "The Hub or bundled Android Platform-Tools are missing. Extract the full ZIP again."
}
if (Get-Process -Name QproFaceTracking -ErrorAction SilentlyContinue) {
    throw "Close the existing QproFaceTracking Hub before starting the wireless launcher."
}

if ([string]::IsNullOrWhiteSpace($AdbTarget)) {
    $configPath = Join-Path $PSScriptRoot "config\wireless-headset.json"
    if (-not (Test-Path -LiteralPath $configPath)) {
        throw "No wireless Quest is configured. Enable Wireless ADB in the headset and run Connect-QproWireless.cmd (or Pair-QproWireless.cmd) first."
    }
    $AdbTarget = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).adbTarget
}
if ($AdbTarget -notmatch '^\d{1,3}(?:\.\d{1,3}){3}:\d{2,5}$') {
    throw "Invalid wireless ADB target in config: $AdbTarget"
}

$ErrorActionPreference = "Continue"
$null = & $adb connect $AdbTarget 2>&1
$state = (& $adb -s $AdbTarget get-state 2>&1) -join "`n"
$ErrorActionPreference = "Stop"
if ($LASTEXITCODE -ne 0 -or $state.Trim() -ne "device") {
    throw "The Quest is unavailable at $AdbTarget. Enable Wireless ADB in the headset, then run Connect-QproWireless.cmd with its current IP and port."
}
$ErrorActionPreference = "Continue"
$rootProbe = (& $adb -s $AdbTarget shell su -c id 2>&1) -join "`n"
$rootExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($rootExit -ne 0 -or $rootProbe -notmatch 'uid=0\(root\)') {
    throw "Wireless ADB works, but Android Shell lacks Magisk root. Check Magisk > Superuser on the Quest."
}

$env:ANDROID_SERIAL = $AdbTarget
$env:QPRO_ADB_TARGET = $AdbTarget
$env:QPRO_ADB = $adb
Write-Host "Starting QproFaceTracking with wireless Quest $AdbTarget..."
Start-Process -FilePath $hub -WorkingDirectory $PSScriptRoot
