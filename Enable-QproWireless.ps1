param(
    [string]$UsbSerial = "",
    [ValidateRange(1024, 65535)]
    [int]$Port = 5555
)

$ErrorActionPreference = "Stop"
$adb = Join-Path $PSScriptRoot "platform-tools\adb.exe"
if (-not (Test-Path -LiteralPath $adb)) {
    throw "Bundled platform-tools\adb.exe is missing. Extract the full ZIP again."
}

if ([string]::IsNullOrWhiteSpace($UsbSerial)) {
    $usbDevices = @(
        & $adb devices | ForEach-Object {
            if ($_ -match '^([^\s:]+)\s+device(?:\s|$)') { $Matches[1] }
        }
    )
    if ($usbDevices.Count -ne 1) {
        throw "Connect exactly one authorized Quest by USB, or pass -UsbSerial. Found $($usbDevices.Count) USB devices."
    }
    $UsbSerial = [string]$usbDevices[0]
}

$ErrorActionPreference = "Continue"
$rootProbe = (& $adb -s $UsbSerial shell su -c id 2>&1) -join "`n"
$rootExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($rootExit -ne 0 -or $rootProbe -notmatch 'uid=0\(root\)') {
    throw "Magisk root is not granted to Android Shell. Approve Shell (or ADB Shell) in Magisk, then retry. Wireless ADB has not been enabled."
}

$ErrorActionPreference = "Continue"
$route = (& $adb -s $UsbSerial shell ip -4 route get 1.1.1.1 2>&1) -join " "
$routeExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($routeExit -ne 0 -or $route -notmatch '\bsrc\s+(?<ip>\d+\.\d+\.\d+\.\d+)') {
    throw "Could not read the Quest's Wi-Fi address. Connect the PC and Quest to the same trusted Wi-Fi network."
}
$ipAddress = $Matches['ip']
$target = "${ipAddress}:$Port"

Write-Host "Switching $UsbSerial to wireless ADB at $target..."
& $adb -s $UsbSerial tcpip $Port
if ($LASTEXITCODE -ne 0) { throw "The Quest did not enable ADB over Wi-Fi." }

$connected = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    Start-Sleep -Seconds 2
    $ErrorActionPreference = "Continue"
    $null = & $adb connect $target 2>&1
    $state = (& $adb -s $target get-state 2>&1) -join "`n"
    $ErrorActionPreference = "Stop"
    if ($LASTEXITCODE -eq 0 -and $state.Trim() -eq "device") {
        $connected = $true
        break
    }
}
if (-not $connected) {
    throw "Could not reach $target. Check that PC and Quest are on the same Wi-Fi and that your router allows devices to talk to each other."
}

$ErrorActionPreference = "Continue"
$wirelessRoot = (& $adb -s $target shell su -c id 2>&1) -join "`n"
$wirelessRootExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($wirelessRootExit -ne 0 -or $wirelessRoot -notmatch 'uid=0\(root\)') {
    throw "Wireless ADB connected, but Magisk did not grant root over that connection. Approve the Shell prompt in the headset, then rerun this helper."
}

$configDirectory = Join-Path $PSScriptRoot "config"
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
[ordered]@{
    adbTarget = $target
    configuredUtc = [DateTime]::UtcNow.ToString("o")
    transport = "adb-tcp"
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $configDirectory "wireless-headset.json") -Encoding UTF8

Write-Host "Wireless ADB ready: $target"
Write-Host "Unplug USB, then double-click Launch-QproWireless.cmd."
Write-Warning "ADB port $Port is reachable on the local network until disabled or the headset reboots. Use a trusted private Wi-Fi network."
