using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

// Match the installer identities. A legacy loose DLL is detected for migration,
// while current readiness requires one Qpro-owned directory and module card.
internal static class HubModuleInstallation
{
    internal const string VirtualDesktopId = "d6a8eeb2-3490-4d4f-bec1-9d5909da08ea";
    internal const string SteamLinkId = "5d5cb4f9-63d7-4e8f-9802-24a5d78ea6ee";
    internal const string LegacyId = "7f9be083-a4f1-4e30-b28a-8e6ec878d583";
    private static readonly string[] LegacyDlls = ["000-Qpro.VirtualDesktop.dll", "000-Qpro.SteamLink.dll", "000-Qpro.IndependentGaze.dll"];

    internal static bool HasInstalled(string customLibs)
    {
        try { return FindQproDlls(customLibs).Count > 0; }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    internal static bool HasSavedBackups(string research)
    {
        try
        {
            if (!RegularPath(research, directory: true)) return false;
            return Directory.EnumerateDirectories(research).Any(path => RegularPath(path, directory: true) &&
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
    {
        try
        {
            var id = steamLink ? SteamLinkId : VirtualDesktopId;
            var dllName = steamLink ? "000-Qpro.SteamLink.dll" : "000-Qpro.VirtualDesktop.dll";
            var folder = Path.Combine(customLibs, id);
            var installed = Path.Combine(folder, dllName);
            if (!RegularPath(folder, directory: true) || !RegularPath(suppliedDll, directory: false) ||
                !IsQproAssembly(installed)) return false;
            var qproDlls = FindQproDlls(customLibs);
            if (qproDlls.Count != 1 || !Path.GetFullPath(qproDlls[0]).Equals(Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
                return false;
            // Another file occupying a Qpro-owned slot needs migration/review,
            // even if it was not a readable Qpro assembly during enumeration.
            var alternate = Path.Combine(customLibs, steamLink ? VirtualDesktopId : SteamLinkId);
            if (Directory.Exists(alternate) || File.Exists(alternate) || Directory.Exists(Path.Combine(customLibs, LegacyId)) ||
                LegacyDlls.Any(name => File.Exists(Path.Combine(customLibs, name)) || Directory.Exists(Path.Combine(customLibs, name)))) return false;
            if (Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).Count() != 1) return false;
            var metadataPath = Path.Combine(folder, "module.json");
            if (!RegularPath(metadataPath, directory: false) || new FileInfo(metadataPath).Length > 65536) return false;
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath), new JsonDocumentOptions { MaxDepth = 8 });
            var data = metadata.RootElement;
            if (data.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (data.EnumerateObject().Any(property => !names.Add(property.Name)) || names.Count > 32) return false;
            if (data.GetProperty("ModuleId").GetString() != id || data.GetProperty("DllFileName").GetString() != dllName ||
                data.GetProperty("IsLocal").ValueKind != JsonValueKind.True || data.GetProperty("AuthorName").GetString() != "Fwooffy" ||
                data.GetProperty("ModuleName").GetString() != "QproFaceTracking - " + (steamLink ? "Steam Link" : "Virtual Desktop") ||
                !Version.TryParse(data.GetProperty("Version").GetString(), out _)) return false;
            var recordedHash = data.GetProperty("FileHash").GetString();
            using var installedStream = File.OpenRead(installed);
            var metadataHash = Convert.ToHexString(MD5.HashData(installedStream));
            if (!metadataHash.Equals(recordedHash, StringComparison.OrdinalIgnoreCase)) return false;
            installedStream.Position = 0;
            using var suppliedStream = File.OpenRead(suppliedDll);
            return SHA256.HashData(installedStream).AsSpan().SequenceEqual(SHA256.HashData(suppliedStream));
        }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
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

    private static bool RegularPath(string path, bool directory)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || (directory ? !Directory.Exists(full) : !File.Exists(full))) return false;
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
        return true;
    }

    private static bool IsInspectionError(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or BadImageFormatException;
}
