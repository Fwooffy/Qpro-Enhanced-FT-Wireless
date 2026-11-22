param(
    [string]$FixtureDll = "",
    [string]$OutputRoot = "",
    [string]$Dotnet = "",
    [switch]$BuildFixturesOnly
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (-not $FixtureDll) { $FixtureDll = Join-Path $sourceRoot 'vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll' }
if ([Reflection.AssemblyName]::GetAssemblyName($FixtureDll).Name -ne 'Qpro.GazeBridge') { throw 'Pass a compiled Qpro module fixture.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $sourceRoot)) 'outputs\module-source-switch-tests' }
$output = [IO.Path]::GetFullPath($OutputRoot)
[IO.Directory]::CreateDirectory($output) | Out-Null
if (-not $Dotnet) {
    $Dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }
}

# Compile tiny named assemblies from our own marker source. No installed vendor
# DLL is needed, loaded or modified, and the empty package source prevents downloads.
$assemblyRoot = Join-Path $output 'fixture-assemblies'
$projectRoot = Join-Path $assemblyRoot 'project'
$offlineFeed = Join-Path $assemblyRoot 'offline-feed'
[IO.Directory]::CreateDirectory($projectRoot) | Out-Null
[IO.Directory]::CreateDirectory($offlineFeed) | Out-Null
$project = Join-Path $projectRoot 'SourceModuleFixture.csproj'
[IO.File]::WriteAllText($project, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DebugType>None</DebugType></PropertyGroup><ItemGroup><Compile Include="Marker.cs" /></ItemGroup></Project>')
[IO.File]::WriteAllText((Join-Path $projectRoot 'Marker.cs'), 'namespace QproSwitchFixtures { public sealed class SourceModuleMarker {} }')
$fixtureNames = @('VirtualDesktop.FaceTracking', 'SteamLinkVRCFTModule', 'SLExtTrackingModule', 'Unrelated.FaceProvider')
$missingFixture = @($fixtureNames | Where-Object {
    $expectedName = $_
    $existing = Join-Path (Join-Path $assemblyRoot $expectedName) ($expectedName + '.dll')
    -not (Test-Path -LiteralPath $existing) -or [Reflection.AssemblyName]::GetAssemblyName($existing).Name -ne $expectedName
}).Count -gt 0
if ($missingFixture) {
    & $Dotnet restore $project --source $offlineFeed
    if ($LASTEXITCODE -ne 0) { throw 'The cached fixture compiler references are unavailable.' }
}
$assemblies = @{}
foreach ($name in $fixtureNames) {
    $assemblyOutput = Join-Path $assemblyRoot $name
    $path = Join-Path $assemblyOutput ($name + '.dll')
    if (-not (Test-Path -LiteralPath $path) -or [Reflection.AssemblyName]::GetAssemblyName($path).Name -ne $name) {
        & $Dotnet build $project --no-restore -c Release -p:UseSharedCompilation=false "-p:AssemblyName=$name" -o $assemblyOutput
        if ($LASTEXITCODE -ne 0) { throw "Could not build named source fixture: $name" }
    }
    if ([Reflection.AssemblyName]::GetAssemblyName($path).Name -ne $name) { throw "Wrong fixture assembly identity: $name" }
    $assemblies[$name] = $path
}
if ($BuildFixturesOnly) { $assemblies | ConvertTo-Json; return }

$fixtureRoot = Join-Path $output ('source-switch-fixture-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$oldAppData, $oldLocalData = $env:APPDATA, $env:LOCALAPPDATA
$script:checks = 0
$script:cases = 0
$global:QproSourceSwitchVrcftOpen = $false
$providerProfiles = @(
    @{ Id = '91a90618-b020-4064-8832-809b2ca2b3bc'; Name = 'Virtual Desktop'; Author = 'Virtual Desktop, Inc.'; Assembly = 'VirtualDesktop.FaceTracking' },
    @{ Id = '2a8c8080-2a76-46af-bf76-1da7c0127ef8'; Name = 'SteamLink VRCFT Module'; Author = 'Ykeara'; Assembly = 'SteamLinkVRCFTModule' },
    @{ Id = 'b146eda9-be48-4016-ab63-680a694064bd'; Name = 'SteamLink VRCFT'; Author = 'danwillm'; Assembly = 'SLExtTrackingModule' }
)

function Assert-Switch([bool]$Condition, [string]$Message) {
    $script:checks++
    if (-not $Condition) { throw $Message }
}
function Expect-SwitchFailure([scriptblock]$Action, [string]$Contains) {
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert-Switch ($null -ne $failure -and $failure.Contains($Contains)) "Expected '$Contains'; result: $failure"
}
function Get-Process {
    param($Name, $ErrorAction)
    if ($global:QproSourceSwitchVrcftOpen) { return [pscustomobject]@{ Name = 'fixture-vrcft' } }
    return @()
}
function Get-SwitchTree([string]$Path) {
    $result = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $result }
    $prefix = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File -Force) {
        $result[$file.FullName.Substring($prefix.Length)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $result
}
function Assert-SwitchTree([string]$Path, [hashtable]$Expected, [string]$Context) {
    $actual = Get-SwitchTree $Path
    Assert-Switch ($actual.Count -eq $Expected.Count) "$Context changed its file inventory."
    foreach ($key in $Expected.Keys) { Assert-Switch ($actual.ContainsKey($key) -and $actual[$key] -eq $Expected[$key]) "$Context changed $key." }
}
function New-SwitchCase([string]$Name) {
    $script:cases++
    $root = Join-Path $fixtureRoot ('{0:00}-{1}' -f $script:cases, $Name)
    $runtime = Join-Path $root 'runtime'
    $case = [pscustomobject]@{
        Root = $root; Runtime = $runtime; AppData = (Join-Path $root 'appdata'); LocalData = (Join-Path $root 'localdata')
        CustomLibs = (Join-Path $root 'appdata\VRCFaceTracking\CustomLibs')
        Recovery = (Join-Path $root 'localdata\QproFaceTracking\module-recovery')
        Token = (Join-Path $root 'localdata\QproFaceTracking\config\tracking-source.txt')
    }
    [IO.Directory]::CreateDirectory($case.CustomLibs) | Out-Null
    [IO.Directory]::CreateDirectory((Split-Path -Parent $case.Token)) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $runtime 'vrcft-gaze-bridge\bin\Release\net10.0')) | Out-Null
    foreach ($name in @('install-vrcft-eye-bridge.ps1', 'uninstall-vrcft-eye-bridge.ps1', 'vrcft-module-installation.ps1')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $runtime $name)
    }
    Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $runtime 'vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll')
    [IO.File]::WriteAllText($case.Token, "steam-link`r`n", [Text.UTF8Encoding]::new($true))
    $env:APPDATA, $env:LOCALAPPDATA = $case.AppData, $case.LocalData
    return $case
}
function New-SwitchProvider($Case, $ProviderProfile, [hashtable]$Changes = @{}, [string]$Assembly = '') {
    $folder = Join-Path $Case.CustomLibs $ProviderProfile.Id
    [IO.Directory]::CreateDirectory((Join-Path $folder 'settings')) | Out-Null
    $metadata = [ordered]@{
        ModuleId = $ProviderProfile.Id; ModuleName = $ProviderProfile.Name; AuthorName = $ProviderProfile.Author
        DllFileName = ($ProviderProfile.Assembly + '.dll'); Version = '1.5'; IsLocal = $false
        UsageInstructions = 'Synthetic test provider'; FileHash = $null
    }
    foreach ($key in $Changes.Keys) { $metadata[$key] = $Changes[$key] }
    [IO.File]::WriteAllText((Join-Path $folder 'module.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $fixtureAssembly = if ($Assembly) { $Assembly } else { $ProviderProfile.Assembly }
    Copy-Item -LiteralPath $assemblies[$fixtureAssembly] -Destination (Join-Path $folder $metadata.DllFileName)
    [IO.File]::WriteAllText((Join-Path $folder 'settings\user.json'), '{"fixtureUserPreference":"keep exactly"}')
    [IO.File]::WriteAllText((Join-Path $folder 'hidden-settings.txt'), 'hidden fixture state')
    (Get-Item -LiteralPath (Join-Path $folder 'hidden-settings.txt')).Attributes = [IO.FileAttributes]::Hidden
    return $folder
}
function Invoke-SwitchInstall($Case, [string]$Source = 'VirtualDesktop') {
    & (Join-Path $Case.Runtime 'install-vrcft-eye-bridge.ps1') -TrackingSource $Source
}
function Invoke-SwitchUninstall($Case, [string]$Runtime = '') {
    if (-not $Runtime) { $Runtime = $Case.Runtime }
    & (Join-Path $Runtime 'uninstall-vrcft-eye-bridge.ps1')
}
function Get-SwitchReceipts($Case) {
    if (-not (Test-Path -LiteralPath $Case.Recovery)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $Case.Recovery -File -Filter 'source-modules.json' -Recurse) {
        [pscustomobject]@{ Path = $file.FullName; Root = $file.DirectoryName; Data = (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json) }
    }
}
function Assert-NoInstalledQpro($Case) {
    Assert-Switch (-not (Test-Path -LiteralPath (Join-Path $Case.CustomLibs 'd6a8eeb2-3490-4d4f-bec1-9d5909da08ea')) -and
        -not (Test-Path -LiteralPath (Join-Path $Case.CustomLibs '5d5cb4f9-63d7-4e8f-9802-24a5d78ea6ee'))) 'A Qpro source module remained installed.'
}

try {
    . (Join-Path $sourceRoot 'vrcft-module-installation.ps1')
    $volumeMarker = 'qpro-volume-fixture-' + [guid]::NewGuid().ToString('N')
    $volumeSource = 'C:\' + $volumeMarker + '\CustomLibs'
    $volumeRecovery = 'D:\' + $volumeMarker + '\recovery'
    Expect-SwitchFailure { Assert-QproModuleTransactionVolume $volumeSource $volumeRecovery } 'same drive'
    Assert-Switch (-not (Test-Path -LiteralPath ('C:\' + $volumeMarker)) -and
        -not (Test-Path -LiteralPath ('D:\' + $volumeMarker))) 'Read-only cross-volume validation created a directory.'
    Assert-QproModuleTransactionVolume $volumeSource ('c:\' + $volumeMarker + '\recovery')
    Assert-Switch $true 'Same-drive validation rejected a case-insensitive drive root.'

    foreach ($providerProfile in $providerProfiles) {
        $case = New-SwitchCase ('known-' + $providerProfile.Assembly)
        $provider = New-SwitchProvider $case $providerProfile
        $original = Get-SwitchTree $provider
        Invoke-SwitchInstall $case
        Assert-Switch (-not (Test-Path -LiteralPath $provider)) 'The recognized source remained active beside Qpro.'
        Assert-Switch (Test-Path -LiteralPath (Join-Path $case.CustomLibs 'd6a8eeb2-3490-4d4f-bec1-9d5909da08ea\000-Qpro.VirtualDesktop.dll')) 'Qpro was not installed after source retirement.'
        $receipt = @(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' })
        Assert-Switch ($receipt.Count -eq 1) 'A committed original-source recovery receipt was not recorded.'
        $record = @($receipt[0].Data.modules | Where-Object { $_.moduleId -eq $providerProfile.Id })
        Assert-Switch ($record.Count -eq 1) 'The original provider was not recorded once.'
        $backup = Join-Path $receipt[0].Root $record[0].backup
        Assert-SwitchTree $backup $original 'Original verified backup'
        Invoke-SwitchInstall $case 'SteamLink'
        Invoke-SwitchInstall $case 'VirtualDesktop'
        Assert-Switch (-not (Test-Path -LiteralPath $provider)) 'Ordinary Qpro source switching restored a competing provider.'
        Assert-SwitchTree $backup $original 'Original backup after repeated installs'
        $otherRuntime = Join-Path $case.Root 'different-extracted-release'
        [IO.Directory]::CreateDirectory($otherRuntime) | Out-Null
        foreach ($name in @('uninstall-vrcft-eye-bridge.ps1', 'vrcft-module-installation.ps1')) {
            Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $otherRuntime $name)
        }
        Invoke-SwitchUninstall $case $otherRuntime
        Assert-SwitchTree $provider $original 'Cross-release original-provider restoration'
        Assert-NoInstalledQpro $case
        Invoke-SwitchUninstall $case $otherRuntime
        Assert-SwitchTree $provider $original 'Idempotent original-provider restoration'
        Assert-SwitchTree $backup $original 'Retained recovery copy after restore'
    }

    $case = New-SwitchCase 'multiple-originals'
    $originals = @{}
    foreach ($providerProfile in $providerProfiles) { $path = New-SwitchProvider $case $providerProfile; $originals[$path] = Get-SwitchTree $path }
    Invoke-SwitchInstall $case 'SteamLink'
    foreach ($path in $originals.Keys) { Assert-Switch (-not (Test-Path -LiteralPath $path)) 'A second recognized provider stayed active.' }
    Invoke-SwitchUninstall $case
    foreach ($path in $originals.Keys) { Assert-SwitchTree $path $originals[$path] 'Multiple original source snapshot' }
    Assert-NoInstalledQpro $case

    foreach ($variant in @('wrong-id', 'wrong-author', 'wrong-dll', 'wrong-assembly', 'mixed-qpro')) {
        $case = New-SwitchCase $variant
        $changes = @{}
        if ($variant -eq 'wrong-id') { $changes.ModuleId = '0e4c20d0-99c7-4396-855b-453f40940723' }
        if ($variant -eq 'wrong-author') { $changes.AuthorName = 'Unrelated publisher' }
        if ($variant -eq 'wrong-dll') { $changes.DllFileName = 'SomethingElse.dll' }
        $assembly = if ($variant -eq 'wrong-assembly') { 'Unrelated.FaceProvider' } else { '' }
        $provider = New-SwitchProvider $case $providerProfiles[0] $changes $assembly
        if ($variant -eq 'mixed-qpro') { Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $provider 'CopiedQpro.dll') }
        $before = Get-SwitchTree $case.CustomLibs
        Expect-SwitchFailure { Invoke-SwitchInstall $case } 'Another Virtual Desktop or Steam Link module'
        Assert-SwitchTree $case.CustomLibs $before 'Unrecognized provider refusal'
        Assert-NoInstalledQpro $case
    }

    $case = New-SwitchCase 'host-open'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $before = Get-SwitchTree $case.CustomLibs
    $global:QproSourceSwitchVrcftOpen = $true
    try { Expect-SwitchFailure { Invoke-SwitchInstall $case } 'Close VRCFaceTracking' }
    finally { $global:QproSourceSwitchVrcftOpen = $false }
    Assert-SwitchTree $case.CustomLibs $before 'Running VRCFT refusal'
    Assert-Switch (-not (Test-Path -LiteralPath $case.Recovery)) 'A running-host refusal created a transaction.'

    $case = New-SwitchCase 'staging-outside-loader-directory'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $original = Get-SwitchTree $provider
    $global:QproSourceSwitchStageRoot = Join-Path $case.AppData 'VRCFaceTracking\QproModuleStaging'
    $global:QproSourceSwitchRecoveryRoot = $case.Recovery
    $global:QproSourceSwitchSuppliedDll = Join-Path $case.Runtime 'vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll'
    $global:QproSourceSwitchInstallStageObserved = 0
    $global:QproSourceSwitchRestoreStageObserved = 0
    function Copy-Item {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
        $destinationFull = [IO.Path]::GetFullPath($Destination)
        if ($LiteralPath -eq $global:QproSourceSwitchSuppliedDll) {
            if (-not $destinationFull.StartsWith($global:QproSourceSwitchStageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The install stage could be scanned by the VRCFT module loader.'
            }
            $global:QproSourceSwitchInstallStageObserved++
        }
        if ($Recurse -and $LiteralPath.StartsWith($global:QproSourceSwitchRecoveryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            if (-not $destinationFull.StartsWith($global:QproSourceSwitchStageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The restore stage could be scanned by the VRCFT module loader.'
            }
            $global:QproSourceSwitchRestoreStageObserved++
        }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    try {
        Invoke-SwitchInstall $case
        Invoke-SwitchUninstall $case
    } finally { Remove-Item -LiteralPath Function:\Copy-Item }
    Assert-Switch ($global:QproSourceSwitchInstallStageObserved -gt 0 -and $global:QproSourceSwitchRestoreStageObserved -gt 0) 'Both staging paths were not exercised.'
    Assert-SwitchTree $provider $original 'Staged source restoration'
    Assert-Switch (@(Get-ChildItem -LiteralPath $global:QproSourceSwitchStageRoot -Filter 'module.json' -File -Recurse -ErrorAction SilentlyContinue).Count -eq 0) 'A completed transaction leaked a staged module card.'

    $case = New-SwitchCase 'linked-staging-root'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $before = Get-SwitchTree $case.CustomLibs
    $stageRoot = Join-Path $case.AppData 'VRCFaceTracking\QproModuleStaging'
    $redirectedStage = Join-Path $case.Root 'unrelated-staging-target'
    [IO.Directory]::CreateDirectory($redirectedStage) | Out-Null
    [IO.File]::WriteAllText((Join-Path $redirectedStage 'keep.txt'), 'unrelated fixture content')
    $sentinel = Get-SwitchTree $redirectedStage
    New-Item -ItemType Junction -Path $stageRoot -Target $redirectedStage | Out-Null
    try {
        Expect-SwitchFailure { Invoke-SwitchInstall $case } 'contains a link'
        Assert-SwitchTree $case.CustomLibs $before 'Linked staging refusal'
        Assert-SwitchTree $redirectedStage $sentinel 'Linked staging sentinel'
        Assert-NoInstalledQpro $case
    } finally {
        Assert-Switch ([IO.Path]::GetFullPath($stageRoot).StartsWith($case.AppData + '\', [StringComparison]::OrdinalIgnoreCase) -and
            ((Get-Item -LiteralPath $stageRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'The staging fixture refused to remove an ordinary or external directory.'
        [IO.Directory]::Delete($stageRoot, $false)
    }

    $case = New-SwitchCase 'backup-failure'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $before = Get-SwitchTree $case.CustomLibs
    $global:QproSourceSwitchFailedBackupSource = $provider
    function Copy-Item {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
        if ($LiteralPath -eq $global:QproSourceSwitchFailedBackupSource -and $Recurse) { throw 'Fixture recovery destination is read-only.' }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    try { Expect-SwitchFailure { Invoke-SwitchInstall $case } 'Fixture recovery destination is read-only' }
    finally { Remove-Item -LiteralPath Function:\Copy-Item }
    Assert-SwitchTree $case.CustomLibs $before 'Backup-failure source preservation'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'partial-commit'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $before = Get-SwitchTree $case.CustomLibs
    $tokenBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($case.Token))
    function Copy-Item {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
        if ($LiteralPath -match '\.qpro-source-[0-9a-f]{32}\.tmp$' -and $Destination -match 'tracking-source\.txt$') {
            [IO.File]::WriteAllText($Destination, 'partial fixture token')
            throw 'Fixture partial source commit failure.'
        }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    try { Expect-SwitchFailure { Invoke-SwitchInstall $case } 'Fixture partial source commit failure' }
    finally { Remove-Item -LiteralPath Function:\Copy-Item }
    Assert-SwitchTree $case.CustomLibs $before 'Failed-commit original-provider rollback'
    Assert-Switch ([Convert]::ToBase64String([IO.File]::ReadAllBytes($case.Token)) -eq $tokenBefore) 'Rollback changed original source preference bytes.'
    Assert-NoInstalledQpro $case
    Assert-Switch (@(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' }).Count -eq 0) 'A rolled-back install left an active recovery receipt.'

    $case = New-SwitchCase 'changed-during-backup'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $before = Get-SwitchTree $case.CustomLibs
    $global:QproSourceSwitchChangedBackupSource = $provider
    $global:QproSourceSwitchChangedPreference = Join-Path $provider 'settings\user.json'
    function Copy-Item {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
        if ($LiteralPath -eq $global:QproSourceSwitchChangedBackupSource -and $Recurse) {
            [IO.File]::WriteAllText($global:QproSourceSwitchChangedPreference, 'concurrent fixture preference edit')
        }
    }
    try { Expect-SwitchFailure { Invoke-SwitchInstall $case } 'changed during' }
    finally { Remove-Item -LiteralPath Function:\Copy-Item }
    $before[(Join-Path $providerProfiles[0].Id 'settings\user.json')] = (Get-FileHash -LiteralPath $global:QproSourceSwitchChangedPreference -Algorithm SHA256).Hash
    Assert-SwitchTree $case.CustomLibs $before 'Concurrent source edit preservation'
    Assert-Switch ([IO.File]::ReadAllText($global:QproSourceSwitchChangedPreference) -eq 'concurrent fixture preference edit') 'Rollback overwrote a concurrent source change.'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'second-source-retirement-failure'
    $global:QproSourceSwitchRetirementSources = @()
    foreach ($sourceProfile in $providerProfiles[0..1]) { $global:QproSourceSwitchRetirementSources += New-SwitchProvider $case $sourceProfile }
    $before = Get-SwitchTree $case.CustomLibs
    $global:QproSourceSwitchRetirementAttempts = 0
    function Move-Item {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Destination, [switch]$Force)
        if ($LiteralPath -in $global:QproSourceSwitchRetirementSources -and $Destination -match '\\retired\\') {
            $global:QproSourceSwitchRetirementAttempts++
            if ($global:QproSourceSwitchRetirementAttempts -eq 2) { throw 'Fixture second source retirement failed.' }
        }
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
    }
    try { Expect-SwitchFailure { Invoke-SwitchInstall $case } 'Fixture second source retirement failed' }
    finally { Remove-Item -LiteralPath Function:\Move-Item }
    Assert-Switch ($global:QproSourceSwitchRetirementAttempts -eq 2) 'The partial retirement fixture did not reach its second source.'
    Assert-SwitchTree $case.CustomLibs $before 'Partial source retirement rollback'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'occupied-restore'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $original = Get-SwitchTree $provider
    Invoke-SwitchInstall $case
    [IO.Directory]::CreateDirectory($provider) | Out-Null
    [IO.File]::WriteAllText((Join-Path $provider 'keep.txt'), 'concurrent unrelated module')
    $occupied = Get-SwitchTree $provider
    Expect-SwitchFailure { Invoke-SwitchUninstall $case } 'saved-module recovery needs attention'
    Assert-SwitchTree $provider $occupied 'Occupied restore target'
    Assert-NoInstalledQpro $case
    $held = Join-Path $case.Root 'held-occupied-module'
    Move-Item -LiteralPath $provider -Destination $held
    Invoke-SwitchUninstall $case
    Assert-SwitchTree $provider $original 'Retry after occupied target was cleared'
    Assert-SwitchTree $held $occupied 'Preserved concurrent module'

    $case = New-SwitchCase 'corrupt-backup'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    Invoke-SwitchInstall $case
    $receipt = @(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' })[0]
    $backup = Join-Path $receipt.Root $receipt.Data.modules[0].backup
    [IO.File]::WriteAllText((Join-Path $backup 'settings\user.json'), 'changed recovery bytes')
    $corrupt = Get-SwitchTree $backup
    Expect-SwitchFailure { Invoke-SwitchUninstall $case } 'saved-module recovery needs attention'
    Assert-Switch (-not (Test-Path -LiteralPath $provider)) 'A corrupt provider backup was restored.'
    Assert-SwitchTree $backup $corrupt 'Corrupt backup preservation'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'prepared-crash-recovery'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $original = Get-SwitchTree $provider
    Invoke-SwitchInstall $case
    $receipt = @(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' })[0]
    $receipt.Data.state = 'prepared'
    [IO.File]::WriteAllText($receipt.Path, ($receipt.Data | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Invoke-SwitchUninstall $case
    Assert-SwitchTree $provider $original 'Interrupted source-switch recovery'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'consumed-backup-not-resurrected'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $original = Get-SwitchTree $provider
    Invoke-SwitchInstall $case
    Invoke-SwitchUninstall $case
    Assert-SwitchTree $provider $original 'Initial consumed-receipt recovery'
    $held = Join-Path $case.Root 'user-removed-source-module'
    Move-Item -LiteralPath $provider -Destination $held
    Invoke-SwitchInstall $case 'SteamLink'
    Invoke-SwitchUninstall $case
    Assert-Switch (-not (Test-Path -LiteralPath $provider)) 'A consumed backup resurrected a source the user removed later.'
    Assert-SwitchTree $held $original 'User-removed source data'

    $case = New-SwitchCase 'modern-recovery-precedes-historical'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $original = Get-SwitchTree $provider
    Invoke-SwitchInstall $case
    $receipt = @(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' })[0]
    $modernBackup = Join-Path $receipt.Root $receipt.Data.modules[0].backup
    $historicalBackup = Join-Path $case.Runtime 'research\vrcft-official-virtual-desktop-backup'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $historicalBackup)) | Out-Null
    Copy-Item -LiteralPath $modernBackup -Destination $historicalBackup -Recurse
    [IO.File]::WriteAllText((Join-Path $historicalBackup 'settings\user.json'), 'older historical source preference')
    $historical = Get-SwitchTree $historicalBackup
    Invoke-SwitchUninstall $case
    Assert-SwitchTree $provider $original 'Modern original-provider recovery priority'
    Assert-SwitchTree $historicalBackup $historical 'Unused historical backup preservation'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'malformed-newest-receipt'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $older = Get-SwitchTree $provider
    Invoke-SwitchInstall $case
    $olderReceipt = @(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' })[0]
    $olderBackup = Join-Path $olderReceipt.Root $olderReceipt.Data.modules[0].backup
    Copy-Item -LiteralPath $olderBackup -Destination $provider -Recurse
    [IO.File]::WriteAllText((Join-Path $provider 'settings\user.json'), 'newer provider preference')
    $newer = Get-SwitchTree $provider
    Invoke-SwitchInstall $case 'SteamLink'
    $newerReceipt = @(Get-SwitchReceipts $case | Where-Object { $_.Path -ne $olderReceipt.Path -and $_.Data.state -eq 'committed' })[0]
    $newerBackup = Join-Path $newerReceipt.Root $newerReceipt.Data.modules[0].backup
    [IO.File]::WriteAllText($newerReceipt.Path, '{malformed-newer-recovery-receipt')
    (Get-Item -LiteralPath $olderReceipt.Path).LastWriteTimeUtc = [datetime]'2025-01-01'
    (Get-Item -LiteralPath $newerReceipt.Path).LastWriteTimeUtc = [datetime]'2026-01-01'
    Expect-SwitchFailure { Invoke-SwitchUninstall $case } 'saved-module recovery needs attention'
    Assert-Switch (-not (Test-Path -LiteralPath $provider)) 'A malformed newer receipt silently activated an older provider configuration.'
    Assert-SwitchTree $olderBackup $older 'Older source backup after malformed newer receipt'
    Assert-SwitchTree $newerBackup $newer 'Newer source backup after malformed newer receipt'
    Assert-NoInstalledQpro $case

    $case = New-SwitchCase 'recovery-discovery-failure'
    $provider = New-SwitchProvider $case $providerProfiles[0]
    $original = Get-SwitchTree $provider
    Invoke-SwitchInstall $case
    $receipt = @(Get-SwitchReceipts $case | Where-Object { $_.Data.state -eq 'committed' })[0]
    $modernBackup = Join-Path $receipt.Root $receipt.Data.modules[0].backup
    $historicalBackup = Join-Path $case.Runtime 'research\vrcft-official-virtual-desktop-backup'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $historicalBackup)) | Out-Null
    Copy-Item -LiteralPath $modernBackup -Destination $historicalBackup -Recurse
    [IO.File]::WriteAllText((Join-Path $historicalBackup 'settings\user.json'), 'obsolete historical preference')
    $historical = Get-SwitchTree $historicalBackup
    $global:QproSourceSwitchDiscoveryRoot = $case.Recovery
    $global:QproSourceSwitchDiscoveryAttempted = $false
    $global:QproSourceSwitchDiscoveryFailure = $null
    function Get-ChildItem {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Filter, [switch]$Directory, [switch]$File, [switch]$Recurse, [switch]$Force)
        if ($LiteralPath -eq $global:QproSourceSwitchDiscoveryRoot -and $Directory) {
            $global:QproSourceSwitchDiscoveryAttempted = $true
            throw [IO.IOException]::new('Fixture source recovery enumeration was denied.')
        }
        Microsoft.PowerShell.Management\Get-ChildItem @PSBoundParameters
    }
    try {
        $records = @(& {
            try { Invoke-SwitchUninstall $case }
            catch { $global:QproSourceSwitchDiscoveryFailure = $_.Exception.Message }
        } 6>&1)
    } finally { Remove-Item -LiteralPath Function:\Get-ChildItem }
    Assert-Switch ($global:QproSourceSwitchDiscoveryAttempted) 'The global recovery discovery failure was not exercised.'
    Assert-Switch ($global:QproSourceSwitchDiscoveryFailure -and $global:QproSourceSwitchDiscoveryFailure.Contains('saved-module recovery needs attention')) 'A recovery discovery failure did not retain its failed result.'
    $statusLines = @($records | ForEach-Object { $_.ToString() } | Where-Object { $_.StartsWith('QPRO_MODULE_UNINSTALL ') })
    Assert-Switch ($statusLines.Count -eq 1) 'Recovery discovery failure lost the structured uninstall result.'
    $status = $statusLines[0].Substring('QPRO_MODULE_UNINSTALL '.Length) | ConvertFrom-Json
    Assert-Switch ($status.removed -eq 1 -and $status.remainingQpro -eq 0 -and $status.restored -eq 0 -and
        @($status.recoveryIssues | Where-Object { $_.Contains('Fixture source recovery enumeration was denied') }).Count -gt 0) 'The structured result concealed removal or the failed recovery cause.'
    Assert-Switch (-not (Test-Path -LiteralPath $provider)) 'A discovery failure activated an obsolete historical module.'
    Assert-SwitchTree $modernBackup $original 'Modern backup after global recovery discovery failure'
    Assert-SwitchTree $historicalBackup $historical 'Historical backup after global recovery discovery failure'
    Assert-NoInstalledQpro $case

    Write-Host "PASS $script:checks source-switch checks across $script:cases isolated transactions. No installed modules were changed. Fixture: $fixtureRoot"
} finally {
    $global:QproSourceSwitchVrcftOpen = $false
    Remove-Variable -Scope Global -Name QproSourceSwitchFailedBackupSource, QproSourceSwitchChangedBackupSource,
        QproSourceSwitchChangedPreference, QproSourceSwitchRetirementSources, QproSourceSwitchRetirementAttempts,
        QproSourceSwitchStageRoot, QproSourceSwitchRecoveryRoot, QproSourceSwitchSuppliedDll,
        QproSourceSwitchInstallStageObserved, QproSourceSwitchRestoreStageObserved,
        QproSourceSwitchDiscoveryRoot, QproSourceSwitchDiscoveryAttempted, QproSourceSwitchDiscoveryFailure -ErrorAction SilentlyContinue
    $env:APPDATA, $env:LOCALAPPDATA = $oldAppData, $oldLocalData
}
