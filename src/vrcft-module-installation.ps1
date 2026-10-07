# Shared inventory and transaction helpers for Qpro's local VRCFT modules.
# Assembly inspection reads metadata only; no installed DLL is executed.
$QproVirtualDesktopId = "d6a8eeb2-3490-4d4f-bec1-9d5909da08ea"
$QproSteamLinkId = "5d5cb4f9-63d7-4e8f-9802-24a5d78ea6ee"
$QproLegacyModuleId = "7f9be083-a4f1-4e30-b28a-8e6ec878d583"

function Get-QproModuleVersion([string]$RuntimeRoot, [string]$SourceVersion = "2.1.2") {
    $manifestPath = Join-Path $RuntimeRoot "release-manifest.json"
    Assert-QproPathWithoutLinks $manifestPath
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        # A development checkout may omit its packaged identity. Keep the fallback explicit
        # so a future release always takes its version from the built manifest.
        $version = $SourceVersion
        Write-Verbose "No packaged release manifest; using source-development module version $SourceVersion."
    } else {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
            (Get-Item -LiteralPath $manifestPath -Force).Length -gt 65536) {
            throw "The Qpro release manifest is not a regular, reasonably sized file. Re-extract the complete release ZIP."
        }
        try {
            $json = [System.IO.File]::ReadAllText($manifestPath).Trim()
            # Windows PowerShell unwraps singleton arrays after ConvertFrom-Json.
            # Require the JSON root itself to be an object before parsing it.
            if (-not $json.StartsWith('{', [StringComparison]::Ordinal) -or
                -not $json.EndsWith('}', [StringComparison]::Ordinal)) {
                throw "The release manifest must be a JSON object."
            }
            $manifest = $json | ConvertFrom-Json -ErrorAction Stop
        } catch {
            throw "The Qpro release manifest could not be read. Re-extract the complete release ZIP."
        }
        if ($manifest -isnot [pscustomobject] -or $manifest.name -cne "QproFaceTracking" -or $manifest.version -isnot [string]) {
            throw "The Qpro release manifest has no valid app identity or version. Re-extract the complete release ZIP."
        }
        $version = $manifest.version
    }
    $parsed = $null
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        -not [System.Version]::TryParse($version, [ref]$parsed)) {
        throw "The Qpro module version must be a stable major.minor.patch version. Re-extract the complete release ZIP."
    }
    return $version
}

function Get-QproModuleIdentity([string]$TrackingSource) {
    if ($TrackingSource -eq "SteamLink") {
        return @{ Id = $QproSteamLinkId; Dll = "000-Qpro.SteamLink.dll"; Token = "steam-link"; Name = "Steam Link" }
    }
    return @{ Id = $QproVirtualDesktopId; Dll = "000-Qpro.VirtualDesktop.dll"; Token = "virtual-desktop"; Name = "Virtual Desktop" }
}

function Assert-QproDirectChild([string]$Path, [string]$Parent) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if (-not [System.IO.Path]::GetDirectoryName($full).Equals($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Module path is outside its expected directory: $full"
    }
}

function Assert-QproPathWithoutLinks([string]$Path) {
    $current = [System.IO.Path]::GetFullPath($Path)
    if ($current.StartsWith('\\', [System.StringComparison]::Ordinal)) { throw "Network module paths are unsupported: $current" }
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The module or recovery path contains a link and was left untouched: $current"
            }
        }
        $next = [System.IO.Path]::GetDirectoryName($current)
        if ($next -eq $current) { break }
        $current = $next
    }
}

