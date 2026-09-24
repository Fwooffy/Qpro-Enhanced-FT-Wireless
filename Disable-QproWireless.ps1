param([string]$AdbTarget = "")

$ErrorActionPreference = "Stop"
$adb = Join-Path $PSScriptRoot "platform-tools\adb.exe"
$configPath = Join-Path $PSScriptRoot "config\wireless-headset.json"
if (-not (Test-Path -LiteralPath $adb)) { throw "Bundled platform-tools\adb.exe is missing." }
if ([string]::IsNullOrWhiteSpace($AdbTarget)) {
    if (-not (Test-Path -LiteralPath $configPath)) {
        throw "No saved wireless Quest. Pass -AdbTarget, or turn off Wireless ADB in the headset settings."
    }
    $AdbTarget = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).adbTarget
}
if ($AdbTarget -notmatch '^\d{1,3}(?:\.\d{1,3}){3}:\d{2,5}$') {
    throw "Invalid wireless ADB target: $AdbTarget"
}

& $adb -s $AdbTarget usb
if ($LASTEXITCODE -ne 0) {
    throw "Could not switch the Quest back to USB mode. Rebooting the headset also ends this wireless ADB session."
}
$null = & $adb disconnect $AdbTarget 2>&1
if (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath }
Write-Host "Wireless ADB disabled on $AdbTarget. USB debugging remains available."
