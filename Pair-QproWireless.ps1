param(
    [string]$PairingEndpoint = "",
    [string]$AdbTarget = ""
)

$ErrorActionPreference = "Stop"
$adb = Join-Path $PSScriptRoot "platform-tools\adb.exe"
if (-not (Test-Path -LiteralPath $adb)) {
    throw "Bundled platform-tools\adb.exe is missing. Extract the full ZIP again."
}
if ([string]::IsNullOrWhiteSpace($PairingEndpoint)) {
    $PairingEndpoint = Read-Host "Pairing IP:port shown on the headset"
}
if ($PairingEndpoint -notmatch '^(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d{2,5})$') {
    throw "Enter the exact pairing IP:port shown on the headset."
}
$pairIp = $Matches['ip']
$pairPort = [int]$Matches['port']
$parsedIp = $null
if (-not [System.Net.IPAddress]::TryParse($pairIp, [ref]$parsedIp) -or
    $parsedIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
    $pairPort -lt 1024 -or $pairPort -gt 65535) {
    throw "The pairing IP or port is invalid."
}
$pairingCode = Read-Host "Six-digit pairing code shown on the headset"
if ($pairingCode -notmatch '^\d{6}$') { throw "The pairing code must be six digits." }

$ErrorActionPreference = "Continue"
$pairOutput = (& $adb pair $PairingEndpoint $pairingCode 2>&1) -join "`n"
$pairExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($pairExit -ne 0 -or $pairOutput -notmatch 'Successfully paired') {
    throw "Pairing failed. Keep the pairing dialog open on the headset and try again. $pairOutput"
}
Write-Host $pairOutput
Write-Host "Use the regular Wireless debugging IP:port for the connection, not the temporary pairing port."
& (Join-Path $PSScriptRoot "Connect-QproWireless.ps1") -AdbTarget $AdbTarget
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
