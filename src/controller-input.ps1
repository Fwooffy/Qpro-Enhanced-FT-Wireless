param(
    [ValidateSet('install', 'uninstall', 'check', 'run')][string]$Action = 'install',
    [switch]$Hands,
    [switch]$Touchpad,
    [ValidateSet('trackpad', 'joystick', 'swipe', 'mouse')][string]$Mode = 'trackpad',
    [ValidateSet('left', 'right', 'both')][string]$Side = 'both',
    [ValidateRange(0.01, 5.0)][double]$SwipeGain = 0.22,
    [string]$StopFile = ''
)

$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('SwipeGain') -and ([double]::IsNaN($SwipeGain) -or [double]::IsInfinity($SwipeGain))) {
    throw 'Swipe sensitivity must be a finite number from 0.01 to 5.'
}
Write-Host "Optional controller action started: $Action."
. (Join-Path $PSScriptRoot 'runtime-python.ps1')
$python = $env:QPRO_PYTHON
if (-not (Test-QproPython312 $python)) {
    $python = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\python-3.12.10\python.exe'
}
if (-not (Test-QproPython312 $python)) {
    throw 'Install runtime in First-time setup before using the optional controller components.'
}
$arguments = @('-u', (Join-Path $PSScriptRoot 'controller-input\manage.py'), $Action, '--root', $PSScriptRoot)
if ($env:QPRO_ADB) { $arguments += @('--adb', $env:QPRO_ADB) }
if ($env:QPRO_ADB_TARGET) { $arguments += @('--target', $env:QPRO_ADB_TARGET) }
if ($Hands) { $arguments += '--hands' }
if ($Touchpad) { $arguments += '--touchpad' }
if ($StopFile) { $arguments += @('--stop-file', $StopFile) }
if ($Action -eq 'run') { $arguments += '--parent-stdin' }
$arguments += @('--mode', $Mode)
$arguments += @('--side', $Side.ToLowerInvariant())
if ($Action -eq 'run' -and $Mode -eq 'swipe' -and $PSBoundParameters.ContainsKey('SwipeGain')) {
    $arguments += @('--swipe-gain', $SwipeGain.ToString('R', [Globalization.CultureInfo]::InvariantCulture))
}
# No interactive PowerShell prompts, and no changes to a global Python install.
$ErrorActionPreference = 'Continue'
& $python @arguments
$code = $LASTEXITCODE
if ($code -ne 0) { throw "Optional controller action failed with code $code. Check the detailed Activity lines above." }
