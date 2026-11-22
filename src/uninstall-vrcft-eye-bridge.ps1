$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$helper = Join-Path $root "vrcft-module-installation.ps1"
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw "Module installation files are missing. Re-extract the complete release ZIP." }
. $helper
$customLibs = Join-Path $env:APPDATA "VRCFaceTracking\CustomLibs"
$research = Join-Path $root "research"
$recoveryRoot = Get-QproModuleRecoveryRoot
Assert-QproPathWithoutLinks $customLibs
Assert-QproVrcftClosed
$inventory = @(Get-QproModuleInventory $customLibs)
if ($inventory.Count -gt 0) { Assert-QproModuleTransactionVolume $customLibs $recoveryRoot }
$transactionId = [guid]::NewGuid().ToString("N")
$recoveryDirectory = Join-Path $recoveryRoot ("vrcft-qpro-module-uninstall-backup-" + $transactionId)
Assert-QproDirectChild $recoveryDirectory $recoveryRoot
$moved = @()

try {
    if ($inventory.Count -gt 0) {
        Backup-QproEntries $inventory $customLibs $recoveryDirectory
        Assert-QproVrcftClosed
        $latest = @(Get-QproModuleInventory $customLibs)
        if ((@($latest | ForEach-Object Path | Sort-Object) -join [Environment]::NewLine) -ne
            (@($inventory | ForEach-Object Path | Sort-Object) -join [Environment]::NewLine)) {
            throw "Installed Qpro files changed during uninstallation. Retry with VRCFaceTracking closed."
        }
        Move-QproEntriesToRecovery $inventory $customLibs $recoveryDirectory ([ref]$moved)
        if (@(Get-QproModuleInventory $customLibs).Count -gt 0) { throw "Qpro modules remain after uninstallation. Check the module inventory." }
    }
} catch {
    $failure = $_.Exception.Message
    $rollbackErrors = @(Restore-QproRetiredEntries $moved)
    $rollback = if ($rollbackErrors.Count -gt 0) { " Rollback needs attention: $($rollbackErrors -join '; ')." } else { "" }
    throw "Uninstalling the Qpro modules failed: $failure Recovery copies: $recoveryDirectory.$rollback"
}
foreach ($entry in $inventory) {
    Write-Host "Removed the verified Qpro module: $($entry.Path)"
    if ($entry.Copied) { Write-Host "Its surrounding module metadata and other files were preserved. Repair that old module card through VRCFaceTracking if needed." }
}
if ($inventory.Count -eq 0) { Write-Host "No verified Qpro module was installed. Checking historical saved modules." }