function Assert-QproRegularTree([string]$Path) {
    Assert-QproPathWithoutLinks $Path
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push([System.IO.Path]::GetFullPath($Path))
    $count = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            $count++
            if ($count -gt 4096 -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The module directory contains a link or exceeds the inventory limit and was left untouched: $Path"
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Get-QproAssemblyIdentity([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $null }
    try { return [System.Reflection.AssemblyName]::GetAssemblyName($item.FullName).Name }
    catch { return $null }
}

function Read-QproModuleManifest([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -gt 65536 -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The module manifest is a link or is too large and was left untouched: $Path"
    }
    try { return [System.IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "The module manifest could not be read and was left untouched: $Path" }
}

function Get-QproModuleInventory([string]$CustomLibs) {
    Assert-QproPathWithoutLinks $CustomLibs
    if (-not (Test-Path -LiteralPath $CustomLibs -PathType Container)) { return }
    $reservedDlls = @("000-Qpro.VirtualDesktop.dll", "000-Qpro.SteamLink.dll", "000-Qpro.IndependentGaze.dll")
    foreach ($item in Get-ChildItem -LiteralPath $CustomLibs -Force) {
        $ownDirectory = $item.Name -in @($QproVirtualDesktopId, $QproSteamLinkId, $QproLegacyModuleId)
        if ($ownDirectory) {
            if (-not $item.PSIsContainer) { throw "A Qpro module directory is occupied by another file: $($item.FullName)" }
            Assert-QproRegularTree $item.FullName
            $manifest = Read-QproModuleManifest (Join-Path $item.FullName "module.json")
            if ($null -eq $manifest -or [string]$manifest.ModuleId -ne $item.Name) {
                throw "The Qpro module directory has no matching ownership manifest and was left untouched: $($item.FullName)"
            }
            $dlls = @(Get-ChildItem -LiteralPath $item.FullName -File -Filter "*.dll")
            # The historical registry slot can contain its restored official
            # module. Its readable non-Qpro assemblies are not ours to retire.
            if ($item.Name -eq $QproLegacyModuleId -and $dlls.Count -gt 0 -and
                @($dlls | Where-Object { $name = Get-QproAssemblyIdentity $_.FullName; $null -eq $name -or $name -eq "Qpro.GazeBridge" }).Count -eq 0) {
                continue
            }
            if ($dlls.Count -eq 0 -or @($dlls | Where-Object { (Get-QproAssemblyIdentity $_.FullName) -ne "Qpro.GazeBridge" }).Count -gt 0) {
                throw "The Qpro module directory contains an unknown DLL or no verified Qpro DLL and was left untouched: $($item.FullName)"
            }
            [pscustomobject]@{ Path = $item.FullName; Relative = $item.Name; Directory = $true; Copied = $false }
            continue
        }
        if (-not $item.PSIsContainer) {
            if ($item.Name -notlike "*.dll" -and $item.Name -notin $reservedDlls) { continue }
            $identity = Get-QproAssemblyIdentity $item.FullName
            if ($item.Name -in $reservedDlls -and $identity -ne "Qpro.GazeBridge") {
                throw "The reserved Qpro DLL path contains an unknown file and was left untouched: $($item.FullName)"
            }
            if ($identity -eq "Qpro.GazeBridge") {
                [pscustomobject]@{ Path = $item.FullName; Relative = $item.Name; Directory = $false; Copied = $item.Name -notin $reservedDlls }
            }
            continue
        }
        $guid = [guid]::Empty
        if (-not [guid]::TryParseExact($item.Name, "D", [ref]$guid) -or
            ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        # Ad-hoc copies sometimes use an official module's directory or DLL
        # filename. Remove only assembly-verified Qpro files, keeping its metadata
        # and every other file in that foreign directory.
        foreach ($dll in Get-ChildItem -LiteralPath $item.FullName -File -Filter "*.dll") {
            if ((Get-QproAssemblyIdentity $dll.FullName) -eq "Qpro.GazeBridge") {
                Assert-QproPathWithoutLinks $dll.FullName
                [pscustomobject]@{ Path = $dll.FullName; Relative = (Join-Path $item.Name $dll.Name); Directory = $false; Copied = $true }
            }
        }
    }
}

function Get-QproTreeHashes([string]$Path, [bool]$Directory) {
    if (-not $Directory) { return @{ "." = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash } }
    Assert-QproRegularTree $Path
    $hashes = @{}
    $prefix = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse -Force) {
        $hashes[$file.FullName.Substring($prefix.Length)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $hashes
}

function Assert-QproSameTree([string]$Path, [bool]$Directory, [hashtable]$Expected) {
    $actual = Get-QproTreeHashes $Path $Directory
    if ($actual.Count -ne $Expected.Count) { throw "Module files changed during the transaction: $Path" }
    foreach ($name in $Expected.Keys) {
        if (-not $actual.ContainsKey($name) -or $actual[$name] -ne $Expected[$name]) { throw "Module files changed during the transaction: $Path" }
    }
}

function Backup-QproEntries([object[]]$Entries, [string]$CustomLibs, [string]$RecoveryDirectory) {
    New-Item -ItemType Directory -Path $RecoveryDirectory -Force | Out-Null
    Assert-QproPathWithoutLinks $RecoveryDirectory
    foreach ($entry in $Entries) {
        $saved = [System.IO.Path]::GetFullPath((Join-Path $RecoveryDirectory $entry.Relative))
        $allowed = [System.IO.Path]::GetFullPath($RecoveryDirectory).TrimEnd('\') + '\'
        if (-not $saved.StartsWith($allowed, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Invalid module recovery path." }
        New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
        $entry | Add-Member -NotePropertyName Hashes -NotePropertyValue (Get-QproTreeHashes $entry.Path $entry.Directory)
        Copy-Item -LiteralPath $entry.Path -Destination $saved -Recurse
        Assert-QproSameTree $saved $entry.Directory $entry.Hashes
        $entry | Add-Member -NotePropertyName Backup -NotePropertyValue $saved
    }
}

function Move-QproEntriesToRecovery([object[]]$Entries, [string]$CustomLibs, [string]$RecoveryDirectory, [ref]$Moved) {
    foreach ($entry in $Entries) {
        Assert-QproPathWithoutLinks $entry.Path
        Assert-QproSameTree $entry.Path $entry.Directory $entry.Hashes
        $retired = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $RecoveryDirectory "retired") $entry.Relative))
        $allowed = [System.IO.Path]::GetFullPath($RecoveryDirectory).TrimEnd('\') + '\'
        if (-not $retired.StartsWith($allowed, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Invalid retired module path." }
        New-Item -ItemType Directory -Path (Split-Path -Parent $retired) -Force | Out-Null
        Assert-QproPathWithoutLinks $retired
        # Targets remain under CustomLibs and the recorded recovery directory;
        # verify the source scope immediately before a recursive directory move.
        $sourceFull = [System.IO.Path]::GetFullPath($entry.Path)
        $sourceScope = [System.IO.Path]::GetFullPath($CustomLibs).TrimEnd('\') + '\'
        if (-not $sourceFull.StartsWith($sourceScope, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Invalid installed module source path." }
        Move-Item -LiteralPath $entry.Path -Destination $retired
        $entry | Add-Member -NotePropertyName Retired -NotePropertyValue $retired
        $Moved.Value += $entry
    }
}

function Restore-QproRetiredEntries([object[]]$Moved) {
    $errors = @()
    foreach ($entry in $Moved) {
        try {
            Assert-QproPathWithoutLinks $entry.Path
            if (Test-Path -LiteralPath $entry.Path) { throw "The original module path is occupied: $($entry.Path)" }
            Assert-QproSameTree $entry.Retired $entry.Directory $entry.Hashes
            Move-Item -LiteralPath $entry.Retired -Destination $entry.Path
        } catch { $errors += $_.Exception.Message }
    }
    return $errors
}

function Assert-QproVrcftClosed {
    if (Get-Process -Name "VRCFaceTracking", "VRCFaceTracking.ModuleProcess", "ModuleProcess" -ErrorAction SilentlyContinue) {
        throw "Close VRCFaceTracking and wait for its ModuleProcess helper to exit before changing Qpro modules."
    }
}

function Find-QproCompetingSourceModules([string]$CustomLibs, [object[]]$Inventory) {
    if (-not (Test-Path -LiteralPath $CustomLibs -PathType Container)) { return }
    $ownedDirectories = @($Inventory | Where-Object Directory | ForEach-Object Path)
    $ownedFiles = @($Inventory | Where-Object { -not $_.Directory } | ForEach-Object Path)
    foreach ($item in Get-ChildItem -LiteralPath $CustomLibs -Force) {
        if ($item.FullName -in $ownedDirectories -or $item.FullName -in $ownedFiles) { continue }
        if (-not $item.PSIsContainer) {
            if ($item.Name -like "*.dll" -and $item.Name -match '(?i)steam[ ._-]*link|linkft|virtual[ ._-]*desktop') { $item.FullName }
            continue
        }
        $moduleGuid = [guid]::Empty
        if (-not [guid]::TryParseExact($item.Name, "D", [ref]$moduleGuid)) { continue }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            if ($item.Name -in @("2a8c8080-2a76-46af-bf76-1da7c0127ef8", "91a90618-b020-4064-8832-809b2ca2b3bc")) { $item.FullName }
            continue
        }
        $dlls = @(Get-ChildItem -LiteralPath $item.FullName -File -Filter "*.dll" | Where-Object { $_.FullName -notin $ownedFiles })
        if ($dlls.Count -eq 0) { continue }
        $manifestPath = Join-Path $item.FullName "module.json"
        $manifest = $null
        try { $manifest = Read-QproModuleManifest $manifestPath } catch { }
        $identity = @($item.Name, $manifest.ModuleName, $manifest.DllFileName, $manifest.ModuleId, $manifest.ModulePageUrl) -join " "
        if ($identity -match '(?i)steam[ ._-]*link|linkft|virtual[ ._-]*desktop|2a8c8080-2a76-46af-bf76-1da7c0127ef8|91a90618-b020-4064-8832-809b2ca2b3bc|7f9be083-a4f1-4e30-b28a-8e6ec878d583' -or
            @($dlls | Where-Object Name -Match '(?i)steam[ ._-]*link|linkft|virtual[ ._-]*desktop').Count -gt 0) { $item.FullName }
    }
}
