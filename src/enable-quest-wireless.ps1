param(
    [string]$UsbSerial = "",
    [ValidateRange(1024, 65535)]
    [int]$Port = 5555
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $adbCommand = Get-Command adb -ErrorAction SilentlyContinue
    if ($null -eq $adbCommand) {
        $sdkAdb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
        if (-not (Test-Path -LiteralPath $sdkAdb)) {
            throw "adb.exe was not found. Install Android platform-tools or add adb to PATH."
        }
        $adb = $sdkAdb
    } else {
        $adb = $adbCommand.Source
    }

    if ([string]::IsNullOrWhiteSpace($UsbSerial)) {
        # Wrap the entire pipeline, not only adb's output. PowerShell otherwise
        # unwraps one result into a scalar string and $devices[0] becomes the
        # first character of the serial (for example "2").
        $devices = @(
            @(& $adb devices) | ForEach-Object {
                if ($_ -match '^([^\s:]+)\s+device$') { $Matches[1] }
            }
        )
        if ($devices.Count -ne 1) {
            throw "Connect exactly one Quest by USB, or pass -UsbSerial. Found $($devices.Count) USB devices."
        }
        $UsbSerial = [string]$devices[0]
    }

    # Both the relay and the provider injector require Magisk root. Prompt for
    # and verify that permission while USB is still connected, when recovering
    # from a denied Shell request is least confusing.
    $rootProbe = (& $adb -s $UsbSerial shell su -c id 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $rootProbe -notmatch 'uid=0\(root\)') {
        throw @"
Magisk has not granted superuser access to Android Shell.
On the headset, open Magisk > Superuser and enable the entry named Shell (or ADB Shell).
If no entry is visible, rerun this command while watching the headset and approve the prompt.
No wireless setting was changed. Do not install questcam-magisk.zip for this project.
"@
    }

    $route = (& $adb -s $UsbSerial shell ip -4 route get 1.1.1.1 2>&1) -join " "
    if ($LASTEXITCODE -ne 0 -or $route -notmatch '\bsrc\s+(?<ip>\d+\.\d+\.\d+\.\d+)') {
        throw "Could not determine the headset Wi-Fi address from $UsbSerial."
    }
    $ipAddress = $Matches.ip
    $target = "${ipAddress}:$Port"

    Write-Host "Switching the USB-authorized headset ADB daemon to TCP port $Port"
    & $adb -s $UsbSerial tcpip $Port
    if ($LASTEXITCODE -ne 0) { throw "Enabling ADB-over-Wi-Fi failed." }
    Start-Sleep -Seconds 2
    & $adb connect $target
    if ($LASTEXITCODE -ne 0) { throw "Connecting to $target failed." }
    $state = (& $adb -s $target get-state 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $state.Trim() -ne "device") {
        throw "The wireless headset did not reach the ADB device state: $target"
    }

    New-Item -ItemType Directory -Force .\config | Out-Null
    [ordered]@{
        adbTarget = $target
        configuredUtc = [DateTime]::UtcNow.ToString("o")
        transport = "adb-tcp"
    } | ConvertTo-Json | Set-Content -LiteralPath .\config\wireless-headset.json -Encoding utf8

    Write-Host "WIRELESS_ADB_READY $target"
    Write-Host "Use Launch-QproWireless.cmd to open Qpro Companion with this connection."
    Write-Warning "ADB is reachable on the local network until headset reboot or disable-quest-wireless.ps1. Use only a trusted private network."
} finally {
    Pop-Location
}
