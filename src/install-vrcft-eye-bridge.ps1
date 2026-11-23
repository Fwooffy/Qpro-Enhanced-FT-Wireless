param(
    [switch]$Rebuild,
    [ValidateSet("VirtualDesktop", "SteamLink")][string]$TrackingSource,
    [string]$VrcftInstallDir = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$helper = Join-Path $root "vrcft-module-installation.ps1"
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw "Module installation files are missing. Re-extract the complete release ZIP." }
. $helper
$QproModuleVersion = Get-QproModuleVersion $root
$customLibs = Join-Path $env:APPDATA "VRCFaceTracking\CustomLibs"
$stageRoot = Get-QproModuleStagingRoot $customLibs
$research = Get-QproModuleRecoveryRoot
$sourcePath = Join-Path $env:LOCALAPPDATA "QproFaceTracking\config\tracking-source.txt"
foreach ($path in @($customLibs, $research, $sourcePath, $stageRoot)) { Assert-QproPathWithoutLinks $path }
if (-not $TrackingSource) {
    $TrackingSource = if ((Test-Path -LiteralPath $sourcePath -PathType Leaf) -and
        ([System.IO.File]::ReadAllText($sourcePath)).Trim() -eq "steam-link") { "SteamLink" } else { "VirtualDesktop" }
}
$identity = Get-QproModuleIdentity $TrackingSource
$destination = Join-Path $customLibs $identity.Id
Assert-QproDirectChild $destination $customLibs
Assert-QproVrcftClosed
$source = Join-Path $root "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll"
if ($Rebuild) {
    $project = Join-Path $root "vrcft-gaze-bridge\Qpro.GazeBridge.csproj"
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Rebuilding the backup module requires the Qpro Companion source checkout." }
    if ([string]::IsNullOrWhiteSpace($VrcftInstallDir) -or
        -not (Test-Path -LiteralPath (Join-Path $VrcftInstallDir "VRCFaceTracking.SDK.dll") -PathType Leaf)) {
        throw "For -Rebuild, pass -VrcftInstallDir with your Steam VRCFaceTracking installation containing its SDK DLL."
    }
    & dotnet build $project -c Release "-p:Version=$QproModuleVersion" "-p:VrcftInstallDir=$([System.IO.Path]::GetFullPath($VrcftInstallDir))"
    if ($LASTEXITCODE -ne 0) { throw "Building the Qpro module failed." }
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "This copy of Companion is missing the backup module. Extract the full package with VRCFaceTracking backup support and retry." }
Assert-QproReadableSourcePath $source
if ((Get-QproAssemblyIdentity $source -ReadableSource) -ne "Qpro.GazeBridge") { throw "The packaged DLL is not a readable Qpro module. Re-extract the complete release ZIP. If the folder is synced by OneDrive, choose 'Always keep on this device' for the extracted Qpro folder, then retry." }
$inventory = @(Get-QproModuleInstallInventory $customLibs)
$sourceModules = @($inventory | Where-Object { $_.SourceModuleId })
Assert-QproModuleTransactionVolume $customLibs $research
if ((Test-Path -LiteralPath $sourcePath) -and -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "The face-tracking source path is not a regular file and was left untouched: $sourcePath"
}
New-Item -ItemType Directory -Path $customLibs, $research, $stageRoot, (Split-Path -Parent $sourcePath) -Force | Out-Null
foreach ($path in @($customLibs, $research, $sourcePath, $stageRoot)) { Assert-QproPathWithoutLinks $path }
$transactionId = [guid]::NewGuid().ToString("N")
$stageDirectory = Join-Path $stageRoot ("install-" + $transactionId)
$stageToken = Join-Path (Split-Path -Parent $sourcePath) (".qpro-source-" + $transactionId + ".tmp")
$recoveryDirectory = Join-Path $research ("vrcft-qpro-module-switch-backup-" + $transactionId)
Assert-QproDirectChild $stageDirectory $stageRoot
Assert-QproDirectChild $recoveryDirectory $research
$configExisted = Test-Path -LiteralPath $sourcePath -PathType Leaf
$configOriginal = if ($configExisted) { [System.IO.File]::ReadAllBytes($sourcePath) } else { $null }
$moved = @()
$installed = $false
$configWritten = $false
$stageHashes = $null
$sourceReceiptPrepared = $false

