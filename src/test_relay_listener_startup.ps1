$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'camera-transport-recovery.ps1')
$StopFile = ''; $CompanionPid = 0; $CompanionStartFileTime = 0
$script:checks = 0
function Assert-Listener([bool]$Value, [string]$Message) {
    $script:checks++
    if (-not $Value) { throw $Message }
}
function Test-ListenerCase([string[]]$Logs, [bool]$Exit, [string]$ExpectedFailure = '', [bool]$Cancel = $false) {
    $script:tick = 0; $script:reads = 0
    $process = [pscustomobject]@{HasExited=$Exit}
    $read = {
        $index = [Math]::Min($script:reads, $Logs.Count-1)
        $script:reads++
        return $Logs[$index]
    }
    $pause = { $script:tick += 100; if ($Cancel) { throw [OperationCanceledException]::new('cancelled by owner') } }
    $elapsed = { return $script:tick }
    $failure = ''
    try { Wait-QproRelayListening $process 'fake-output' 'fake-error' 27273 'all' 4000 $read $pause $elapsed }
    catch { $failure = $_.Exception.Message }
    if ($ExpectedFailure) { Assert-Listener ($failure.Contains($ExpectedFailure)) "Expected $ExpectedFailure; got $failure" }
    else { Assert-Listener ($failure -eq '') "Unexpected startup failure: $failure" }
    return @{Elapsed=$script:tick; Reads=$script:reads}
}
$ready = 'RELAY_LISTENING address=127.0.0.1 port=27273 mode=all max_fps=30'
$result = Test-ListenerCase @('* daemon started successfully *', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', '', $ready) $false
Assert-Listener ($result.Elapsed -eq 2200) 'Did not wait for actual delayed listener evidence'
$result = Test-ListenerCase @('* daemon started successfully *', 'not readiness') $false 'did not report its listener'
Assert-Listener ($result.Elapsed -eq 4000) 'Listener timeout did not use elapsed deadline'
$result = Test-ListenerCase @($ready) $true 'exited before listener'
Assert-Listener ($result.Elapsed -eq 0) 'Exited client did not fail promptly'
$null = Test-ListenerCase @('permission denied creating shared memory') $true 'permission denied'
$null = Test-ListenerCase @('') $false 'cancelled by owner' $true
$null = Test-ListenerCase @('RELAY_LISTENING address=127.0.0.1 port=9999 mode=all max_fps=30') $false 'did not report its listener'
$null = Test-ListenerCase @('daemon: RELAY_LISTENING address=127.0.0.1 port=27273 mode=all max_fps=30') $false 'did not report its listener'
$failure = ''
try { Wait-QproRelayListening $null 'fake' 'fake' 27273 'all' 4000 { 'adb process creation failed' } }
catch { $failure = $_.Exception.Message }
Assert-Listener ($failure.Contains('exited before listener') -and $failure.Contains('adb process creation failed')) 'Null child was not rejected promptly with diagnostics'
# Verify actual redirected-file reads and unique filenames isolate stale output.
$fixture = Join-Path (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'outputs') ('relay-listener-fixture-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixture)
$script:relayClientOutputPath = Join-Path $fixture 'old-output.txt'
$script:relayClientErrorPath = Join-Path $fixture 'old-error.txt'
[IO.File]::WriteAllText($relayClientOutputPath, $ready)
$old = $relayClientOutputPath
New-QproRelayClientLogs
Assert-Listener ($old -ne $relayClientOutputPath -and -not (Test-Path -LiteralPath $relayClientOutputPath)) 'Stale log filename was reused'
Assert-Listener ((Read-QproRelayClientLogs $relayClientOutputPath $relayClientErrorPath) -eq '') 'Previous listener leaked into fresh logs'
[IO.File]::WriteAllText($relayClientOutputPath, "* daemon started successfully *`n$ready`n")
[IO.File]::WriteAllText($relayClientErrorPath, 'adb daemon diagnostic')
Wait-QproRelayListening ([pscustomobject]@{HasExited=$false}) $relayClientOutputPath $relayClientErrorPath 27273 'all'
Assert-Listener ((Read-QproRelayClientLogs $relayClientOutputPath $relayClientErrorPath).Contains('adb daemon diagnostic')) 'Redirected diagnostics lost'
$summary = Join-Path $fixture 'summary.txt'
Complete-QproRelayClientLogs ([pscustomobject]@{HasExited=$false}) $summary
Assert-Listener ((Test-Path -LiteralPath $relayClientOutputPath) -and -not (Test-Path -LiteralPath $summary)) 'Deleted logs before writer reaped'
$lock = [IO.File]::Open($relayClientErrorPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Complete-QproRelayClientLogs ([pscustomobject]@{HasExited=$true}) $summary
    Assert-Listener (Test-Path -LiteralPath $relayClientErrorPath) 'Locked log was unexpectedly removed'
    $refused = $false
    try { New-QproRelayClientLogs } catch { $refused = $true }
    Assert-Listener $refused 'New relay obscured previous locked-log ownership'
} finally { $lock.Dispose() }
Complete-QproRelayClientLogs ([pscustomobject]@{HasExited=$true}) $summary
Assert-Listener (@($script:relayClientOwnedLogs).Count -eq 0 -and -not (Test-Path -LiteralPath $relayClientErrorPath)) 'Reaped writer logs not cleaned'
Assert-Listener ((Get-Content -LiteralPath $summary -Raw).Contains('adb daemon diagnostic')) 'Final relay summary lost useful diagnostics'
New-QproRelayClientLogs
Assert-Listener ([IO.Path]::GetDirectoryName($relayClientOutputPath).TrimEnd('\') -eq [IO.Path]::GetTempPath().TrimEnd('\')) 'Temporary relay files were placed in application directory'
[IO.File]::WriteAllText($relayClientErrorPath, 'startup failed before writer returned')
Complete-QproRelayClientLogs $null $summary
Assert-Listener (@($script:relayClientOwnedLogs).Count -eq 0 -and -not (Test-Path -LiteralPath $relayClientErrorPath)) 'Failed-start temporary log leaked'
# Both initial and replacement relay creation must terminate on Start-Process
# errors even though the surrounding launcher defaults to Continue.
foreach ($sourceFile in @('build-and-run.ps1', 'camera-transport-recovery.ps1')) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $sourceFile), [ref]$tokens, [ref]$errors)
    $starts = @($ast.FindAll({param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Start-Process' -and $node.Extent.Text -match '\$relayArguments'
    }, $true))
    Assert-Listener ($errors.Count -eq 0 -and $starts.Count -eq 1 -and $starts[0].Extent.Text -match '-ErrorAction Stop') "Relay Start-Process is not terminating in $sourceFile"
}
Write-Host "PASS $script:checks offline relay listener checks"
