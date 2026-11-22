# Shared inventory and transaction helpers for Qpro's local VRCFT modules.
# Assembly inspection reads metadata only; no installed DLL is executed.
$QproVirtualDesktopId = "d6a8eeb2-3490-4d4f-bec1-9d5909da08ea"
$QproSteamLinkId = "5d5cb4f9-63d7-4e8f-9802-24a5d78ea6ee"
$QproLegacyModuleId = "7f9be083-a4f1-4e30-b28a-8e6ec878d583"

function Get-QproModuleVersion([string]$RuntimeRoot, [string]$SourceVersion = "3.0.0") {
    $manifestPath = Join-Path $RuntimeRoot "release-manifest.json"
    Assert-QproReadableSourcePath $manifestPath
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
        return @{ Id = $QproSteamLinkId; Dll = "000-Qpro.SteamLink.dll"; Token = "steam-link"; Name = "Steam Link"; DisplayName = ("Qpro Companion " + [char]0x2014 + " Steam Link") }
    }
    return @{ Id = $QproVirtualDesktopId; Dll = "000-Qpro.VirtualDesktop.dll"; Token = "virtual-desktop"; Name = "Virtual Desktop"; DisplayName = ("Qpro Companion " + [char]0x2014 + " Virtual Desktop") }
}

function Get-QproAcceptedModuleNames([string]$SourceName) {
    # Branding does not change module ownership. Accept old cards with the same
    # source identity so an otherwise current installation needs no repair.
    return @(
        ("Qpro Companion " + [char]0x2014 + " " + $SourceName),
        ("Qpro Companion " + [char]0x2013 + " " + $SourceName),
        ("Qpro Companion - " + $SourceName),
        ("QproFaceTracking " + [char]0x2013 + " " + $SourceName),
        ("QproFaceTracking - " + $SourceName)
    )
}

function Assert-QproDirectChild([string]$Path, [string]$Parent) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if (-not [System.IO.Path]::GetDirectoryName($full).Equals($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Module path is outside its expected directory: $full"
    }
}

function Initialize-QproSourcePathNative {
    if ("Qpro.ModuleSourcePath" -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Qpro {
    public static class ModuleSourcePath {
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { public uint Attributes; public uint Tag; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        public static uint GetTag(string path) {
            using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                AttributeTag info;
                if (!GetFileInformationByHandleEx(handle, 9, out info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return info.Tag;
            }
        }
        public static string GetFinalPath(string path) {
            using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, 0);
                if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (length >= result.Capacity) throw new InvalidOperationException("The resolved source path is too long.");
                string value = result.ToString();
                return value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + value.Substring(8) :
                    value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value.Substring(4) : value;
            }
        }
    }
}
'@
}

function Test-QproSourceReparseTag([uint32]$Tag, [bool]$Directory) {
    # Cloud Files placeholders are local files, not links to arbitrary targets.
    # Only directory links are accepted; packaged file symlinks remain rejected.
    $cloud = ($Tag -band [uint32]4294905855) -eq [uint32]2415919130 # 0xFFFF0FFF / 0x9000001A
    return $cloud -or ($Directory -and $Tag -in @([uint32]2684354563, [uint32]2684354572))
}

function Get-QproSourcePathItem([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    $tag = [uint32]0
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Initialize-QproSourcePathNative
        $tag = [Qpro.ModuleSourcePath]::GetTag($item.FullName)
    }
    return [pscustomobject]@{ FullName = $item.FullName; Directory = $item.PSIsContainer; Attributes = $item.Attributes; Tag = $tag }
}

function Assert-QproSourceAncestors([string]$Path) {
    $current = [System.IO.Path]::GetFullPath($Path)
    $existing = $null
    while (-not [string]::IsNullOrEmpty($current)) {
        $item = Get-QproSourcePathItem $current
        if ($null -ne $item) {
            if ($null -eq $existing) { $existing = $item.FullName }
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -and
                -not (Test-QproSourceReparseTag $item.Tag $item.Directory)) {
                throw "The packaged module source contains an unsupported file link or reparse point: $current"
            }
        }
        $next = [System.IO.Path]::GetDirectoryName($current)
        if ($next -eq $current) { break }
        $current = $next
    }
    return $existing
}

