param([switch]$Diagnose)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$adb = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_ADB) -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
    [System.IO.Path]::GetFullPath($env:QPRO_ADB)
} elseif (Test-Path -LiteralPath (Join-Path $root "platform-tools\adb.exe")) {
    Join-Path $root "platform-tools\adb.exe"
} else {
    Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
}
$python = if (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) { $env:QPRO_PYTHON } else { Join-Path $root ".venv\Scripts\python.exe" }
$fallbackPython = Join-Path $root ".venv\Scripts\qpro-python-console.exe"
if (-not (Test-Path -LiteralPath $python) -and (Test-Path -LiteralPath $fallbackPython)) { $python = $fallbackPython }
$helper = Join-Path $root "prepare_eye_model.py"

if (-not (Test-Path -LiteralPath $adb)) { throw "Android Platform Tools were not found. Re-extract the release so platform-tools\adb.exe is present." }
if (-not (Test-Path -LiteralPath $python)) { throw "Set up the PC runtime first." }
if (-not (Test-Path -LiteralPath $helper)) { throw "The local gaze preparation helper is missing. Reinstall the release package." }
if (-not (Test-Path -LiteralPath (Join-Path $root "research\patch_seacliff_independent_axes.py"))) {
    throw "The local gaze patcher is missing. Reinstall the release package."
}

$qproArguments = @($helper, '--adb', $adb)
if ($Diagnose) { $qproArguments += '--diagnose' }
& $python @qproArguments
if ($LASTEXITCODE -ne 0) {
    if ($Diagnose) { throw "The read-only headset compatibility check did not complete. Check the prerequisite reported above." }
    throw "Independent gaze preparation failed. Check the error above for the headset or firmware detail."
}
