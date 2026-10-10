using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace QproFaceTracking.Hub;

internal enum HubModuleState { Missing, Legacy, WrongSource, Conflict, Invalid, UpdateNeeded, Current, Unreadable }
internal sealed record HubModuleInspection(HubModuleState State, string Status, string Detail, string ExpectedDirectory)
{
    internal bool IsCurrent => State == HubModuleState.Current;
}

// Match the installer identities. A legacy loose DLL is detected for migration,
// while current readiness requires one Qpro-owned directory and module card.
internal static class HubModuleInstallation
{
    internal const string VirtualDesktopId = "d6a8eeb2-3490-4d4f-bec1-9d5909da08ea";
    internal const string SteamLinkId = "5d5cb4f9-63d7-4e8f-9802-24a5d78ea6ee";
    internal const string LegacyId = "7f9be083-a4f1-4e30-b28a-8e6ec878d583";
    private static readonly string[] LegacyDlls = ["000-Qpro.VirtualDesktop.dll", "000-Qpro.SteamLink.dll", "000-Qpro.IndependentGaze.dll"];
    private static readonly string[] OtherSourceIds = ["2a8c8080-2a76-46af-bf76-1da7c0127ef8", "91a90618-b020-4064-8832-809b2ca2b3bc", LegacyId];
    private static readonly Regex OtherSourceName = new(@"steam[ ._-]*link|linkft|virtual[ ._-]*desktop", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool HasInstalled(string customLibs)
    {
        try { return FindQproDlls(customLibs).Count > 0; }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    internal static bool HasSavedBackups(string research)
    {
        try
        {
            if (!ReadableSourcePath(research, directory: true)) return false;
            return Directory.EnumerateDirectories(research).Any(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) &&
                ReadableSourcePath(path, directory: true) &&
                Regex.IsMatch(Path.GetFileName(path), @"^(vrcft-legacy-registry-module-backup|vrcft-official-virtual-desktop-backup(?:-\d{8}-\d{6}|-[0-9a-f]{32})?)$", RegexOptions.IgnoreCase));
        }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    internal static bool TryGetInstalledSource(string customLibs, bool legacySteamLink, out bool steamLink)
    {
        steamLink = false;
        try
        {
            var dlls = FindQproDlls(customLibs);
            if (dlls.Count != 1) return false;
            switch (Path.GetFileName(dlls[0]).ToLowerInvariant())
            {
                case "000-qpro.steamlink.dll": steamLink = true; return true;
                case "000-qpro.virtualdesktop.dll": return true;
                // The old combined module follows the saved source preference.
                case "000-qpro.independentgaze.dll": steamLink = legacySteamLink; return true;
                default: return false;
            }
        }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    internal static bool IsCurrent(string customLibs, string suppliedDll, bool steamLink)
        => Inspect(customLibs, suppliedDll, steamLink).IsCurrent;

    internal static HubModuleInspection Inspect(string customLibs, string suppliedDll, bool steamLink)
    {
        var source = steamLink ? "Steam Link" : "Virtual Desktop";
        var id = steamLink ? SteamLinkId : VirtualDesktopId;
        var dllName = steamLink ? "000-Qpro.SteamLink.dll" : "000-Qpro.VirtualDesktop.dll";
        var folder = Path.Combine(customLibs, id);
        HubModuleInspection Result(HubModuleState state, string status, string detail)
            => new(state, status, detail, folder);
        var repair = $"Close VRCFaceTracking, install the Qpro {source} module in First-time setup, then reopen VRCFaceTracking.";
        try
        {
            var installed = Path.Combine(folder, dllName);
            var qproDlls = FindQproDlls(customLibs);
            if (qproDlls.Count > 1)
                return Result(HubModuleState.Conflict, "Multiple Qpro modules", "More than one Qpro DLL is installed. " + repair);
            if (!RegularPath(folder, directory: true) || !IsQproAssembly(installed))
            {
                if (Directory.Exists(folder) || File.Exists(folder))
                    return Result(HubModuleState.Invalid, "Module needs repair", $"The selected module folder has no readable Qpro DLL: {folder}. " + repair);
                if (qproDlls.Count == 0)
                    return Result(HubModuleState.Missing, "Setup needed", $"The Qpro {source} module was not found at {folder}. " + repair);
                if (TryGetInstalledSource(customLibs, steamLink, out var installedSteamLink) && installedSteamLink != steamLink)
                    return Result(HubModuleState.WrongSource, "Different source installed",
                        $"The installed Qpro module uses {(installedSteamLink ? "Steam Link" : "Virtual Desktop")}, but this session selects {source}. " + repair);
                return Result(HubModuleState.Legacy, "Legacy module installed", $"A Qpro DLL is installed outside the current module folder {folder}. " + repair);
            }
            if (qproDlls.Count != 1 || !Path.GetFullPath(qproDlls[0]).Equals(Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
                return Result(HubModuleState.Conflict, "Module conflict", "The selected Qpro DLL is not the only installed Qpro module. " + repair);
            // Another file occupying a Qpro-owned slot needs migration/review,
            // even if it was not a readable Qpro assembly during enumeration.
            var alternate = Path.Combine(customLibs, steamLink ? VirtualDesktopId : SteamLinkId);
            if (Directory.Exists(alternate) || File.Exists(alternate) || Directory.Exists(Path.Combine(customLibs, LegacyId)) ||
                LegacyDlls.Any(name => File.Exists(Path.Combine(customLibs, name)) || Directory.Exists(Path.Combine(customLibs, name))))
                return Result(HubModuleState.Conflict, "Module conflict", "An alternate or legacy module slot is still present. " + repair);
            if (Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).Count() != 1)
                return Result(HubModuleState.Invalid, "Module needs repair", $"The module folder contains an unexpected DLL: {folder}. " + repair);
            if (!RegularModuleTree(folder))
                return Result(HubModuleState.Invalid, "Module folder needs repair", $"The module folder contains a linked entry or exceeds its file inventory limit: {folder}. " + repair);
            var competingSources = FindCompetingSourceModules(customLibs, folder);
            if (competingSources.Count > 0)
                return Result(HubModuleState.Conflict, "Face source conflict",
                    $"Another Virtual Desktop or Steam Link face module is installed at {string.Join("; ", competingSources)}. Remove that other source module through VRCFaceTracking, close VRCFaceTracking, then retry. Qpro left those files unchanged.");
            var metadataPath = Path.Combine(folder, "module.json");
            if (!RegularPath(metadataPath, directory: false) || new FileInfo(metadataPath).Length > 65536)
                return Result(HubModuleState.Invalid, "Module card needs repair", $"The module card is missing, linked or too large: {metadataPath}. " + repair);
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath), new JsonDocumentOptions { MaxDepth = 8 });
            var data = metadata.RootElement;
            if (data.ValueKind != JsonValueKind.Object)
                return Result(HubModuleState.Invalid, "Module card needs repair", "The installed module card is not a JSON object. " + repair);
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (data.EnumerateObject().Any(property => !names.Add(property.Name)) || names.Count > 32)
                return Result(HubModuleState.Invalid, "Module card needs repair", "The installed module card has duplicate or excessive fields. " + repair);
            if (data.GetProperty("ModuleId").GetString() != id || data.GetProperty("DllFileName").GetString() != dllName ||
                data.GetProperty("IsLocal").ValueKind != JsonValueKind.True || data.GetProperty("AuthorName").GetString() != "Fwooffy" ||
                data.GetProperty("ModuleName").GetString() != "QproFaceTracking - " + (steamLink ? "Steam Link" : "Virtual Desktop") ||
                !Version.TryParse(data.GetProperty("Version").GetString(), out _))
                return Result(HubModuleState.Invalid, "Module card needs repair", "The module card does not match the selected Qpro source. " + repair);
            var recordedHash = data.GetProperty("FileHash").GetString();
            using var installedStream = File.OpenRead(installed);
            var metadataHash = Convert.ToHexString(MD5.HashData(installedStream));
            if (!metadataHash.Equals(recordedHash, StringComparison.OrdinalIgnoreCase))
                return Result(HubModuleState.Invalid, "Module card needs repair", "The installed DLL does not match its module card's file hash. " + repair);
            if (!IsPackagedQproAssembly(suppliedDll))
                return Result(HubModuleState.Unreadable, "App package needs repair", "This app's packaged Qpro module is missing or unreadable. Extract the complete release ZIP to a local folder. For OneDrive, choose 'Always keep on this device' for the extracted folder, then retry.");
            installedStream.Position = 0;
            using var suppliedStream = File.OpenRead(suppliedDll);
            if (!SHA256.HashData(installedStream).AsSpan().SequenceEqual(SHA256.HashData(suppliedStream)))
                return Result(HubModuleState.UpdateNeeded, "Module update needed", "The installed Qpro DLL differs from this app's module, even if both show the same version number. " + repair);
            return Result(HubModuleState.Current, "Installed · input unchecked",
                $"The Qpro {source} module card and DLL match this app at {folder}. This verifies installed files; confirm live expressions in VRCFaceTracking's preview.");
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            if (error is JsonException or KeyNotFoundException or InvalidOperationException)
                return Result(HubModuleState.Invalid, "Module card needs repair", "The installed module card is incomplete or invalid. " + repair);
            return Result(HubModuleState.Unreadable, "Module check needs attention", "The module files could not be verified: " + error.Message + " " + repair);
        }
    }

    private static List<string> FindQproDlls(string customLibs)
    {
        var found = new List<string>();
        if (!RegularPath(customLibs, directory: true)) return found;
        var entries = new DirectoryInfo(customLibs).EnumerateFileSystemInfos().Take(513).ToArray();
        if (entries.Length > 512) throw new FormatException("Module inventory exceeded its limit.");
        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (entry is FileInfo file && file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (IsQproAssembly(file.FullName)) found.Add(file.FullName);
            }
            else if (entry is DirectoryInfo directory && Guid.TryParseExact(directory.Name, "D", out _))
            {
                var dlls = directory.EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly).Take(65).ToArray();
                if (dlls.Length > 64) throw new FormatException("Module directory exceeded its limit.");
                foreach (var dll in dlls) if (IsQproAssembly(dll.FullName)) found.Add(dll.FullName);
            }
        }
        return found;
    }