function Assert-QproReadableSourcePath([string]$Path) {
    # Reading a user-selected release can follow local directory links. Installed
    # modules, source preferences and transaction recovery still use the strict
    # no-links guard below. Do not use this function for a write destination.
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\', [StringComparison]::Ordinal)) { throw "Network module source paths are unsupported: $full" }
    try {
        $existing = Assert-QproSourceAncestors $full
        if ($null -ne $existing) {
            Initialize-QproSourcePathNative
            $resolved = [Qpro.ModuleSourcePath]::GetFinalPath($existing)
            if ($resolved.StartsWith('\\', [StringComparison]::Ordinal) -or
                [System.IO.Path]::GetPathRoot($resolved) -notmatch '^[A-Za-z]:\\$' -or
                ([System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($resolved))).DriveType -notin @([System.IO.DriveType]::Fixed, [System.IO.DriveType]::Removable, [System.IO.DriveType]::Ram)) {
                throw "The packaged module source resolves to a network location: $full"
            }
            $null = Assert-QproSourceAncestors $resolved
        }
    } catch {
        throw "The packaged module source could not be read safely: $($_.Exception.Message) If this folder is synced by OneDrive, choose 'Always keep on this device' for the extracted Qpro folder, then retry."
    }
}

function Get-QproModuleRecoveryRoot {
    $path = Join-Path $env:LOCALAPPDATA "QproFaceTracking\module-recovery"
    Assert-QproPathWithoutLinks $path
    return $path
}

