$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-python.ps1')
$fixtureLocalAppData = Join-Path $PSScriptRoot 'artifacts\rocm-visibility-fixture'
$originalLocalAppData = $env:LOCALAPPDATA

# Exercise the launchers' real environment setup, candidate selection, fallback
# and finally restoration without running Python, ADB or any installer.
$fixtureNames = @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')
$originalVisibility = @{}
foreach ($fixtureName in $fixtureNames) {
    $originalVisibility[$fixtureName] = [Environment]::GetEnvironmentVariable($fixtureName, 'Process')
}

function Assert-Visibility([bool]$Cleared) {
    foreach ($fixtureName in $fixtureNames) {
        $value = [Environment]::GetEnvironmentVariable($fixtureName, 'Process')
        if ($Cleared -and $null -ne $value) { throw "$fixtureName was inherited by the ROCm probe" }
        if (-not $Cleared -and $value -ne 'fixture-original') { throw "$fixtureName was not restored before CPU/CUDA fallback" }
    }
}

try {
    $env:LOCALAPPDATA = $fixtureLocalAppData
    foreach ($launcher in @('build-and-run.ps1', 'train-latest-tongue-stills.ps1', 'train-latest-tongue-refinement.ps1')) {
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $launcher) -Raw
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw "$launcher contains PowerShell syntax errors" }
        $restoreFunction = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Restore-QproGpuVisibility'
        }, $true)
        $setupStart = $source.IndexOf('$qproGpuVisibilityNames =')
        $setup = $source.Substring($setupStart, $restoreFunction.Extent.EndOffset - $setupStart)
        $candidates = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$rocmCandidates'
        }, $true).Extent.Text
        $fixtureScriptRoot = "'" + $PSScriptRoot.Replace("'", "''") + "'"
        $candidates = $candidates.Replace('$PSScriptRoot', $fixtureScriptRoot)
        $selection = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.ForEachStatementAst] -and
            $node.Extent.Text.StartsWith('foreach ($candidate in $rocmCandidates)')
        }, $true).Extent.Text
        $fallbackVariable = if ($launcher -eq 'build-and-run.ps1') { '$rocmPython' } else { '$python' }
        $fallback = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.StartsWith("if (-not $fallbackVariable) {") -and
            $node.Extent.Text.Contains('Restore-QproGpuVisibility')
        }, $true).Extent.Text
        $fallback = $fallback.Replace('$PSScriptRoot', $fixtureScriptRoot)
        $finallyRestore = $ast.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.TryStatementAst] -and
            $null -ne $node.Finally
        }, $true) | ForEach-Object { $_.Finally.Statements } |
            Where-Object { $_.Extent.Text -eq 'Restore-QproGpuVisibility' } | Select-Object -First 1
        if ($null -eq $finallyRestore) { throw "$launcher has no finally restoration" }

        foreach ($scenario in @('rocm-ready', 'rocm-failed', 'no-rocm')) {
            & {
                foreach ($fixtureName in $fixtureNames) {
                    [Environment]::SetEnvironmentVariable($fixtureName, 'fixture-original', 'Process')
                }
                . ([scriptblock]::Create($setup))
                $script:fixtureCalls = 0
                $script:fixtureOutcome = if ($scenario -eq 'rocm-ready') { 0 } else { 1 }
                $script:fixtureInstalled = $scenario -ne 'no-rocm'
                function Test-Path { return $script:fixtureInstalled }
                function Test-QproTrainingPython {
                    param([string]$Candidate)
                    Assert-Visibility $false
                    return -not [string]::IsNullOrWhiteSpace($Candidate)
                }
                function Invoke-QproFixturePython {
                    Assert-Visibility $true
                    $script:fixtureCalls++
                    $global:LASTEXITCODE = $script:fixtureOutcome
                }
                $python = $null
                $rocmPython = $null
                . ([scriptblock]::Create($candidates))
                foreach ($candidate in $rocmCandidates) {
                    Set-Alias -Name $candidate.Python -Value Invoke-QproFixturePython
                }
                . ([scriptblock]::Create($selection))
                . ([scriptblock]::Create($fallback))
                if ($scenario -eq 'rocm-ready') {
                    Assert-Visibility $true
                    if ($script:fixtureCalls -ne 1) { throw 'Expected one successful ROCm probe' }
                } else {
                    Assert-Visibility $false
                    $expectedCalls = if ($scenario -eq 'no-rocm') { 0 } else { 2 }
                    if ($script:fixtureCalls -ne $expectedCalls) { throw 'Unexpected ROCm probe count' }
                }
                . ([scriptblock]::Create($finallyRestore.Extent.Text))
                Assert-Visibility $false
            }
            Write-Host "PASS: $launcher $scenario preserves GPU visibility outside ROCm"
        }
    }
} finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    foreach ($fixtureName in $fixtureNames) {
        [Environment]::SetEnvironmentVariable($fixtureName, $originalVisibility[$fixtureName], 'Process')
    }
}
