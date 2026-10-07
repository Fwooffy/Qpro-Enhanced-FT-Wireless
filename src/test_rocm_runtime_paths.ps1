param([string]$Python = '')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-python.ps1')

function Assert-Equal($Expected, $Actual, [string]$Context) {
    if ($Expected -ne $Actual) { throw "$Context expected '$Expected', got '$Actual'" }
}
function Assert-Contains([string]$Actual, [string]$Expected, [string]$Context) {
    if (-not $Actual.Contains($Expected)) { throw "$Context lost '$Expected': $Actual" }
}

# Load the installer helpers only. No GPU probes, registry changes or downloads.
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-QproRocm.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Get-QproRocmPipFailure', 'Invoke-QproRocmPip')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}

$previousStorageOverride = $env:QPRO_ROCM_HOME
$previousPythonPath = $env:PYTHONPATH
$env:QPRO_ROCM_HOME = $null
$fixtureRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ('.r-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$expectedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\') + '\'
if (-not [System.IO.Path]::GetFullPath($fixtureRoot).StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped the workspace.' }
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

function Add-FixtureEnvironment([string]$Path, [string]$Tier = 'experimental-rocm-10') {
    New-Item -ItemType Directory -Path (Join-Path $Path 'Scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Path 'Scripts\python.exe') -Value 'fixture, not executable'
    @{ schema = 1; supportTier = $Tier; rocmVersion = if ($Tier -eq 'experimental-rocm-10') { '10.0.0' } else { '7.2.1' }; gfxTarget = 'gfx1100'; python = Join-Path $Path 'Scripts\python.exe' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Path 'qpro-rocm-ready.json')
}

try {
    $reportedRelease = 'C:\Fixtures\A PC\Documents\Qpro enhanced face tracking\QproFaceTracking V2.1.0 Fix Test\QproRuntime'
    $reportedEnv = Join-Path $reportedRelease '.venv-rocm-experimental'
    $compact = Get-QproRocmInstallEnvironment '10' 'gfx1100' '' 'C:\Fixtures\A PC\AppData\Local'
    Assert-Equal 'C:\Fixtures\A PC\AppData\Local\QproFaceTracking\r\10-gfx1100' $compact 'Reported PC automatic storage'
    Assert-QproRocmPathBudget $compact
    try {
        Assert-QproRocmPathBudget $reportedEnv
        throw 'The reported long release path was incorrectly accepted.'
    } catch { Assert-Contains $_.Exception.Message 'shorter Qpro-only location' 'Long path preflight' }

    $local = Join-Path $fixtureRoot 'a'
    $release = Join-Path $fixtureRoot 'release'
    $shortStore = Get-QproRocmStorageRoot '' $local
    $latest = Join-Path $shortStore '10-gfx1100'
    $legacy = Join-Path $shortStore '721-gfx1100'
    $oldLatest = Join-Path $release '.venv-rocm-experimental'
    $oldLegacy = Join-Path $release '.venv-rocm'
    foreach ($path in @($latest, $oldLatest)) { Add-FixtureEnvironment $path }
    foreach ($path in @($legacy, $oldLegacy)) { Add-FixtureEnvironment $path 'amd-windows-7.2.1' }
    Register-QproRocmEnvironment $legacy $true $local
    Register-QproRocmEnvironment $latest $false $local
    $candidates = @(Get-QproRocmCandidates $release $local)
    Assert-Equal 4 $candidates.Count 'Shared and local candidates deduplicate'
    Assert-Equal $latest $candidates[0].EnvironmentRoot 'Latest compact environment selected first'
    Assert-Equal $oldLatest $candidates[1].EnvironmentRoot 'Old latest remains fallback'
    Assert-Equal $legacy $candidates[2].EnvironmentRoot 'Legacy compact follows latest'
    Assert-Equal $oldLegacy $candidates[3].EnvironmentRoot 'Old legacy remains fallback'
    Assert-Equal 'custom' $candidates[2].TargetFamily 'Legacy family routing'
    Assert-Equal $null $candidates[0].TargetFamily 'Latest family routing'
    Assert-Equal $legacy (Read-QproRocmRuntimeIndex $local).legacyEnvironment 'Register preserves other tier'

    $custom = Join-Path $fixtureRoot '10-gfx1100-1234abcd'
    Add-FixtureEnvironment $custom
    Register-QproRocmEnvironment $custom $false $local
    Assert-Equal $custom (Get-QproRocmInstallEnvironment '10' 'gfx1100' '' $local) 'Repeated install reuses routed recovery slot'
    Assert-Equal $custom @(Get-QproRocmCandidates $release $local)[0].EnvironmentRoot 'Custom storage reused by runtime consumers'
    Assert-Equal $latest (Read-QproRocmRuntimeIndex $local).latestFallbackEnvironments[0] 'Previous environment remains a routed fallback'
    $nextCustom = Join-Path $fixtureRoot 'second\10-gfx1100-5678abcd'
    Add-FixtureEnvironment $nextCustom
    Register-QproRocmEnvironment $nextCustom $false $local
    $updatedCandidates = @(Get-QproRocmCandidates $release $local)
    Assert-Equal $nextCustom $updatedCandidates[0].EnvironmentRoot 'Verified update gets priority'
    Assert-Equal $custom $updatedCandidates[1].EnvironmentRoot 'Custom old runtime outside default storage remains fallback'
    Assert-Equal $true (Test-Path -LiteralPath (Join-Path $custom 'qpro-rocm-ready.json')) 'Register keeps previous readiness'
    Assert-Equal (Join-Path $shortStore '10-gfx1031') (Get-QproRocmInstallEnvironment '10' 'gfx1031' '' $local) 'Changing GPU uses separate target environment'
    Assert-Equal (Join-Path $fixtureRoot 'explicit\10-gfx1100') (Get-QproRocmInstallEnvironment '10' 'gfx1100' (Join-Path $fixtureRoot 'explicit') $local) 'Explicit storage overrides prior route'
    Set-Content -LiteralPath (Get-QproRocmRuntimeIndexPath $local) -Value '{broken'
    Assert-Equal $latest @(Get-QproRocmCandidates $release $local)[0].EnvironmentRoot 'Damaged index still finds compact fallback'

    # Real environment initialization with a fake base interpreter. Keep every
    # previous folder and mark only the independently created replacement owned.
    $broken = Join-Path $fixtureRoot '10-gfx1100'
    New-Item -ItemType Directory -Path $broken | Out-Null
    Set-Content -LiteralPath (Join-Path $broken 'keep.txt') -Value 'original'
    $script:fixtureCreateCount = 0
    function Test-QproPython312 { param([string]$Python) return (Test-Path -LiteralPath $Python -PathType Leaf) }
    function Invoke-FixtureVenv {
        param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
        Assert-Equal '-m' $Arguments[0] 'Private venv module option'
        Assert-Equal 'venv' $Arguments[1] 'Private venv module'
        $path = $Arguments[2]
        $script:fixtureCreateCount++
        New-Item -ItemType Directory -Path (Join-Path $Path 'Scripts') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $Path 'Scripts\python.exe') -Value 'fixture'
        $global:LASTEXITCODE = 0
    }
    $replacement = Initialize-QproRocmEnvironment $broken 'Invoke-FixtureVenv'
    Assert-Equal 'original' (Get-Content -LiteralPath (Join-Path $broken 'keep.txt') -Raw).Trim() 'Broken environment retained'
    Assert-Equal $false ($replacement -eq $broken) 'Replacement uses independent slot'
    Assert-Equal $replacement (Initialize-QproRocmEnvironment $replacement 'Invoke-FixtureVenv') 'Working owned replacement reused'
    Assert-Equal 1 $script:fixtureCreateCount 'Reuse does not reinstall private base'
    Add-FixtureEnvironment $replacement
    $updateReplacement = Initialize-QproRocmEnvironment $replacement 'Invoke-FixtureVenv' -ForceReplacement
    Assert-Equal $false ($updateReplacement -eq $replacement) 'Update stages beside an owned working environment'
    Assert-Equal $true (Test-Path -LiteralPath (Join-Path $replacement 'qpro-rocm-ready.json')) 'Update retains the prior readiness marker'
    Assert-Equal 2 $script:fixtureCreateCount 'Update creates exactly one replacement environment'

    $script:fixtureExit = 1
    $script:fixtureOutput = "ERROR: OSError [Errno 2] missing hipblaslt library`nHINT: This system does not have Windows Long Path support enabled."
    function Invoke-FixturePip {
        param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
        Write-Output $script:fixtureOutput
        $global:LASTEXITCODE = $script:fixtureExit
    }
    try {
        Invoke-QproRocmPip 'Invoke-FixturePip' @('install', 'fixture') 'AMD install failed.' $reportedEnv
        throw 'Long-path pip failure was incorrectly accepted.'
    } catch {
        Assert-Contains $_.Exception.Message 'path-length failure' 'Long-path pip classification'
        Assert-Contains $_.Exception.Message $reportedEnv 'Long-path pip retains environment'
        Assert-Equal $false $_.Exception.Message.Contains('may not be published') 'Path failure does not blame GPU package availability'
    }
    $script:fixtureOutput = 'ERROR: No matching distribution found for amd-torch-device-gfx1031==fixture'
    try {
        Invoke-QproRocmPip 'Invoke-FixturePip' @('install', 'fixture') 'AMD install failed.' $latest
        throw 'Missing-wheel pip failure was incorrectly accepted.'
    } catch { Assert-Contains $_.Exception.Message 'index did not provide the requested wheel' 'Missing-wheel classification' }
    $script:fixtureOutput = 'ERROR: [WinError 5] Access is denied'
    try {
        Invoke-QproRocmPip 'Invoke-FixturePip' @('install', 'fixture') 'AMD install failed.' $latest
        throw 'Filesystem pip failure was incorrectly accepted.'
    } catch { Assert-Contains $_.Exception.Message 'filesystem failure' 'Other pip failure classification' }
    $script:fixtureExit = 0
    $script:fixtureOutput = 'fixture install completed'
    Invoke-QproRocmPip 'Invoke-FixturePip' @('install', 'fixture') 'AMD install failed.' $latest
    Assert-Equal 'Stop' $ErrorActionPreference 'Pip wrapper restores error handling'
    if (-not [string]::IsNullOrWhiteSpace($Python)) {
        # Exercise the native PowerShell 5.1 stderr path, using a local fake pip
        # module that exits immediately. It cannot install or download anything.
        $pipFixture = Join-Path $fixtureRoot 'pipfixture'
        New-Item -ItemType Directory -Path $pipFixture | Out-Null
        @'
import sys
print('QPRO_FAKE_PIP: long-path stderr fixture', file=sys.stderr, flush=True)
print('ERROR: OSError [Errno 2] missing hipblaslt library', file=sys.stderr, flush=True)
print('HINT: This system does not have Windows Long Path support enabled.', file=sys.stderr, flush=True)
sys.exit(1)
'@ | Set-Content -LiteralPath (Join-Path $pipFixture 'pip.py') -Encoding UTF8
        $env:PYTHONPATH = $pipFixture
        try {
            Invoke-QproRocmPip $Python @('--qpro-long-path-fixture') 'AMD install failed.' $reportedEnv
            throw 'Native stderr long-path failure was incorrectly accepted.'
        } catch { Assert-Contains $_.Exception.Message 'path-length failure' 'Native stderr long-path classification' }
    }
    Write-Host 'PASS: compact ROCm path budget, indexed/custom/legacy resolution, non-destructive replacement and precise pip failures'
} finally {
    $env:QPRO_ROCM_HOME = $previousStorageOverride
    $env:PYTHONPATH = $previousPythonPath
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