function Get-QproModuleStagingRoot([string]$CustomLibs) {
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CustomLibs).TrimEnd('\', '/'))
    $path = Join-Path $parent 'QproModuleStaging'
    Assert-QproDirectChild $path $parent
    Assert-QproPathWithoutLinks $path
    return $path
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

function Assert-QproRegularTree([string]$Path, [switch]$ReadableSource) {
    if ($ReadableSource) {
        Assert-QproReadableSourcePath $Path
        if (((Get-Item -LiteralPath $Path -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The saved module directory is a link or cloud placeholder and was left untouched: $Path"
        }
    } else { Assert-QproPathWithoutLinks $Path }
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

function Get-QproAssemblyIdentity([string]$Path, [switch]$ReadableSource) {
    if ($ReadableSource) { Assert-QproReadableSourcePath $Path }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or (-not $ReadableSource -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) { return $null }
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
    catch { throw "The module manifest could not be read and was left untouched: $Path. $($_.Exception.Message)" }
}

function Get-QproFileHash([string]$Path, [ValidateSet("MD5", "SHA256")][string]$Algorithm = "SHA256") {
    # Get-FileHash is a module-exported function in Windows PowerShell 5.1.
    # A launcher inheriting another PowerShell's module path may not have it.
    $stream = $null
    $hasher = $null
    try {
        $hasher = if ($Algorithm -eq "MD5") { [Security.Cryptography.MD5]::Create() } else { [Security.Cryptography.SHA256]::Create() }
        $stream = [IO.File]::OpenRead($Path)
        return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '')
    } catch {
        throw "Qpro module verification could not hash '$Path' ($Algorithm): $($_.Exception.Message)"
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $hasher) { $hasher.Dispose() }
    }
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
            # Hidden DLLs are part of the complete backup inventory, so
            # ownership checks must include them too.
            $dlls = @(Get-ChildItem -LiteralPath $item.FullName -File -Filter "*.dll" -Force)
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
        foreach ($dll in Get-ChildItem -LiteralPath $item.FullName -File -Filter "*.dll" -Force) {
            if ((Get-QproAssemblyIdentity $dll.FullName) -eq "Qpro.GazeBridge") {
                Assert-QproPathWithoutLinks $dll.FullName
                [pscustomobject]@{ Path = $dll.FullName; Relative = (Join-Path $item.Name $dll.Name); Directory = $false; Copied = $true }
            }
        }
    }
}

function Assert-QproInstalledModule(
    [ValidateSet("VirtualDesktop", "SteamLink")][string]$TrackingSource,
    [string]$CustomLibs,
    [string]$PackagedModule
) {
    # Tracking reads the same GUID folder and card that the installer writes.
    # Hashing and assembly metadata inspection do not load the installed module.
    $ErrorActionPreference = "Stop"
    $identity = Get-QproModuleIdentity $TrackingSource
    $folder = Join-Path $CustomLibs $identity.Id
    $installed = Join-Path $folder $identity.Dll
    $repair = "Close VRCFaceTracking, install the Qpro $($identity.Name) module in First-time setup, then reopen VRCFaceTracking."
    Assert-QproPathWithoutLinks $CustomLibs
    Assert-QproPathWithoutLinks $installed
    if (-not (Test-Path -LiteralPath $folder -PathType Container) -or
        -not (Test-Path -LiteralPath $installed -PathType Leaf)) {
        throw "The Qpro $($identity.Name) module was not found in its current module folder: $folder. $repair"
    }
    if ((Get-QproAssemblyIdentity $installed) -ne "Qpro.GazeBridge") {
        throw "The selected module folder has no readable Qpro DLL: $folder. $repair"
    }
    $alternateId = if ($TrackingSource -eq "SteamLink") { $QproVirtualDesktopId } else { $QproSteamLinkId }
    foreach ($slot in @($alternateId, $QproLegacyModuleId, "000-Qpro.VirtualDesktop.dll", "000-Qpro.SteamLink.dll", "000-Qpro.IndependentGaze.dll")) {
        if (Test-Path -LiteralPath (Join-Path $CustomLibs $slot)) {
            throw "An alternate or legacy Qpro module slot is still present: $slot. $repair"
        }
    }
    $inventory = @(Get-QproModuleInventory $CustomLibs)
    if ($inventory.Count -ne 1 -or -not $inventory[0].Directory -or
        $inventory[0].Relative -ne $identity.Id) {
        throw "The selected Qpro DLL is not the only installed Qpro module. $repair"
    }
    $competing = @(Find-QproCompetingSourceModules $CustomLibs $inventory | Sort-Object -Unique)
    if ($competing.Count -gt 0) {
        throw "Another Virtual Desktop or Steam Link face module is installed at $($competing -join '; '). Remove that other source module through VRCFaceTracking, close VRCFaceTracking, then retry. Qpro left those files unchanged."
    }
    if (@(Get-ChildItem -LiteralPath $folder -File -Filter "*.dll" -Force).Count -ne 1) {
        throw "The module folder contains an unexpected DLL: $folder. $repair"
    }
    $cardPath = Join-Path $folder "module.json"
    # File access and hashing failures are verification errors, not evidence
    # that a valid module card needs reinstalling.
    Assert-QproPathWithoutLinks $cardPath
    $card = Read-QproModuleManifest $cardPath
    try { $json = if ($null -ne $card) { [System.IO.File]::ReadAllText($cardPath).Trim() } else { "" } }
    catch { throw "The Qpro module card could not be read for verification: $($_.Exception.Message) No module files were changed." }
    $installedMd5 = Get-QproFileHash $installed "MD5"
    try {
        # ConvertFrom-Json can discard duplicate fields in Windows PowerShell.
        # Walk validated JSON tokens to keep the Hub's card checks consistent.
        $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $tokens = [regex]::Matches($json, '"(?:[^"\\]|\\.)*"|[{}\[\]:]')
        $depth = 0
        for ($i = 0; $i -lt $tokens.Count; $i++) {
            $token = $tokens[$i].Value
            if ($token -in @('{', '[')) { $depth++; if ($depth -gt 8) { throw "The module card is nested too deeply." } }
            elseif ($token -in @('}', ']')) { $depth-- }
            elseif ($depth -eq 1 -and $token.StartsWith('"') -and $i + 1 -lt $tokens.Count -and $tokens[$i + 1].Value -eq ':') {
                $name = $token | ConvertFrom-Json -ErrorAction Stop
                if (-not $names.Add($name) -or $names.Count -gt 32) { throw "The module card has duplicate or excessive fields." }
            }
        }
        $version = $null
        if (-not $json.StartsWith('{', [StringComparison]::Ordinal) -or
            -not $json.EndsWith('}', [StringComparison]::Ordinal) -or
            $card -isnot [pscustomobject] -or @($card.PSObject.Properties).Count -gt 32 -or
            $card.ModuleId -isnot [string] -or $card.ModuleId -cne $identity.Id -or
            $card.DllFileName -isnot [string] -or $card.DllFileName -cne $identity.Dll -or
            $card.IsLocal -isnot [bool] -or -not $card.IsLocal -or
            $card.AuthorName -isnot [string] -or $card.AuthorName -cne "Fwooffy" -or
            $card.ModuleName -isnot [string] -or $card.ModuleName -cnotin @(Get-QproAcceptedModuleNames $identity.Name) -or
            $card.Version -isnot [string] -or -not [System.Version]::TryParse($card.Version, [ref]$version)) {
            throw "The module card does not match the selected Qpro source."
        }
        # FileHash is VRCFaceTracking's MD5 card field. SHA-256 below compares
        # the installed binary with this app, including same-version test builds.
        if ($card.FileHash -isnot [string] -or $card.FileHash -notmatch '^[0-9a-fA-F]{32}$' -or
            $card.FileHash -ne $installedMd5) {
            throw "The installed DLL does not match its module card's file hash."
        }
    } catch [System.Management.Automation.CommandNotFoundException] {
        throw "Qpro module verification cannot run in this PowerShell environment: $($_.Exception.Message) No module files were changed."
    } catch {
        throw "The installed Qpro module card needs repair: $($_.Exception.Message) $repair"
    }
    Assert-QproReadableSourcePath $PackagedModule
    if (-not (Test-Path -LiteralPath $PackagedModule -PathType Leaf) -or
        (Get-QproAssemblyIdentity $PackagedModule -ReadableSource) -ne "Qpro.GazeBridge") {
        throw "This app's packaged Qpro module is missing or unreadable. Extract the complete release ZIP, then retry."
    }
    if ((Get-QproFileHash $installed) -ne (Get-QproFileHash $PackagedModule)) {
        throw "The installed Qpro DLL differs from this app's module, even if both show the same version number. $repair"
    }
    return [System.IO.Path]::GetFullPath($installed)
}

function Get-QproTreeHashes([string]$Path, [bool]$Directory, [switch]$ReadableSource) {
    if (-not $Directory) { return @{ "." = Get-QproFileHash $Path } }
    Assert-QproRegularTree $Path -ReadableSource:$ReadableSource
    $hashes = @{}
    $prefix = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse -Force) {
        $hashes[$file.FullName.Substring($prefix.Length)] = Get-QproFileHash $file.FullName
    }
    return $hashes
}

