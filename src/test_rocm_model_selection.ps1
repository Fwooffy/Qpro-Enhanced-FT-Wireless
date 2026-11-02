param([string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts\rocm-model-selection'))

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-QproRocm.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('Get-QproRocmModelPair', 'Assert-QproRocmModelPairUnchanged')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $definition) { throw "Missing model preflight helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$fixture = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('fixture-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$checks = 0
function Check([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:checks++
}
function Write-Text([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Add-Pair([string]$Root, [int]$Version, [string]$Text = 'bundled') {
    foreach ($branch in @('gate', 'direction')) { Write-Text (Join-Path $Root "models\qpro-stereo-tongue-v$Version-$branch.pt") "$Text $branch" }
}
function Manifest([string]$Root, $Stem = 'qpro-stereo-tongue-v15', [string]$Version = '3.0.0') {
    Write-Text (Join-Path $Root 'release-manifest.json') (@{ name='QproFaceTracking'; version=$Version; bundledTongueModel=$Stem } | ConvertTo-Json -Depth 4)
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Developer-Metadata([string]$Root, [int]$Version, [bool]$Owned = $true) {
    $stem = "qpro-stereo-tongue-v$Version"
    Write-Text (Join-Path $Root "models\$stem.metadata.json") (@{
        modelKind='developer-tongue'; origin='bundled developer model'; bundledDeveloperModel=$Owned
        modelFiles=@{gateSha256=(Hash (Join-Path $Root "models\$stem-gate.pt")); directionSha256=(Hash (Join-Path $Root "models\$stem-direction.pt"))}
    } | ConvertTo-Json -Depth 4)
}
function Must-Fail([scriptblock]$Action, [string]$Text, [string]$Context) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Check ($null -ne $caught -and $caught.Contains($Text)) "$Context did not report '$Text': $caught"
}

# Source-only fixtures do not need a runnable Python or GPU environment.
$current = Join-Path $fixture 'current'
Manifest $current
Add-Pair $current 15
Add-Pair $current 9 'Mustachio'
Add-Pair $current 99 'personal'
Developer-Metadata $current 15
$pair = Get-QproRocmModelPair $current
Check ($pair.Stem -ceq 'qpro-stereo-tongue-v15') 'Current release selected an optional/personal model.'
Assert-QproRocmModelPairUnchanged $pair
Check ($true) 'Unchanged current bundle failed validation.'

$legacy = Join-Path $fixture 'legacy'
Add-Pair $legacy 8 'legacy'
Check ((Get-QproRocmModelPair $legacy).Stem -ceq 'qpro-stereo-tongue-v8') 'Manifest-free legacy v8 pair was rejected.'
Manifest $legacy 'qpro-stereo-tongue-v8' '2.1.2'
Check ((Get-QproRocmModelPair $legacy).Stem -ceq 'qpro-stereo-tongue-v8') 'Declared pre-V3 bundle was rejected.'

$missing = Join-Path $fixture 'missing'
Manifest $missing
Add-Pair $missing 8 'old'
Add-Pair $missing 9 'optional'
Add-Pair $missing 99 'personal'
Must-Fail { Get-QproRocmModelPair $missing } 'complete bundled tongue model' 'Missing v15 must not select another version'
Add-Pair $missing 15
Developer-Metadata $missing 15
$direction = Join-Path $missing 'models\qpro-stereo-tongue-v15-direction.pt'
[IO.File]::Delete($direction)
Must-Fail { Get-QproRocmModelPair $missing } 'complete bundled tongue model' 'Missing direction'
Write-Text $direction ''
Must-Fail { Get-QproRocmModelPair $missing } 'complete bundled tongue model' 'Empty direction'
[IO.File]::Delete($direction)
[IO.Directory]::CreateDirectory($direction) | Out-Null
Must-Fail { Get-QproRocmModelPair $missing } 'complete bundled tongue model' 'Directory in place of checkpoint'

foreach ($bad in @('../outside', '..\outside', 'C:\outside', '\\host\outside', 'qpro-stereo-tongue-v15.pt', ' qpro-stereo-tongue-v15', @('qpro-stereo-tongue-v15'), @{path='qpro-stereo-tongue-v15'})) {
    Manifest $current $bad
    Must-Fail { Get-QproRocmModelPair $current } 'valid bundled tongue model' 'Unsafe/non-string manifest stem'
}
Write-Text (Join-Path $current 'release-manifest.json') '{'
Must-Fail { Get-QproRocmModelPair $current } 'manifest could not be read' 'Malformed manifest'
Write-Text (Join-Path $current 'release-manifest.json') '{"name":"QproFaceTracking","version":"3.0.0"}'
Must-Fail { Get-QproRocmModelPair $current } 'valid bundled tongue model' 'Missing manifest selection'
Manifest $current

# An update may keep a personal v15 and relocate the bundled v15 bytes to v16.
# Only matching public hashes AND the local bundle ownership marker establish it.
$package = Join-Path $fixture 'collision'
$collision = Join-Path $package 'QproRuntime'
Manifest $collision
Add-Pair $collision 15 'personal'
Add-Pair $collision 16 'bundled'
Add-Pair $collision 99 'bundled'
Developer-Metadata $collision 16
Developer-Metadata $collision 99 $false
$gateHash = Hash (Join-Path $collision 'models\qpro-stereo-tongue-v16-gate.pt')
$directionHash = Hash (Join-Path $collision 'models\qpro-stereo-tongue-v16-direction.pt')
Write-Text (Join-Path $package 'SHA256SUMS.txt') ("$gateHash  QproRuntime/models/qpro-stereo-tongue-v15-gate.pt`n$directionHash  QproRuntime/models/qpro-stereo-tongue-v15-direction.pt`n")
Check ((Get-QproRocmModelPair $collision).Stem -ceq 'qpro-stereo-tongue-v16') 'Relocated bundled pair was confused with personal v15.'
Write-Text (Join-Path $collision 'models\qpro-stereo-tongue-v16-direction.pt') 'changed after update'
Must-Fail { Get-QproRocmModelPair $collision } 'complete bundled tongue model' 'Tampered relocated model and unowned identical copy'
Write-Text $pair.Gate 'changed during setup'
Must-Fail { Assert-QproRocmModelPairUnchanged $pair } 'changed during setup' 'Pinned model changed while downloading'
Write-Text $pair.Gate 'bundled gate'
[IO.File]::Delete($pair.Direction)
Must-Fail { Assert-QproRocmModelPairUnchanged $pair } 'missing or empty' 'Pinned direction disappeared'

# Run the installer's real training/inference call sequence with an inert
# executable substitute. A file changed by the first child must be rejected
# before a second child can run or a ready marker can be written.
$during = Join-Path $fixture 'changed during child'
Manifest $during
Add-Pair $during 15
Developer-Metadata $during 15
$qproSmokeModels = Get-QproRocmModelPair $during
$qproGateModel = $qproSmokeModels.Gate
$qproDirectionModel = $qproSmokeModels.Direction
$qproRocmPython = 'Invoke-QproModelFixture'
$script:modelFixtureCalls = 0
function Invoke-QproModelFixture {
    $script:modelFixtureCalls++
    Write-Text $qproDirectionModel 'changed by synthetic training child'
    $global:LASTEXITCODE = 0
}
$installerText = [IO.File]::ReadAllText($installer)
$modelStart = $installerText.IndexOf('    Assert-QproRocmModelPairUnchanged $qproSmokeModels', [StringComparison]::Ordinal)
$modelEnd = $installerText.IndexOf('    Remove-Item Env:QPRO_ROCM_INSTALL_SMOKE_TEST', $modelStart, [StringComparison]::Ordinal)
if ($modelStart -lt 0 -or $modelEnd -lt $modelStart) { throw 'Could not locate the actual GPU model validation sequence.' }
$modelSequence = [scriptblock]::Create($installerText.Substring($modelStart, $modelEnd - $modelStart))
Must-Fail { & $modelSequence } 'changed during setup' 'Model changed during training child'
Check ($script:modelFixtureCalls -eq 1) 'Inference ran after its model changed during training.'

# Execute the installer itself, with adapter discovery stubbed and an inert
# runtime import boundary. A bad bundle must stop before that boundary and
# before the installer's environment/registration/readiness mutations.
function Get-CimInstance {
    [CmdletBinding()]param([string]$ClassName)
    if ($ClassName -ne 'Win32_VideoController') { throw 'Unexpected hardware query in fixture.' }
    [pscustomobject]@{Name='AMD Radeon RX 7900 XTX'; PNPDeviceID='PCI\VEN_1002&DEV_744C'}
}
$originalLocal = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = Join-Path $fixture 'local'
    $ready = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\r\10-gfx1100\qpro-rocm-ready.json'
    $registration = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\r\rocm-runtimes.json'
    Write-Text $ready 'verified runtime sentinel'
    Write-Text $registration 'registration sentinel'
    $beforeReady = [IO.File]::ReadAllBytes($ready)
    $beforeRegistration = [IO.File]::ReadAllBytes($registration)
    $entry = Join-Path $fixture 'installer entry'
    Manifest $entry
    Write-Text (Join-Path $entry 'runtime-python.ps1') "throw 'FIXTURE_RUNTIME_IMPORT_MUST_NOT_RUN'"
    Must-Fail { & $installer -QproRoot $entry -Update } 'complete bundled tongue model' 'Installer missing-model preflight'
    Check ([Convert]::ToBase64String($beforeReady) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($ready))) 'Missing model changed the existing ready marker.'
    Check ([Convert]::ToBase64String($beforeRegistration) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($registration))) 'Missing model changed runtime registration.'
    Add-Pair $entry 15
    Developer-Metadata $entry 15
    Write-Text (Join-Path $entry 'runtime-python.ps1') @'
if ([IO.Path]::GetFileName($qproGateModel) -cne 'qpro-stereo-tongue-v15-gate.pt' -or [IO.Path]::GetFileName($qproDirectionModel) -cne 'qpro-stereo-tongue-v15-direction.pt') { throw 'WRONG_MODEL_REACHED_RUNTIME_IMPORT' }
throw 'FIXTURE_VALID_V15_PREFLIGHT_REACHED_RUNTIME_IMPORT'
'@
    Must-Fail { & $installer -QproRoot $entry -Update } 'FIXTURE_VALID_V15_PREFLIGHT_REACHED_RUNTIME_IMPORT' 'Installer uses selected pair before runtime import'
} finally { $env:LOCALAPPDATA = $originalLocal }

Write-Output "PASS: $checks ROCm bundled-model selection/preflight/preservation checks. No Python, downloads or GPU installs ran. Fixtures: $fixture"
