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
$research = Join-Path $root "research"
$sourcePath = Join-Path $env:LOCALAPPDATA "QproFaceTracking\config\tracking-source.txt"
foreach ($path in @($customLibs, $research, $sourcePath)) { Assert-QproPathWithoutLinks $path }
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
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "The module source project is missing. Install the prebuilt release ZIP instead." }
    if ([string]::IsNullOrWhiteSpace($VrcftInstallDir) -or
        -not (Test-Path -LiteralPath (Join-Path $VrcftInstallDir "VRCFaceTracking.SDK.dll") -PathType Leaf)) {
        throw "For -Rebuild, pass -VrcftInstallDir with your Steam VRCFaceTracking installation containing its SDK DLL. Release users do not need to rebuild."
    }
    & dotnet build $project -c Release "-p:VrcftInstallDir=$([System.IO.Path]::GetFullPath($VrcftInstallDir))"
    if ($LASTEXITCODE -ne 0) { throw "Building the Qpro module failed." }
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "The prebuilt Qpro module is missing. Re-extract the complete release ZIP." }
Assert-QproPathWithoutLinks $source
if ((Get-QproAssemblyIdentity $source) -ne "Qpro.GazeBridge") { throw "The packaged DLL is not a readable Qpro module. Re-extract the complete release ZIP." }
$inventory = @(Get-QproModuleInventory $customLibs)
$competing = @(Find-QproCompetingSourceModules $customLibs $inventory | Sort-Object -Unique)
if ($competing.Count -gt 0) {
    throw "Another Virtual Desktop or Steam Link module is installed at $($competing -join '; '). Remove it through VRCFaceTracking, close VRCFaceTracking, then retry. Qpro left those files unchanged."
}
if ((Test-Path -LiteralPath $sourcePath) -and -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "The face-tracking source path is not a regular file and was left untouched: $sourcePath"
}
New-Item -ItemType Directory -Path $customLibs, $research, (Split-Path -Parent $sourcePath) -Force | Out-Null
foreach ($path in @($customLibs, $research, $sourcePath)) { Assert-QproPathWithoutLinks $path }
$transactionId = [guid]::NewGuid().ToString("N")
$stageDirectory = Join-Path $customLibs (".qpro-module-" + $transactionId + ".tmp")
$stageToken = Join-Path (Split-Path -Parent $sourcePath) (".qpro-source-" + $transactionId + ".tmp")
$recoveryDirectory = Join-Path $research ("vrcft-qpro-module-switch-backup-" + $transactionId)
Assert-QproDirectChild $stageDirectory $customLibs
Assert-QproDirectChild $recoveryDirectory $research
$configExisted = Test-Path -LiteralPath $sourcePath -PathType Leaf
$configOriginal = if ($configExisted) { [System.IO.File]::ReadAllBytes($sourcePath) } else { $null }
$moved = @()
$installed = $false
$configWritten = $false
$stageHashes = $null