function Assert-QproSameTree([string]$Path, [bool]$Directory, [hashtable]$Expected, [switch]$ReadableSource) {
    $actual = Get-QproTreeHashes $Path $Directory -ReadableSource:$ReadableSource
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
        Copy-Item -LiteralPath $entry.Path -Destination $saved -Recurse -Force
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
        Assert-QproVrcftClosed
        Move-Item -LiteralPath $entry.Path -Destination $retired
        $entry | Add-Member -NotePropertyName Retired -NotePropertyValue $retired
        $Moved.Value += $entry
        Assert-QproSameTree $retired $entry.Directory $entry.Hashes
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
            if ($item.Name -in @("2a8c8080-2a76-46af-bf76-1da7c0127ef8", "91a90618-b020-4064-8832-809b2ca2b3bc", "b146eda9-be48-4016-ab63-680a694064bd")) { $item.FullName }
            continue
        }
        $dlls = @(Get-ChildItem -LiteralPath $item.FullName -File -Filter "*.dll" -Force | Where-Object { $_.FullName -notin $ownedFiles })
        if ($dlls.Count -eq 0) { continue }
        $manifestPath = Join-Path $item.FullName "module.json"
        $manifest = $null
        try { $manifest = Read-QproModuleManifest $manifestPath } catch { }
        $identity = @($item.Name, $manifest.ModuleName, $manifest.DllFileName, $manifest.ModuleId, $manifest.ModulePageUrl) -join " "
        if ($identity -match '(?i)steam[ ._-]*link|linkft|virtual[ ._-]*desktop|2a8c8080-2a76-46af-bf76-1da7c0127ef8|91a90618-b020-4064-8832-809b2ca2b3bc|b146eda9-be48-4016-ab63-680a694064bd|7f9be083-a4f1-4e30-b28a-8e6ec878d583' -or
            @($dlls | Where-Object Name -Match '(?i)steam[ ._-]*link|linkft|virtual[ ._-]*desktop').Count -gt 0) { $item.FullName }
    }
}