try {
    New-Item -ItemType Directory -Path $stageDirectory | Out-Null
    $stageDll = Join-Path $stageDirectory $identity.Dll
    Copy-Item -LiteralPath $source -Destination $stageDll
    $sourceHash = Get-QproFileHash $source
    if ((Get-QproFileHash $stageDll) -ne $sourceHash) { throw "The staged Qpro DLL failed its SHA-256 check." }
    # VRCFT metadata uses MD5 for FileHash. Qpro's own transaction checks use
    # SHA-256; the two hashes describe different contracts.
    $metadata = [ordered]@{
        ModuleId = $identity.Id
        LastUpdated = [DateTime]::UtcNow.ToString("o")
        Version = $QproModuleVersion
        IsLocal = $true
        AuthorName = "Fwooffy"
        ModuleName = $identity.DisplayName
        ModuleDescription = "Quest Pro face tracking from " + $identity.Name + " with opt-in Qpro gaze, tongue, cheek and pupil outputs."
        UsageInstructions = "Select VRCFaceTracking backup in Qpro Companion for " + $identity.Name + ". Reopen VRCFaceTracking after installing the module. Enable OSC in VRChat."
        DownloadUrl = $null
        ModulePageUrl = "https://github.com/Fwooffy/Qpro-Companion"
        DllFileName = $identity.Dll
        FileHash = (Get-QproFileHash $stageDll "MD5").ToLowerInvariant()
    }
    [System.IO.File]::WriteAllText((Join-Path $stageDirectory "module.json"), ($metadata | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($stageToken, $identity.Token, [System.Text.UTF8Encoding]::new($false))
    $stageHashes = Get-QproTreeHashes $stageDirectory $true
    Backup-QproEntries $inventory $customLibs $recoveryDirectory
    if ($configExisted) { [System.IO.File]::WriteAllBytes((Join-Path $recoveryDirectory "tracking-source.txt"), $configOriginal) }
    Write-QproSourceModuleReceipt $recoveryDirectory $transactionId 'prepared' $inventory
    $sourceReceiptPrepared = $sourceModules.Count -gt 0
    Assert-QproVrcftClosed
    $latestInventory = @(Get-QproModuleInstallInventory $customLibs)
    $originalPaths = @($inventory | ForEach-Object Path | Sort-Object) -join [Environment]::NewLine
    $currentPaths = @($latestInventory | ForEach-Object Path | Sort-Object) -join [Environment]::NewLine
    if ($currentPaths -ne $originalPaths) {
        throw "Installed Qpro files changed during preparation. Check the module list and retry."
    }
    Assert-QproPathWithoutLinks $sourcePath
    if ($configExisted) {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or
            [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($sourcePath)) -ne [Convert]::ToBase64String($configOriginal)) {
            throw "The face-tracking source changed during installation. Retry after checking the Hub."
        }
    } elseif (Test-Path -LiteralPath $sourcePath) { throw "A face-tracking source file appeared during installation." }
    Assert-QproSameTree $stageDirectory $true $stageHashes
    Assert-QproVrcftClosed
    Move-QproEntriesToRecovery $inventory $customLibs $recoveryDirectory ([ref]$moved)
    Assert-QproVrcftClosed
    if (Test-Path -LiteralPath $destination) { throw "The Qpro module destination is occupied and was left untouched." }
    Move-Item -LiteralPath $stageDirectory -Destination $destination
    $installed = $true
    $configWritten = $true
    Copy-Item -LiteralPath $stageToken -Destination $sourcePath -Force
    Assert-QproSameTree $destination $true $stageHashes
    if (([System.IO.File]::ReadAllText($sourcePath)).Trim() -ne $identity.Token) { throw "The installed face-tracking source failed verification." }
    $installedInventory = @(Get-QproModuleInventory $customLibs)
    if ($installedInventory.Count -ne 1 -or @(Find-QproCompetingSourceModules $customLibs $installedInventory).Count -gt 0) {
        throw 'Installed source modules changed during installation. No competing module can be left active with Qpro.'
    }
    Write-QproSourceModuleReceipt $recoveryDirectory $transactionId 'committed' $inventory
} catch {
    $failure = $_.Exception.Message
    $rollbackErrors = @()
    if ($installed) {
        try {
            Assert-QproDirectChild $destination $customLibs
            Assert-QproSameTree $destination $true $stageHashes
            $failedInstall = Join-Path $recoveryDirectory "failed-install"
            Assert-QproDirectChild $failedInstall $recoveryDirectory
            Move-Item -LiteralPath $destination -Destination $failedInstall
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    $rollbackErrors += @(Restore-QproRetiredEntries $moved)
    if ($configWritten) {
        try {
            Assert-QproPathWithoutLinks $sourcePath
            if ($configExisted) { [System.IO.File]::WriteAllBytes($sourcePath, $configOriginal) }
            elseif (Test-Path -LiteralPath $sourcePath -PathType Leaf) { Remove-Item -LiteralPath $sourcePath }
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    if ($sourceReceiptPrepared -and $rollbackErrors.Count -eq 0) {
        try { Write-QproSourceModuleReceipt $recoveryDirectory $transactionId 'rolled-back' $inventory }
        catch { $rollbackErrors += "Could not finalize the source-module rollback receipt: $($_.Exception.Message)" }
    }
    $rollback = if ($rollbackErrors.Count -gt 0) { " Rollback needs attention: $($rollbackErrors -join '; ')." } else { "" }
    throw "Installing the Qpro $($identity.Name) module failed: $failure Recovery copies: $recoveryDirectory.$rollback"
} finally {
    if (Test-Path -LiteralPath $stageToken -PathType Leaf) {
        Assert-QproPathWithoutLinks $stageToken
        Remove-Item -LiteralPath $stageToken
    }
    if ((Test-Path -LiteralPath $stageDirectory -PathType Container) -and $null -ne $stageHashes) {
        Assert-QproDirectChild $stageDirectory $stageRoot
        Assert-QproSameTree $stageDirectory $true $stageHashes
        foreach ($file in Get-ChildItem -LiteralPath $stageDirectory -File) { Remove-Item -LiteralPath $file.FullName }
        # The stage has only its two recorded files. Remove its empty directory
        # without recursive deletion of CustomLibs or another module directory.
        Remove-Item -LiteralPath $stageDirectory
    }
}

Write-Host "Installed the Qpro $($identity.Name) VRCFaceTracking module: $destination"
Write-Host "Recovery copies of previous Qpro modules and the selected source are in $recoveryDirectory"
foreach ($entry in $sourceModules) {
    Write-Host "Backed up and replaced the recognized $($entry.Provider) module at $($entry.Path). Uninstall Qpro module or Restore stock VRCFT can restore its verified recovery copy."
}
foreach ($entry in $inventory | Where-Object Copied) {
    Write-Host "Migrated a copied Qpro DLL from $($entry.Path). Its surrounding metadata and other module files were left unchanged. Repair that old module card through VRCFaceTracking if needed."
}
Write-Host "One Qpro source module is installed, with its own module card. The alternate Qpro source is removed. Restart VRCFaceTracking to load $($identity.Name)."