try {
    New-Item -ItemType Directory -Path $stageDirectory | Out-Null
    $stageDll = Join-Path $stageDirectory $identity.Dll
    Copy-Item -LiteralPath $source -Destination $stageDll
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $stageDll -Algorithm SHA256).Hash -ne $sourceHash) { throw "The staged Qpro DLL failed its SHA-256 check." }
    # VRCFT metadata uses MD5 for FileHash. Qpro's own transaction checks use
    # SHA-256; the two hashes describe different contracts.
    $metadata = [ordered]@{
        ModuleId = $identity.Id
        LastUpdated = [DateTime]::UtcNow.ToString("o")
        Version = $QproModuleVersion
        IsLocal = $true
        AuthorName = "Fwooffy"
        ModuleName = "QproFaceTracking - " + $identity.Name
        ModuleDescription = "Quest Pro face tracking from " + $identity.Name + " with opt-in Qpro gaze, tongue, cheek and pupil outputs."
        UsageInstructions = "Select " + $identity.Name + " in Qpro Hub. Use one source module at a time. Start VRCFaceTracking after installing; start optional live overrides in Qpro Hub."
        DownloadUrl = $null
        ModulePageUrl = "https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless"
        DllFileName = $identity.Dll
        FileHash = (Get-FileHash -LiteralPath $stageDll -Algorithm MD5).Hash.ToLowerInvariant()
    }
    [System.IO.File]::WriteAllText((Join-Path $stageDirectory "module.json"), ($metadata | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($stageToken, $identity.Token, [System.Text.UTF8Encoding]::new($false))
    $stageHashes = Get-QproTreeHashes $stageDirectory $true
    Backup-QproEntries $inventory $customLibs $recoveryDirectory
    if ($configExisted) { [System.IO.File]::WriteAllBytes((Join-Path $recoveryDirectory "tracking-source.txt"), $configOriginal) }
    Assert-QproVrcftClosed
    $latestInventory = @(Get-QproModuleInventory $customLibs)
    $originalPaths = @($inventory | ForEach-Object Path | Sort-Object) -join [Environment]::NewLine
    $currentPaths = @($latestInventory | ForEach-Object Path | Sort-Object) -join [Environment]::NewLine
    if ($currentPaths -ne $originalPaths) {
        throw "Installed Qpro files changed during preparation. Check the module list and retry."
    }
    if (@(Find-QproCompetingSourceModules $customLibs $latestInventory).Count -gt 0) { throw "A competing source module appeared during installation. Retry with VRCFaceTracking closed." }
    Assert-QproPathWithoutLinks $sourcePath
    if ($configExisted) {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or
            [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($sourcePath)) -ne [Convert]::ToBase64String($configOriginal)) {
            throw "The face-tracking source changed during installation. Retry after checking the Hub."
        }
    } elseif (Test-Path -LiteralPath $sourcePath) { throw "A face-tracking source file appeared during installation." }
    Assert-QproSameTree $stageDirectory $true $stageHashes
    Move-QproEntriesToRecovery $inventory $customLibs $recoveryDirectory ([ref]$moved)
    if (Test-Path -LiteralPath $destination) { throw "The Qpro module destination is occupied and was left untouched." }
    Move-Item -LiteralPath $stageDirectory -Destination $destination
    $installed = $true
    $configWritten = $true
    Copy-Item -LiteralPath $stageToken -Destination $sourcePath -Force
    Assert-QproSameTree $destination $true $stageHashes
    if (([System.IO.File]::ReadAllText($sourcePath)).Trim() -ne $identity.Token) { throw "The installed face-tracking source failed verification." }
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
    $rollback = if ($rollbackErrors.Count -gt 0) { " Rollback needs attention: $($rollbackErrors -join '; ')." } else { "" }
    throw "Installing the Qpro $($identity.Name) module failed: $failure Recovery copies: $recoveryDirectory.$rollback"
} finally {
    if (Test-Path -LiteralPath $stageToken -PathType Leaf) {
        Assert-QproPathWithoutLinks $stageToken
        Remove-Item -LiteralPath $stageToken
    }
    if ((Test-Path -LiteralPath $stageDirectory -PathType Container) -and $null -ne $stageHashes) {
        Assert-QproDirectChild $stageDirectory $customLibs
        Assert-QproSameTree $stageDirectory $true $stageHashes
        foreach ($file in Get-ChildItem -LiteralPath $stageDirectory -File) { Remove-Item -LiteralPath $file.FullName }
        # The stage has only its two recorded files. Remove its empty directory
        # without recursive deletion of CustomLibs or another module directory.
        Remove-Item -LiteralPath $stageDirectory
    }
}

Write-Host "Installed the Qpro $($identity.Name) VRCFaceTracking module: $destination"
Write-Host "Recovery copies of previous Qpro modules and the selected source are in $recoveryDirectory"
foreach ($entry in $inventory | Where-Object Copied) {
    Write-Host "Migrated a copied Qpro DLL from $($entry.Path). Its surrounding metadata and other module files were left unchanged. Repair that old module card through VRCFaceTracking if needed."
}
Write-Host "One Qpro source module is installed, with its own module card. The alternate Qpro source is removed. Restart VRCFaceTracking to load $($identity.Name)."