function Get-QproSourceModuleProfile([string]$ModuleId) {
    switch ($ModuleId.ToLowerInvariant()) {
        '91a90618-b020-4064-8832-809b2ca2b3bc' {
            return @{ Id = $ModuleId.ToLowerInvariant(); Provider = 'Virtual Desktop'; Name = 'Virtual Desktop'; Author = 'Virtual Desktop, Inc.'; Dll = 'VirtualDesktop.FaceTracking.dll'; Assembly = 'VirtualDesktop.FaceTracking' }
        }
        '2a8c8080-2a76-46af-bf76-1da7c0127ef8' {
            return @{ Id = $ModuleId.ToLowerInvariant(); Provider = 'Steam Link'; Name = 'SteamLink VRCFT Module'; Author = 'Ykeara'; Dll = 'SteamLinkVRCFTModule.dll'; Assembly = 'SteamLinkVRCFTModule' }
        }
        'b146eda9-be48-4016-ab63-680a694064bd' {
            return @{ Id = $ModuleId.ToLowerInvariant(); Provider = 'Steam Link'; Name = 'SteamLink VRCFT'; Author = 'danwillm'; Dll = 'SLExtTrackingModule.dll'; Assembly = 'SLExtTrackingModule' }
        }
    }
    return $null
}

function Get-QproRecognizedSourceModuleEntry([string]$Path, [string]$CustomLibs) {
    Assert-QproDirectChild $Path $CustomLibs
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or -not $item.PSIsContainer) { return $null }
    $profile = Get-QproSourceModuleProfile $item.Name
    if ($null -eq $profile) { return $null }
    Assert-QproRegularTree $item.FullName
    $manifestPath = Join-Path $item.FullName 'module.json'
    $manifest = Read-QproModuleManifest $manifestPath
    if ($null -eq $manifest) { return $null }
    $json = [IO.File]::ReadAllText($manifestPath).Trim()
    if (-not $json.StartsWith('{', [StringComparison]::Ordinal) -or
        -not $json.EndsWith('}', [StringComparison]::Ordinal)) { return $null }
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $tokens = [regex]::Matches($json, '"(?:[^"\\]|\\.)*"|[{}\[\]:]')
    $depth = 0
    for ($index = 0; $index -lt $tokens.Count; $index++) {
        $token = $tokens[$index].Value
        if ($token -in @('{', '[')) { $depth++; if ($depth -gt 8) { return $null } }
        elseif ($token -in @('}', ']')) { $depth-- }
        elseif ($depth -eq 1 -and $token.StartsWith('"') -and $index + 1 -lt $tokens.Count -and $tokens[$index + 1].Value -eq ':') {
            $key = $token | ConvertFrom-Json -ErrorAction Stop
            if (-not $names.Add($key) -or $names.Count -gt 32) { return $null }
        }
    }
    if ($manifest -isnot [pscustomobject] -or $manifest.ModuleId -isnot [string] -or
        $manifest.ModuleId -ne $profile.Id -or $manifest.ModuleName -isnot [string] -or
        $manifest.ModuleName -ne $profile.Name -or $manifest.AuthorName -isnot [string] -or
        $manifest.AuthorName -ne $profile.Author -or $manifest.DllFileName -isnot [string] -or
        $manifest.DllFileName -ne $profile.Dll) { return $null }
    $main = Join-Path $item.FullName $profile.Dll
    if (-not (Test-Path -LiteralPath $main -PathType Leaf) -or
        (Get-QproAssemblyIdentity $main) -ne $profile.Assembly) { return $null }
    # A copied Qpro DLL belongs to the narrower migration inventory. Never
    # retire its surrounding foreign folder as a second overlapping entry.
    foreach ($dll in Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Filter '*.dll' -Force) {
        if ((Get-QproAssemblyIdentity $dll.FullName) -eq 'Qpro.GazeBridge') { return $null }
    }
    return [pscustomobject]@{ Path = $item.FullName; Relative = $item.Name; Directory = $true;
        Copied = $false; SourceModuleId = $profile.Id; Provider = $profile.Provider }
}

