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
if (-not $validation -or -not $initial -or -not $forward) { throw 'Companion launcher argument path was not found.' }
$probe = [scriptblock]::Create(($ast.ParamBlock.Extent.Text, $validation.Extent.Text, $initial.Extent.Text, $forward.Extent.Text, ',$receiverArguments') -join "`n")

$standalone = & $probe
if (($standalone -join '|') -ne '.\receiver.py|--port|27273') { throw 'Standalone launcher arguments changed.' }
$owned = & $probe -CompanionPid 45678 -CompanionStartFileTime 134041739581234567
if (($owned -join '|') -ne '.\receiver.py|--port|27273|--companion-pid|45678|--companion-start-filetime|134041739581234567') {
    throw 'Owner identifiers were lost or creation-time precision changed.'
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
