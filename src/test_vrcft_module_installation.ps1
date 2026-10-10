param([string]$FixtureDll = "", [string]$OutputRoot = "")

$ErrorActionPreference = "Stop"
$sourceRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($FixtureDll)) {
    $FixtureDll = Join-Path $sourceRoot "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll"
}
if ([System.Reflection.AssemblyName]::GetAssemblyName($FixtureDll).Name -ne "Qpro.GazeBridge") {
    throw "The fixture must be an existing Qpro DLL. This test reads its metadata and copies it; it never loads or executes it."
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $sourceRoot)) "outputs\test-module-installation"
}
$fixtureRoot = Join-Path ([System.IO.Path]::GetFullPath($OutputRoot)) ("qpro-module-fixture-" + [guid]::NewGuid().ToString("N"))
$runtime = Join-Path $fixtureRoot "runtime"
$appData = Join-Path $fixtureRoot "appdata"
$localData = Join-Path $fixtureRoot "localdata"
$customLibs = Join-Path $appData "VRCFaceTracking\CustomLibs"
$oldAppData = $env:APPDATA
$oldLocalData = $env:LOCALAPPDATA
$install = Join-Path $runtime "install-vrcft-eye-bridge.ps1"
$uninstall = Join-Path $runtime "uninstall-vrcft-eye-bridge.ps1"
$dllParent = Join-Path $runtime "vrcft-gaze-bridge\bin\Release\net10.0"
New-Item -ItemType Directory -Path $runtime, $dllParent, $appData, $localData -Force | Out-Null
foreach ($name in @("install-vrcft-eye-bridge.ps1", "uninstall-vrcft-eye-bridge.ps1", "vrcft-module-installation.ps1")) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $runtime $name)
}
Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $dllParent "Qpro.GazeBridge.dll")
. (Join-Path $runtime "vrcft-module-installation.ps1")