# Older installers moved official modules into this release's research folder.
# Restore only a regular, metadata-identified, non-Qpro module. Personal model
# data and newer Qpro rollback directories are never restored as active modules.
function Restore-SavedOfficialModule([string]$BackupPath, [string]$ModuleId) {
    if (-not (Test-Path -LiteralPath $BackupPath -PathType Container)) { return "missing" }
    Assert-QproDirectChild $BackupPath $research
    Assert-QproRegularTree $BackupPath -ReadableSource
    # Pin the accepted historical source to its physical local directory. A
    # release junction may later change targets; it must not redirect a move or
    # a recovery write after the backup's identity was checked.
    $resolvedResearch = [Qpro.ModuleSourcePath]::GetFinalPath($research)
    $BackupPath = [Qpro.ModuleSourcePath]::GetFinalPath($BackupPath)
    Assert-QproDirectChild $BackupPath $resolvedResearch
    function Assert-SavedModuleRecoveryParent {
        Assert-QproReadableSourcePath $BackupPath
        $parent = [IO.Path]::GetDirectoryName($BackupPath)
        if (-not ([Qpro.ModuleSourcePath]::GetFinalPath($parent)).Equals($parent, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The saved module's recovery directory changed and was left untouched: $parent"
        }
    }
    $manifest = Read-QproModuleManifest (Join-Path $BackupPath "module.json")
    $dlls = @(Get-ChildItem -LiteralPath $BackupPath -File -Filter "*.dll" -Force)
    if ($null -eq $manifest -or [string]$manifest.ModuleId -ne $ModuleId -or $dlls.Count -eq 0) {
        Write-Host "The historical backup has no verified official module identity; it remains at $BackupPath for review."
        return "unverified"
    }
    if (@($dlls | Where-Object { (Get-QproAssemblyIdentity $_.FullName) -eq "Qpro.GazeBridge" }).Count -gt 0) {
        Write-Host "The historical backup contains Qpro DLLs and was not reinstalled: $BackupPath"
        return "unverified"
    }
    if (@($dlls | Where-Object { $null -eq (Get-QproAssemblyIdentity $_.FullName) }).Count -gt 0) {
        Write-Host "The historical backup contains an unreadable DLL and was left unchanged: $BackupPath"
        return "unverified"
    }
    $target = Join-Path $customLibs $ModuleId
    Assert-QproDirectChild $target $customLibs
    Assert-QproPathWithoutLinks $target
    if (Test-Path -LiteralPath $target) {
        Write-Host "The saved official module remains at $BackupPath because its installation path is occupied. No existing module was overwritten."
        return "occupied"
    }
    $hashes = Get-QproTreeHashes $BackupPath $true -ReadableSource
    New-Item -ItemType Directory -Path $customLibs -Force | Out-Null
    Assert-QproVrcftClosed
    Assert-SavedModuleRecoveryParent
    Assert-QproSameTree $BackupPath $true $hashes -ReadableSource
    Assert-QproPathWithoutLinks $target
    if (Test-Path -LiteralPath $target) { throw "The official module destination appeared during recovery and was left untouched: $target" }
    Move-Item -LiteralPath $BackupPath -Destination $target
    try { Assert-QproSameTree $target $true $hashes }
    catch {
        Assert-QproRegularTree $target
        Assert-SavedModuleRecoveryParent
        if (Test-Path -LiteralPath $BackupPath) { throw "The saved module recovery path is now occupied; the restored files remain at $target." }
        Move-Item -LiteralPath $target -Destination $BackupPath
        throw
    }
    Write-Host "Restored the saved official VRCFaceTracking module: $target"
    return "restored"
}

$restored = 0
$recoveryIssues = @()
try { $savedSources = Restore-QproSourceModuleBackups $customLibs $recoveryRoot }
catch {
    $reason = $_.Exception.Message
    $expected = $false
    for ($cause = $_.Exception; $null -ne $cause; $cause = $cause.InnerException) {
        if ($cause -is [IO.IOException] -or $cause -is [UnauthorizedAccessException] -or
            $cause -is [Security.SecurityException] -or $cause -is [FormatException] -or
            $cause -is [System.Management.Automation.ItemNotFoundException]) { $expected = $true; break }
    }
    if (-not $expected) { throw }
    # Qpro has already been retired. Preserve that accurate result and refuse
    # older fallback when the newer recovery inventory cannot be inspected.
    $savedSources = @{ Restored = 0; Issues = @("Saved source-module recovery could not be inspected: $reason");
        HandledModuleIds = @(); HistoricalBlocked = $true }
}
$restored += $savedSources.Restored
$recoveryIssues += @($savedSources.Issues)
if ((Test-Path -LiteralPath $research -PathType Container) -and -not $savedSources.HistoricalBlocked) {
    $officialBackups = @(Get-ChildItem -LiteralPath $research -Directory -Force |
        Where-Object { $_.Name -match '^vrcft-official-virtual-desktop-backup(?:-\d{8}-\d{6}|-[0-9a-f]{32})?$' } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($officialBackups.Count -gt 0 -and ($savedSources.HistoricalBlocked -or
        '91a90618-b020-4064-8832-809b2ca2b3bc' -in @($savedSources.HandledModuleIds))) {
        Write-Host 'Historical Virtual Desktop backups were kept for review because a newer source-module transaction already owns its recovery result.'
    } elseif ($officialBackups.Count -gt 0) {
        $result = "unverified"
        foreach ($backup in $officialBackups) {
            try { $result = Restore-SavedOfficialModule $backup.FullName "91a90618-b020-4064-8832-809b2ca2b3bc" }
            catch { Write-Host "The saved official module could not be restored: $($_.Exception.Message)"; $result = "unverified" }
            if ($result -eq "restored") { $restored++; break }
            if ($result -eq "occupied") { break }
        }
        if ($result -ne "restored") {
            $recoveryIssues += "The saved Virtual Desktop module was not restored ($result). Its recovery files remain in $research; review Activity or install the official module through VRCFaceTracking."
        }
        if ($officialBackups.Count -gt 1) { Write-Host "Older official module backups remain in $research for review." }
    }
    try { $result = Restore-SavedOfficialModule (Join-Path $research "vrcft-legacy-registry-module-backup") $QproLegacyModuleId }
    catch { Write-Host "The saved legacy module could not be restored: $($_.Exception.Message)"; $result = "unverified" }
    if ($result -eq "restored") { $restored++ }
    elseif ($result -ne "missing") {
        $recoveryIssues += "The saved legacy module was not restored ($result). Its recovery files remain in $research for review."
    }
}
elseif ($savedSources.HistoricalBlocked) { Write-Host 'Historical module backups remain unchanged because the newer recovery state could not be verified.' }
$remaining = @(Get-QproModuleInventory $customLibs).Count
Write-Host ("QPRO_MODULE_UNINSTALL " + (@{ removed = $inventory.Count; restored = $restored; remainingQpro = $remaining; recoveryIssues = @($recoveryIssues) } | ConvertTo-Json -Compress))
if ($remaining -ne 0) { throw "Qpro modules appeared during recovery. Keep VRCFaceTracking closed, then retry uninstallation." }
if ($recoveryIssues.Count -gt 0) { throw "Qpro modules were removed, but saved-module recovery needs attention. $($recoveryIssues -join ' ')" }
Write-Host "Qpro module uninstall finished. Restart VRCFaceTracking. Personal lower-face models and captures were not changed."
Write-Host "If face tracking is missing, install the official module for your chosen source. Unrelated modules and their metadata were left untouched."