function Get-QproModuleInstallInventory([string]$CustomLibs) {
    $own = @(Get-QproModuleInventory $CustomLibs)
    $sources = @()
    $unknown = @()
    foreach ($path in @(Find-QproCompetingSourceModules $CustomLibs $own | Sort-Object -Unique)) {
        $entry = Get-QproRecognizedSourceModuleEntry $path $CustomLibs
        if ($null -eq $entry) { $unknown += $path } else { $sources += $entry }
    }
    if ($unknown.Count -gt 0) {
        throw "Another Virtual Desktop or Steam Link module is installed at $($unknown -join '; '). Its identity could not be verified for automatic backup. Remove it through VRCFaceTracking, close VRCFaceTracking, then retry. Qpro left those files unchanged."
    }
    return @($own) + @($sources)
}

function Assert-QproModuleTransactionVolume([string]$CustomLibs, [string]$RecoveryDirectory) {
    Assert-QproPathWithoutLinks $CustomLibs
    Assert-QproPathWithoutLinks $RecoveryDirectory
    $sourceRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($CustomLibs))
    $recoveryRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($RecoveryDirectory))
    if (-not $sourceRoot.Equals($recoveryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Automatic module backup and recovery require CustomLibs and Qpro's recovery folder on the same drive. No installed module files were changed. Remove the other source module through VRCFaceTracking or restore its saved module manually, then retry. CustomLibs: $CustomLibs Recovery: $RecoveryDirectory"
    }
}

function Write-QproSourceModuleReceipt([string]$RecoveryDirectory, [string]$TransactionId,
    [ValidateSet('prepared', 'committed', 'rolled-back')][string]$State, [object[]]$Entries) {
    $sources = @($Entries | Where-Object { $_.SourceModuleId })
    if ($sources.Count -eq 0) { return }
    Assert-QproPathWithoutLinks $RecoveryDirectory
    $modules = @($sources | ForEach-Object {
        [ordered]@{ moduleId = $_.SourceModuleId; provider = $_.Provider; backup = $_.Relative; hashes = $_.Hashes }
    })
    $record = [ordered]@{ format = 'qpro-source-module-backup-v1'; state = $State;
        transactionId = $TransactionId; createdUtc = [DateTime]::UtcNow.ToString('o'); modules = $modules }
    $json = $record | ConvertTo-Json -Depth 8
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 1048576) { throw 'The saved source-module receipt exceeds its size limit.' }
    $receipt = Join-Path $RecoveryDirectory 'source-modules.json'
    $temporary = Join-Path $RecoveryDirectory ('source-modules-' + [guid]::NewGuid().ToString('N') + '.tmp')
    Assert-QproPathWithoutLinks $receipt
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $receipt -Force
}