function Assert-Test([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Contains) {
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    Assert-Test ($null -ne $message -and $message.Contains($Contains)) "Expected a safe refusal containing '$Contains'; result: $message"
}
# Only the fixture calls these installers. Suppress process discovery so a real
# VRCFT session cannot alter the test; no real APPDATA path is passed to them.
function Get-Process {
    param($Name, $ErrorAction)
    if ($global:QproFixtureVrcftOpen) { return [pscustomobject]@{ Name = "fixture-process" } }
    return @()
}

function New-FixtureJunction([string]$Path, [string]$Target) {
    $scope = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\') + '\'
    foreach ($value in @($Path, $Target)) {
        Assert-Test ([IO.Path]::GetFullPath($value).StartsWith($scope, [StringComparison]::OrdinalIgnoreCase)) "The junction fixture escaped its own directory."
    }
    New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
}
function Remove-FixtureJunction([string]$Path) {
    $scope = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    Assert-Test ($full.StartsWith($scope, [StringComparison]::OrdinalIgnoreCase) -and
        ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) "The test refused to unlink an ordinary directory or external path."
    [IO.Directory]::Delete($full, $false)
}

try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localData
    & $install -TrackingSource VirtualDesktop
    $vdFolder = Join-Path $customLibs $QproVirtualDesktopId
    $slFolder = Join-Path $customLibs $QproSteamLinkId
    $vdDll = Join-Path $vdFolder "000-Qpro.VirtualDesktop.dll"
    $metadata = Read-QproModuleManifest (Join-Path $vdFolder "module.json")
    Assert-Test ($metadata.ModuleId -eq $QproVirtualDesktopId -and $metadata.IsLocal -eq $true -and
        $metadata.AuthorName -eq "Fwooffy" -and $metadata.Version -eq "2.1.2" -and
        $metadata.DllFileName -eq "000-Qpro.VirtualDesktop.dll" -and $metadata.ModuleName -eq "QproFaceTracking - Virtual Desktop") "The new module card has the wrong Qpro identity."
    Assert-Test ($null -eq $metadata.DownloadUrl -and $null -eq $metadata.InstallationState -and
        $metadata.FileHash -eq (Get-FileHash -LiteralPath $vdDll -Algorithm MD5).Hash.ToLowerInvariant()) "The metadata uses an upstream identity, stale state or wrong FileHash."
    Assert-Test (@(Get-ChildItem -LiteralPath $customLibs -File -Filter "*.dll").Count -eq 0 -and
        @(Get-QproModuleInventory $customLibs).Count -eq 1) "A duplicate loose module DLL was installed."

    [System.IO.File]::WriteAllText((Join-Path $runtime "release-manifest.json"), '{"name":"QproFaceTracking","version":"2.4.7"}')
    & $install -TrackingSource SteamLink
    Assert-Test (-not (Test-Path -LiteralPath $vdFolder) -and (Test-Path -LiteralPath $slFolder)) "Source switching left both own GUID modules installed."
    Assert-Test ((Read-QproModuleManifest (Join-Path $slFolder "module.json")).Version -eq "2.4.7") "The module card did not use its packaged release version."
    Assert-Test ((Get-Content -LiteralPath (Join-Path $localData "QproFaceTracking\config\tracking-source.txt") -Raw) -eq "steam-link") "Source selection was not committed."
    [System.IO.File]::WriteAllText((Join-Path $runtime "release-manifest.json"), '{invalid')
    Expect-Failure { & $install -TrackingSource VirtualDesktop } "release manifest could not be read"
    Assert-Test (Test-Path -LiteralPath $slFolder) "Malformed release metadata changed the installed module."
    # Recovery helpers and uninstallation must remain usable if the extracted
    # release's metadata is damaged; only new installation needs its version.
    . (Join-Path $runtime "vrcft-module-installation.ps1")
    & $uninstall
    Assert-Test (@(Get-QproModuleInventory $customLibs).Count -eq 0) "Uninstallation left a Qpro module active."
    Remove-Item -LiteralPath (Join-Path $runtime "release-manifest.json")

    # The user may launch the release from a junction or synced-folder alias.
    # Only the packaged source follows it; recovery lives in regular local data.
    $runtimeLink = Join-Path $fixtureRoot "linked-runtime"
    New-FixtureJunction $runtimeLink $runtime
    try {
        foreach ($sourceName in @("VirtualDesktop", "SteamLink")) {
            & (Join-Path $runtimeLink "install-vrcft-eye-bridge.ps1") -TrackingSource $sourceName
            $null = Assert-QproInstalledModule $sourceName $customLibs (Join-Path $runtimeLink "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll")
        }
        Assert-Test (-not (Test-Path -LiteralPath (Join-Path $runtime "research"))) "A linked release was used as the transaction recovery destination."
        Assert-Test (@(Get-ChildItem -LiteralPath (Join-Path $localData "QproFaceTracking\module-recovery") -Directory).Count -ge 3) "No regular local recovery copies were created."
        & (Join-Path $runtimeLink "uninstall-vrcft-eye-bridge.ps1")
        Assert-Test (@(Get-QproModuleInventory $customLibs).Count -eq 0) "Uninstall through a directory junction left a Qpro module installed."
    } finally { Remove-FixtureJunction $runtimeLink }

    # Source permissiveness must never allow an installed/config/recovery path
    # to redirect writes into another folder.
    $savedCustomLibs = Join-Path $fixtureRoot "saved-custom-libs"
    $redirected = Join-Path $fixtureRoot "redirected-files"
    New-Item -ItemType Directory -Path $redirected | Out-Null
    $sentinel = Join-Path $redirected "keep.txt"
    [IO.File]::WriteAllText($sentinel, "unrelated fixture data")
    Move-Item -LiteralPath $customLibs -Destination $savedCustomLibs
    New-FixtureJunction $customLibs $redirected
    try {
        Expect-Failure { & $install -TrackingSource VirtualDesktop } "contains a link"
        Expect-Failure { & $uninstall } "contains a link"
        Assert-Test (([IO.File]::ReadAllText($sentinel)) -eq "unrelated fixture data" -and
            @(Get-ChildItem -LiteralPath $redirected -Force).Count -eq 1) "A linked destination was modified."
    } finally {
        Remove-FixtureJunction $customLibs
        Move-Item -LiteralPath $savedCustomLibs -Destination $customLibs
    }
    $recoveryRoot = Join-Path $localData "QproFaceTracking\module-recovery"
    $savedRecovery = Join-Path $fixtureRoot "saved-recovery"
    Move-Item -LiteralPath $recoveryRoot -Destination $savedRecovery
    New-FixtureJunction $recoveryRoot $redirected
    try {
        Expect-Failure { & $install -TrackingSource VirtualDesktop } "contains a link"
        Expect-Failure { & $uninstall } "contains a link"
        Assert-Test (@(Get-ChildItem -LiteralPath $redirected -Force).Count -eq 1) "A linked recovery destination was modified."
    } finally {
        Remove-FixtureJunction $recoveryRoot
        Move-Item -LiteralPath $savedRecovery -Destination $recoveryRoot
    }

    # Migrate old root layout and copied/renamed DLLs in a foreign GUID folder.
    $foreignId = "2a8c8080-2a76-46af-bf76-1da7c0127ef8"
    $foreign = Join-Path $customLibs $foreignId
    New-Item -ItemType Directory -Path $foreign -Force | Out-Null
    $foreignMetadata = '{"ModuleId":"2a8c8080-2a76-46af-bf76-1da7c0127ef8","ModuleName":"LinkFT","DllFileName":"LinkFT.dll","IsLocal":false}'
    [System.IO.File]::WriteAllText((Join-Path $foreign "module.json"), $foreignMetadata)
    [System.IO.File]::WriteAllText((Join-Path $foreign "keep.txt"), "foreign fixture data")
    Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $customLibs "000-Qpro.IndependentGaze.dll")
    Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $foreign "LinkFT.dll")
    Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $foreign "000-Qpro.IndependentGaze.dll")
    & $install -TrackingSource VirtualDesktop
    Assert-Test (@(Get-QproModuleInventory $customLibs).Count -eq 1 -and
        -not (Test-Path -LiteralPath (Join-Path $customLibs "000-Qpro.IndependentGaze.dll"))) "Legacy/copied Qpro modules were not migrated to one installation."
    Assert-Test (([System.IO.File]::ReadAllText((Join-Path $foreign "module.json"))) -eq $foreignMetadata -and
        ([System.IO.File]::ReadAllText((Join-Path $foreign "keep.txt"))) -eq "foreign fixture data" -and
        @(Get-ChildItem -LiteralPath $foreign -File -Filter "*.dll").Count -eq 0) "Foreign metadata/files were changed during ad-hoc Qpro migration."

    # Unknown files at a reserved Qpro path must stop before any installed change.
    $reserved = Join-Path $customLibs "000-Qpro.SteamLink.dll"
    [System.IO.File]::WriteAllText($reserved, "not a Qpro assembly")
    $beforeHash = (Get-FileHash -LiteralPath $vdDll -Algorithm SHA256).Hash
    Expect-Failure { & $install -TrackingSource SteamLink } "unknown file"
    Assert-Test ((Get-FileHash -LiteralPath $vdDll -Algorithm SHA256).Hash -eq $beforeHash -and
        ([System.IO.File]::ReadAllText($reserved)) -eq "not a Qpro assembly") "A refusal changed installed or unknown files."
    Remove-Item -LiteralPath $reserved

    # A real competing source module is preserved and reported, never backed up
    # under a borrowed Qpro identity or silently deleted.
    [System.IO.File]::WriteAllText((Join-Path $foreign "LinkFT.dll"), "foreign native fixture DLL")
    Expect-Failure { & $install -TrackingSource SteamLink } "Another Virtual Desktop or Steam Link module"
    Assert-Test (([System.IO.File]::ReadAllText((Join-Path $foreign "LinkFT.dll"))) -eq "foreign native fixture DLL" -and
        (Test-Path -LiteralPath $vdFolder)) "A competing module was changed."
    & $uninstall
    Assert-Test ((Test-Path -LiteralPath (Join-Path $foreign "LinkFT.dll")) -and
        (@(Get-QproModuleInventory $customLibs).Count -eq 0)) "Uninstall removed unrelated module data."
    Remove-Item -LiteralPath (Join-Path $foreign "LinkFT.dll")
    & $install -TrackingSource VirtualDesktop
    $beforeHash = (Get-FileHash -LiteralPath $vdDll -Algorithm SHA256).Hash
    $global:QproFixtureVrcftOpen = $true
    Expect-Failure { & $install -TrackingSource SteamLink } "Close VRCFaceTracking"
    $global:QproFixtureVrcftOpen = $false
    Assert-Test ((Get-FileHash -LiteralPath $vdDll -Algorithm SHA256).Hash -eq $beforeHash) "Running-VRCFT refusal changed the installed module."

    # Simulate a partial source-token write after the new module was committed.
    # The rollback must restore both the original module and the exact token.
    function Copy-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
        if ($LiteralPath -match '\.qpro-source-[0-9a-f]{32}\.tmp$' -and $Destination -match 'tracking-source\.txt$') {
            [System.IO.File]::WriteAllText($Destination, "partial fixture write")
            throw "Fixture commit write failed after a partial overwrite."
        }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    Expect-Failure { & $install -TrackingSource SteamLink } "Fixture commit write failed"
    Remove-Item -LiteralPath Function:\Copy-Item
    $tokenPath = Join-Path $localData "QproFaceTracking\config\tracking-source.txt"
    Assert-Test ((Get-FileHash -LiteralPath $vdDll -Algorithm SHA256).Hash -eq $beforeHash -and
        ([System.IO.File]::ReadAllText($tokenPath)) -eq "virtual-desktop" -and
        -not (Test-Path -LiteralPath $slFolder)) "A partial commit failure did not restore the original module and source."

    # A failed second retirement must restore the first module as well.
    $loose = Join-Path $customLibs "000-Qpro.IndependentGaze.dll"
    Copy-Item -LiteralPath $FixtureDll -Destination $loose
    function Move-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Destination)
        if ($LiteralPath -eq $loose) { throw "Fixture uninstall retirement failed." }
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
    }
    Expect-Failure { & $uninstall } "Fixture uninstall retirement failed"
    Remove-Item -LiteralPath Function:\Move-Item
    Assert-Test ((Get-FileHash -LiteralPath $vdDll -Algorithm SHA256).Hash -eq $beforeHash -and
        (Test-Path -LiteralPath $loose)) "A partial uninstall did not restore the original modules."
    Remove-Item -LiteralPath $loose
    & $uninstall

    # An unusable newer backup must not hide an older verified official module.
    $research = Join-Path $runtime "research"
    $officialId = "91a90618-b020-4064-8832-809b2ca2b3bc"
    $validBackup = Join-Path $research "vrcft-official-virtual-desktop-backup"
    $invalidBackup = Join-Path $research "vrcft-official-virtual-desktop-backup-20260101-000000"
    $brokenBackup = Join-Path $research "vrcft-official-virtual-desktop-backup-20260102-000000"
    New-Item -ItemType Directory -Path $validBackup,$invalidBackup,$brokenBackup | Out-Null
    foreach ($path in @($validBackup,$invalidBackup)) {
        [IO.File]::WriteAllText((Join-Path $path "module.json"), ('{"ModuleId":"' + $officialId + '"}'))
    }
    Copy-Item -LiteralPath ([object].Assembly.Location) -Destination (Join-Path $validBackup "OfficialFixture.dll")
    Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $invalidBackup "QproFixture.dll")
    [IO.File]::WriteAllText((Join-Path $brokenBackup "module.json"), '{broken')
    (Get-Item -LiteralPath $validBackup).LastWriteTimeUtc = [datetime]'2025-01-01'
    (Get-Item -LiteralPath $invalidBackup).LastWriteTimeUtc = [datetime]'2026-01-01'
    (Get-Item -LiteralPath $brokenBackup).LastWriteTimeUtc = [datetime]'2026-01-02'
    $modelDirectory = Join-Path $runtime "models"
    New-Item -ItemType Directory -Path $modelDirectory | Out-Null
    $personalModel = Join-Path $modelDirectory "personal.pt"
    [IO.File]::WriteAllText($personalModel, "personal model fixture")
    $modelHash = (Get-FileHash -LiteralPath $personalModel -Algorithm SHA256).Hash
    $tokenOriginal = [IO.File]::ReadAllBytes($tokenPath)
    & $uninstall
    $officialTarget = Join-Path $customLibs $officialId
    Assert-Test ((Test-Path -LiteralPath (Join-Path $officialTarget "OfficialFixture.dll")) -and
        -not (Test-Path -LiteralPath $validBackup) -and (Test-Path -LiteralPath $invalidBackup) -and
        (Test-Path -LiteralPath $brokenBackup)) "An unusable newer backup prevented a valid older official restore."
    Assert-Test ((Get-FileHash -LiteralPath $personalModel -Algorithm SHA256).Hash -eq $modelHash -and
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($tokenPath)) -eq [Convert]::ToBase64String($tokenOriginal)) "Uninstall changed a personal model or source preference."
    Expect-Failure { & $uninstall } "saved-module recovery needs attention"

    # Remove the already-tested backups from discovery without deleting them.
    foreach ($path in @($invalidBackup,$brokenBackup)) {
        $saved = Join-Path $research ("reviewed-" + (Split-Path -Leaf $path))
        Assert-QproDirectChild $path $research
        Assert-QproDirectChild $saved $research
        Move-Item -LiteralPath $path -Destination $saved
    }
    $occupiedBackup = Join-Path $research "vrcft-official-virtual-desktop-backup"
    New-Item -ItemType Directory -Path $occupiedBackup | Out-Null
    [IO.File]::WriteAllText((Join-Path $occupiedBackup "module.json"), ('{"ModuleId":"' + $officialId + '"}'))
    Copy-Item -LiteralPath ([object].Assembly.Location) -Destination (Join-Path $occupiedBackup "OfficialFixture.dll")
    $officialHash = (Get-FileHash -LiteralPath (Join-Path $officialTarget "OfficialFixture.dll") -Algorithm SHA256).Hash
    Expect-Failure { & $uninstall } "saved-module recovery needs attention"
    Assert-Test ((Test-Path -LiteralPath $occupiedBackup) -and
        (Get-FileHash -LiteralPath (Join-Path $officialTarget "OfficialFixture.dll") -Algorithm SHA256).Hash -eq $officialHash) "An occupied restore target was overwritten."
    $saved = Join-Path $research "reviewed-official-backup"
    Assert-QproDirectChild $occupiedBackup $research
    Assert-QproDirectChild $saved $research
    Move-Item -LiteralPath $occupiedBackup -Destination $saved

    $legacyBackup = Join-Path $research "vrcft-legacy-registry-module-backup"
    New-Item -ItemType Directory -Path $legacyBackup | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyBackup "module.json"), ('{"ModuleId":"' + $QproLegacyModuleId + '"}'))
    Copy-Item -LiteralPath ([object].Assembly.Location) -Destination (Join-Path $legacyBackup "LegacyOfficialFixture.dll")

    # If a physical recovery parent is replaced during the post-move check, the
    # rollback must not write through its new junction into unrelated files.
    $legacyTarget = Join-Path $customLibs $QproLegacyModuleId
    $savedResearch = Join-Path $fixtureRoot "saved-historical-research"
    function Move-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Destination)
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
        if ($LiteralPath -eq $legacyBackup -and $Destination -eq $legacyTarget) {
            [IO.File]::WriteAllText((Join-Path $legacyTarget "changed-after-move.txt"), "fixture validation failure")
            Microsoft.PowerShell.Management\Move-Item -LiteralPath $research -Destination $savedResearch
            New-FixtureJunction $research $redirected
        }
    }
    try {
        Expect-Failure { & $uninstall } "saved-module recovery needs attention"
        Assert-Test ((Test-Path -LiteralPath (Join-Path $legacyTarget "LegacyOfficialFixture.dll")) -and
            @(Get-ChildItem -LiteralPath $redirected -Force).Count -eq 1 -and
            ([IO.File]::ReadAllText($sentinel)) -eq "unrelated fixture data") "A rollback wrote through a changed recovery parent."
    } finally {
        Remove-Item -LiteralPath Function:\Move-Item
        Remove-FixtureJunction $research
        Move-Item -LiteralPath $savedResearch -Destination $research
    }
    # Return the same fixture to its pre-failure state for a normal linked
    # historical restore. All paths below were created by this test.
    Assert-QproDirectChild $legacyTarget $customLibs
    Assert-QproDirectChild $legacyBackup $research
    Remove-Item -LiteralPath (Join-Path $legacyTarget "changed-after-move.txt")
    Move-Item -LiteralPath $legacyTarget -Destination $legacyBackup
    New-FixtureJunction $runtimeLink $runtime
    try { & (Join-Path $runtimeLink "uninstall-vrcft-eye-bridge.ps1") }
    finally { Remove-FixtureJunction $runtimeLink }
    & $uninstall
    Assert-Test ((Test-Path -LiteralPath (Join-Path $customLibs ($QproLegacyModuleId + "\LegacyOfficialFixture.dll"))) -and
        @(Get-QproModuleInventory $customLibs).Count -eq 0) "Restored official legacy files were mistaken for a Qpro module."
    Write-Host "PASS: linked release sources, strict destination/recovery paths, own GUID cards, source switching, migration, rollback, backup fallback, unresolved-recovery reporting, legacy restoration and model/config preservation. Fixture: $fixtureRoot"
} finally {
    $global:QproFixtureVrcftOpen = $false
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalData
}