    private static bool IsQproAssembly(string path)
    {
        try { return RegularPath(path, directory: false) && AssemblyName.GetAssemblyName(path).Name == "Qpro.GazeBridge"; }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    private static List<string> FindCompetingSourceModules(string customLibs, string ownFolder)
    {
        // Match the installer's source hints without loading unrelated DLLs.
        // Unknown modules are preserved; only their names and cards are read.
        var found = new List<string>();
        var installedFolder = Path.GetFullPath(ownFolder);
        foreach (var entry in new DirectoryInfo(customLibs).EnumerateFileSystemInfos())
        {
            if (entry.FullName.Equals(installedFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry is FileInfo file)
            {
                if (file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) && OtherSourceName.IsMatch(file.Name)) found.Add(file.FullName);
                continue;
            }
            if (entry is not DirectoryInfo directory || !Guid.TryParseExact(directory.Name, "D", out _)) continue;
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (OtherSourceIds.Take(2).Contains(directory.Name, StringComparer.OrdinalIgnoreCase)) found.Add(directory.FullName);
                continue;
            }
            var dlls = directory.EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly).Take(65).ToArray();
            if (dlls.Length > 64) throw new FormatException("Module directory exceeded its limit.");
            if (dlls.Length == 0) continue;
            var identity = directory.Name;
            var cardPath = Path.Combine(directory.FullName, "module.json");
            try
            {
                if (RegularPath(cardPath, directory: false) && new FileInfo(cardPath).Length <= 65536)
                {
                    using var card = JsonDocument.Parse(File.ReadAllText(cardPath));
                    if (card.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var name in new[] { "ModuleName", "DllFileName", "ModuleId", "ModulePageUrl" })
                            if (card.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                                identity += " " + value.GetString();
                }
            }
            catch (Exception error) when (IsInspectionError(error)) { }
            if (OtherSourceName.IsMatch(identity) || OtherSourceIds.Any(id => identity.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                dlls.Any(dll => OtherSourceName.IsMatch(dll.Name))) found.Add(directory.FullName);
        }
        return found;
    }

    private static bool IsPackagedQproAssembly(string path)
    {
        try { return ReadableSourcePath(path) && AssemblyName.GetAssemblyName(path).Name == "Qpro.GazeBridge"; }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    internal static bool IsReadablePackageSource(string path)
    {
        try { return ReadableSourcePath(path); }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    private static bool RegularPath(string path, bool directory)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || (directory ? !Directory.Exists(full) : !File.Exists(full))) return false;
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
        return true;
    }

    private static bool RegularModuleTree(string path)
    {
        // Match PowerShell's installed-tree preflight. A readable top-level DLL
        // is insufficient if another entry redirects a later module operation.
        var pending = new Stack<string>();
        pending.Push(path);
        var count = 0;
        while (pending.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
            {
                if (++count > 4096 || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (entry is DirectoryInfo directory) pending.Push(directory.FullName);
            }
        }
        return true;
    }

    // Package sources are read-only. Directory links and hydrated Cloud Files
    // are valid locations for a release, while installed module paths stay strict.
    private static bool ReadableSourcePath(string path, bool directory = false)
    {
        var full = Path.GetFullPath(path);
        if (!LocalDrivePath(full) || (directory ? !Directory.Exists(full) : !File.Exists(full))) return false;
        if (!OperatingSystem.IsWindows()) return RegularPath(full, directory);
        if (!SupportedSourceAncestors(full)) return false;
        using var handle = directory ? CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero) :
            File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (handle.IsInvalid) return false;
        var finalPath = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0 || length >= finalPath.Capacity) return false;
        var resolved = finalPath.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return LocalDrivePath(resolved) && SupportedSourceAncestors(resolved);
    }

    private static bool LocalDrivePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)) return false;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        var driveType = new DriveInfo(root).DriveType;
        return driveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
    }

    private static bool SupportedSourceAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            const uint shareReadWriteDelete = 7, openExisting = 3, openReparsePointWithDirectoryAccess = 0x02200000;
            const int fileAttributeTagInfoClass = 9;
            using var handle = CreateFile(current, 0, shareReadWriteDelete, IntPtr.Zero, openExisting, openReparsePointWithDirectoryAccess, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, fileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
                !SupportedSourceReparseTag(info.ReparseTag, attributes.HasFlag(FileAttributes.Directory))) return false;
        }
        return true;
    }

    internal static bool SupportedSourceReparseTag(uint tag, bool directory)
        => (tag & 0xffff0fffu) == 0x9000001au || (directory && tag is 0xa0000003u or 0xa000000cu);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { internal uint FileAttributes; internal uint ReparseTag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    private static bool IsInspectionError(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or BadImageFormatException;
}