function Read-QproSourceModuleReceipt([string]$Directory, [string]$RecoveryRoot) {
    Assert-QproDirectChild $Directory $RecoveryRoot
    Assert-QproPathWithoutLinks $Directory
    $name = [IO.Path]::GetFileName($Directory)
    if ($name -notmatch '^vrcft-qpro-module-switch-backup-([0-9a-f]{32})$') { throw 'The source-module recovery directory has an invalid identity.' }
    $transactionId = $Matches[1]
    $path = Join-Path $Directory 'source-modules.json'
    Assert-QproPathWithoutLinks $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path -Force).Length -gt 1048576) { throw 'The source-module recovery receipt is missing or too large.' }
    $receiptHash = Get-QproFileHash $path
    try {
        $json = [IO.File]::ReadAllText($path).Trim()
        if (-not $json.StartsWith('{', [StringComparison]::Ordinal) -or
            -not $json.EndsWith('}', [StringComparison]::Ordinal)) { throw 'Receipt root must be an object.' }
        $record = $json | ConvertFrom-Json -ErrorAction Stop
    }
    catch { throw 'The source-module recovery receipt could not be read.' }
    if ((Get-QproFileHash $path) -ne $receiptHash) { throw 'The source-module recovery receipt changed while being read.' }
    if ($record -isnot [pscustomobject] -or $record.format -cne 'qpro-source-module-backup-v1' -or
        $record.transactionId -cne $transactionId -or $record.state -notin @('prepared', 'committed', 'rolled-back', 'restored')) {
        throw 'The source-module recovery receipt has an unsupported identity or state.'
    }
    if ($record.state -eq 'rolled-back') { return @() }
    $modules = @($record.modules)
    if ($modules.Count -lt 1 -or $modules.Count -gt 3) { throw 'The source-module recovery receipt has an invalid module count.' }
    $seen = @{}
    $entries = @()
    foreach ($module in $modules) {
        $profile = if ($module.moduleId -is [string]) { Get-QproSourceModuleProfile $module.moduleId } else { $null }
        if ($null -eq $profile -or $seen.ContainsKey($profile.Id) -or
            $module.backup -isnot [string] -or $module.backup -ne $profile.Id -or
            $module.provider -isnot [string] -or $module.provider -ne $profile.Provider -or
            $module.hashes -isnot [pscustomobject]) { throw 'The source-module recovery receipt contains an unrecognized module.' }
        $seen[$profile.Id] = $true
        $hashes = @{}
        foreach ($property in $module.hashes.PSObject.Properties) {
            if ([string]::IsNullOrEmpty($property.Name) -or
                $property.Name -match '(^[\\/]|[<>:"|?*\x00-\x1f]|(^|[\\/])\.{1,2}([\\/]|$)|[\\/]{2})' -or
                $property.Value -isnot [string] -or $property.Value -notmatch '^[0-9a-fA-F]{64}$' -or
                $hashes.ContainsKey($property.Name)) { throw 'The source-module recovery receipt contains invalid file hashes.' }
            $hashes[$property.Name] = $property.Value
        }
        if ($hashes.Count -lt 2 -or $hashes.Count -gt 4096) { throw 'The source-module recovery receipt has an invalid file inventory.' }
        $backup = Join-Path $Directory $module.backup
        Assert-QproDirectChild $backup $Directory
        $entries += [pscustomobject]@{ ModuleId = $profile.Id; Provider = $profile.Provider; Backup = $backup;
            Hashes = $hashes; Consumed = $record.state -eq 'restored'; ReceiptHash = $receiptHash }
    }
    return $entries
}

