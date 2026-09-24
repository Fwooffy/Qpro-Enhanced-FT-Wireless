param([string]$AdbTarget = "")

$ErrorActionPreference = "Stop"
$adb = Join-Path $PSScriptRoot "platform-tools\adb.exe"
$configPath = Join-Path $PSScriptRoot "config\wireless-headset.json"
if (-not (Test-Path -LiteralPath $adb)) {
    throw "Bundled platform-tools\adb.exe is missing. Extract the full ZIP again."
}

function Normalize-Target([string]$Value) {
    $Value = $Value.Trim()
    if ($Value -match '^\d{1,3}(?:\.\d{1,3}){3}$') { $Value += ":5555" }
    if ($Value -notmatch '^(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d{2,5})$') {
        throw "Enter the Quest's IPv4 address and port, for example 192.168.1.50:5555."
    }
    $ip = $Matches['ip']
    $port = [int]$Matches['port']
    $parsedIp = $null
    if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsedIp) -or
        $parsedIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
        $port -lt 1024 -or $port -gt 65535) {
        throw "The Quest address or port is invalid: $Value"
    }
    return "${ip}:$port"
}

if ([string]::IsNullOrWhiteSpace($AdbTarget) -and (Test-Path -LiteralPath $configPath)) {
    $savedTarget = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).adbTarget
    $enteredTarget = Read-Host "Quest Wi-Fi address (Enter for saved $savedTarget; include port if it changed)"
    $AdbTarget = if ([string]::IsNullOrWhiteSpace($enteredTarget)) { $savedTarget } else { $enteredTarget }
}
if ([string]::IsNullOrWhiteSpace($AdbTarget)) {
    $AdbTarget = Read-Host "Quest Wi-Fi address (IP or IP:port, default port 5555)"
}
$AdbTarget = Normalize-Target $AdbTarget

$ErrorActionPreference = "Continue"
$connectOutput = (& $adb connect $AdbTarget 2>&1) -join "`n"
$state = (& $adb -s $AdbTarget get-state 2>&1) -join "`n"
$stateExit = $LASTEXITCODE
if ($stateExit -ne 0 -or $state.Trim() -ne "device") {
    # ADB can report "already connected" while keeping a stale offline transport.
    $null = & $adb disconnect $AdbTarget 2>&1
    $connectOutput = (& $adb connect $AdbTarget 2>&1) -join "`n"
    $state = (& $adb -s $AdbTarget get-state 2>&1) -join "`n"
    $stateExit = $LASTEXITCODE
}
$ErrorActionPreference = "Stop"
if ($stateExit -ne 0 -or $state.Trim() -ne "device") {
    throw "Could not authorize $AdbTarget. Wake the Quest, approve its debugging prompt if shown, and check its current Wi-Fi IP and port. $connectOutput $state"
}

$ErrorActionPreference = "Continue"
$rootProbe = (& $adb -s $AdbTarget shell su -c id 2>&1) -join "`n"
$rootExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($rootExit -ne 0 -or $rootProbe -notmatch 'uid=0\(root\)') {
    throw "ADB connected, but Magisk has not granted root to Android Shell. Enable Shell/ADB Shell in Magisk and retry."
}

$configDirectory = Join-Path $PSScriptRoot "config"
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
[ordered]@{
    adbTarget = $AdbTarget
    configuredUtc = [DateTime]::UtcNow.ToString("o")
    transport = "wireless-adb"
} | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
Write-Host "Wireless Quest ready: $AdbTarget"
Write-Host "Now double-click Launch-QproWireless.cmd."
