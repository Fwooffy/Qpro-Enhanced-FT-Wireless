$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build-and-run.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }

# Exercise only parameter binding and argument construction. Do not run the
# launcher, load installed runtimes, start ADB or connect to a headset.
$validation = $ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('if (($CompanionPid -gt 0)') }
$initial = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$receiverArguments' -and $node.Operator -eq 'Equals'
}, $true)
$forward = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($CompanionPid -gt 0)')
}, $true)
$directArgument = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($DirectVrchat) { $receiverArguments')
}, $true)
$sourceArgument = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($DirectVrchat -or $PSBoundParameters.ContainsKey(') -and $node.Extent.Text.Contains('--label-source')
}, $true)
if (-not $validation -or -not $initial -or -not $forward -or -not $directArgument -or -not $sourceArgument) { throw 'Companion launcher argument path was not found.' }
$probe = [scriptblock]::Create(($ast.ParamBlock.Extent.Text, $validation.Extent.Text, $initial.Extent.Text, $directArgument.Extent.Text, $sourceArgument.Extent.Text, $forward.Extent.Text, ',$receiverArguments') -join "`n")

$standalone = & $probe
if (($standalone -join '|') -ne '.\receiver.py|--port|27273') { throw 'Standalone launcher arguments changed.' }
$owned = & $probe -CompanionPid 45678 -CompanionStartFileTime 134041739581234567
if (($owned -join '|') -ne '.\receiver.py|--port|27273|--companion-pid|45678|--companion-start-filetime|134041739581234567') {
    throw 'Owner identifiers were lost or creation-time precision changed.'
}
foreach ($source in @('VirtualDesktop', 'SteamLink')) {
    $backup = & $probe -TrackingSource $source.ToLowerInvariant()
    if (($backup -join '|') -ne ".\receiver.py|--port|27273|--label-source|$source") {
        throw "VRCFaceTracking backup label source $source did not reach the receiver."
    }
    $direct = & $probe -DirectVrchat -TrackingSource $source.ToLowerInvariant()
    if (($direct -join '|') -ne ".\receiver.py|--port|27273|--direct-vrchat|--label-source|$source") {
        throw "Direct VRChat label source $source did not reach the receiver."
    }
}
foreach ($arguments in @(@{ CompanionPid = 45678 }, @{ CompanionStartFileTime = [long]134041739581234567 })) {
    $rejected = $false
    try { $null = & $probe @arguments } catch {
        if (-not $_.Exception.Message.Contains('must be supplied together')) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A partial owner identity was accepted.' }
}
Write-Host 'Companion launcher: standalone, exact FILETIME forwarding and incomplete identity checks passed.'

# Exercise the production policy and admission guards with no legacy config,
# module helper or VRCFaceTracking process. This intentionally omits ADB.
$launcherBody = ($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] }).Body.Statements
$guards = @($launcherBody | Where-Object { $_.Extent.Text -match '^if \(-not \$DirectVrchat\)|^\$(recordEnabled|labelsEnabled|steamLabelsEnabled|steamVrRequired|vrcftRequired) =|^if \(\$TrackingSource -eq|^if \(\$(steamLabelsEnabled|vrcftRequired|steamVrRequired|DirectVrchat)(\)| -and)' -and
    -not $_.Extent.Text.Contains('$receiverArguments') -and -not $_.Extent.Text.Contains('Camera worker for') })
if ($guards.Count -ne 11) { throw 'Launcher preflight guards were not found.' }
$admission = [scriptblock]::Create((@($ast.ParamBlock.Extent.Text, '$PSScriptRoot = $env:LOCALAPPDATA') +
    @($guards | ForEach-Object { $_.Extent.Text }) + '$labelsEnabled, $steamVrRequired, $vrcftRequired') -join "`n")
function Test-Path { param($LiteralPath, $PathType) return $false }
function Get-Process {
    param($Name, $ErrorAction)
    if ($Name -ne 'vrserver') { throw "Unexpected process check: $Name" }
    if ($script:fixtureSteamVrRunning) { return $true }
}
$fixtureSteamVrRunning = $true

foreach ($source in @('VirtualDesktop', 'SteamLink')) {
    $capture = & $admission -DirectVrchat -TrackingSource $source -TongueStillCalibration
    if (($capture -join '|') -ne 'True|True|False') { throw "Direct capture routing was incorrect for $source." }
    $pupil = & $admission -DirectVrchat -TrackingSource $source -PupilOutput
    if (($pupil -join '|') -ne 'False|True|False') { throw "Direct pupil routing was incorrect for $source." }
}

function Assert-AdmissionFailure([scriptblock]$Attempt, [string]$Message) {
    try { $null = & $Attempt } catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw }
        return
    }
    throw "Expected launcher admission failure: $Message"
}
Assert-AdmissionFailure { & $admission -TrackingSource SteamLink -TongueStillCalibration } 'module is configured for virtual-desktop'
Assert-AdmissionFailure { & $admission -TrackingSource VirtualDesktop -TonguePreview } 'module check helper is missing'
Assert-AdmissionFailure { & $admission -DirectVrchat -TrackingSource SteamLink -TongueStillCalibration -LabelsPort 27280 } 'labels use port 27274'
Assert-AdmissionFailure { & $admission -DirectVrchat -TrackingSource SteamLink -EyeCalibration } 'provide combined gaze'
Assert-AdmissionFailure { & $admission -TrackingSource SteamLink -EyeCalibration } 'provide combined gaze'
Assert-AdmissionFailure { & $admission -TrackingSource SteamLink -HybridPreview } 'provide combined gaze'
$fixtureSteamVrRunning = $false
Assert-AdmissionFailure { & $admission -DirectVrchat -TrackingSource SteamLink -TongueStillCalibration } 'Start SteamVR first'
Write-Host 'Direct VRChat launcher: capture, pupil, Steam Link label port, SteamVR requirement and legacy module checks passed.'