function Complete-QproSourceModuleReceipt([string]$Directory, [string]$ExpectedHash) {
    $path = Join-Path $Directory 'source-modules.json'
    Assert-QproPathWithoutLinks $path
    if ((Get-QproFileHash $path) -ne $ExpectedHash) { throw 'The source-module recovery receipt changed during restoration.' }
    $record = [IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop
    $record.state = 'restored'
    $temporary = Join-Path $Directory ('source-modules-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Assert-QproPathWithoutLinks $path
    if ((Get-QproFileHash $path) -ne $ExpectedHash) { throw 'The source-module recovery receipt changed during restoration.' }
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

function Restore-QproSourceModuleBackups([string]$CustomLibs, [string]$RecoveryRoot) {
    $restored = 0; $issues = @(); $handled = @{}; $historicalBlocked = $false
    Assert-QproPathWithoutLinks $RecoveryRoot
    if (-not (Test-Path -LiteralPath $RecoveryRoot -PathType Container)) { return @{ Restored = 0; Issues = @(); HandledModuleIds = @(); HistoricalBlocked = $false } }
    if (@(Get-QproModuleInventory $CustomLibs).Count -ne 0) { throw 'Remove the Qpro modules before restoring saved source modules.' }
    $directories = @(Get-ChildItem -LiteralPath $RecoveryRoot -Directory -Force |
        Where-Object { $_.Name -match '^vrcft-qpro-module-switch-backup-[0-9a-f]{32}$' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'source-modules.json')) } |
        Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'source-modules.json') -Force).LastWriteTimeUtc } -Descending)
    foreach ($directory in $directories) {
        try { $entries = @(Read-QproSourceModuleReceipt $directory.FullName $RecoveryRoot) }
        catch {
            $issues += "Saved source-module receipt needs attention at $($directory.FullName): $($_.Exception.Message)"
            # Its provider identity is unknown, so older snapshots cannot be
            # selected safely as a substitute for this newer transaction.
            $historicalBlocked = $true
            break
        }
        $complete = $entries.Count -gt 0
        foreach ($entry in $entries) {
            if ($handled.ContainsKey($entry.ModuleId)) { $complete = $false; continue }
            # A damaged newer snapshot must not silently select an older
            # provider configuration. Keep it for review instead.
            $handled[$entry.ModuleId] = $true
            if ($entry.Consumed) { $complete = $false; continue }
            $target = Join-Path $CustomLibs $entry.ModuleId
            try {
                Assert-QproDirectChild $target $CustomLibs
                Assert-QproPathWithoutLinks $target
                Assert-QproSameTree $entry.Backup $true $entry.Hashes
                if ($null -eq (Get-QproRecognizedSourceModuleEntry $entry.Backup $directory.FullName)) { throw 'The saved provider no longer has its recognized module identity.' }
                if (Test-Path -LiteralPath $target) {
                    $handled[$entry.ModuleId] = $true
                    try {
                        Assert-QproSameTree $target $true $entry.Hashes
                        if ($null -eq (Get-QproRecognizedSourceModuleEntry $target $CustomLibs)) { throw 'The original path contains another module.' }
                        Write-Host "The saved $($entry.Provider) module is already restored at $target."
                    } catch { $complete = $false; $issues += "The saved $($entry.Provider) module remains at $($entry.Backup) because its original path is occupied. No installed files were overwritten." }
                    continue
                }
                Assert-QproVrcftClosed
                Assert-QproModuleTransactionVolume $CustomLibs $RecoveryRoot
                New-Item -ItemType Directory -Path $CustomLibs -Force | Out-Null
                # Restore beside CustomLibs on the same drive, never within
                # VRCFT's discovery tree while a module card is being copied.
                $stageRoot = Get-QproModuleStagingRoot $CustomLibs
                New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
                Assert-QproPathWithoutLinks $stageRoot
                $stage = Join-Path $stageRoot ('restore-' + [guid]::NewGuid().ToString('N'))
                Assert-QproDirectChild $stage $stageRoot
                Assert-QproSameTree $entry.Backup $true $entry.Hashes
                Copy-Item -LiteralPath $entry.Backup -Destination $stage -Recurse -Force
                Assert-QproSameTree $stage $true $entry.Hashes
                Assert-QproVrcftClosed
                Assert-QproSameTree $entry.Backup $true $entry.Hashes
                Assert-QproSameTree $stage $true $entry.Hashes
                Assert-QproPathWithoutLinks $target
                if (Test-Path -LiteralPath $target) { throw 'The original provider path appeared during recovery and was left untouched.' }
                Move-Item -LiteralPath $stage -Destination $target
                Assert-QproSameTree $target $true $entry.Hashes
                $handled[$entry.ModuleId] = $true
                $restored++
                Write-Host "Restored the saved $($entry.Provider) VRCFaceTracking module: $target. Its verified recovery copy remains at $($entry.Backup)."
            } catch { $complete = $false; $issues += "The saved $($entry.Provider) module could not be restored from $($entry.Backup): $($_.Exception.Message)" }
        }
        if ($complete) {
            try { Complete-QproSourceModuleReceipt $directory.FullName $entries[0].ReceiptHash }
            catch { $issues += "The provider was restored, but its recovery receipt needs attention: $($_.Exception.Message)" }
        }
    }
    return @{ Restored = $restored; Issues = @($issues); HandledModuleIds = @($handled.Keys); HistoricalBlocked = $historicalBlocked }
}
