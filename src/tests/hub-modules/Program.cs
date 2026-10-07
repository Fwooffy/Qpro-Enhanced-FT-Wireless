using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using QproFaceTracking.Hub;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
if (args.Length != 1 || !File.Exists(args[0]) || AssemblyName.GetAssemblyName(args[0]).Name != "Qpro.GazeBridge")
    throw new Exception("Pass an existing packaged Qpro DLL for copying and metadata inspection. No DLL is loaded or executed.");
var fixture = Path.Combine(Path.GetTempPath(), "qpro-module-detection-" + Guid.NewGuid().ToString("N"));
var customLibs = Path.Combine(fixture, "CustomLibs");
Directory.CreateDirectory(customLibs);
var research = Path.Combine(fixture, "research");
Directory.CreateDirectory(research);
Check(!HubModuleInstallation.HasSavedBackups(research), "An empty research folder enabled uninstall.");
Directory.CreateDirectory(Path.Combine(research, "vrcft-qpro-module-uninstall-backup-fixture"));
Directory.CreateDirectory(Path.Combine(research, "vrcft-official-virtual-desktop-backup-unrelated"));
Check(!HubModuleInstallation.HasSavedBackups(research), "Rollback copies or unrelated names enabled uninstall.");
foreach (var name in new[] { "vrcft-legacy-registry-module-backup", "vrcft-official-virtual-desktop-backup",
    "vrcft-official-virtual-desktop-backup-20260101-120000", "vrcft-official-virtual-desktop-backup-0123456789abcdef0123456789abcdef" })
{
    var path = Path.Combine(research, name);
    Directory.CreateDirectory(path);
    Check(HubModuleInstallation.HasSavedBackups(research), "A recognized recovery folder did not enable uninstall: " + name);
    Directory.Delete(path);
}
var supplied = Path.Combine(fixture, "packaged", "Qpro.GazeBridge.dll");
Directory.CreateDirectory(Path.GetDirectoryName(supplied)!);
File.Copy(args[0], supplied);

static string InstallFixture(string customLibs, string supplied, bool steamLink, string? moduleId = null, string? dllName = null, string? hash = null)
{
    var id = moduleId ?? (steamLink ? HubModuleInstallation.SteamLinkId : HubModuleInstallation.VirtualDesktopId);
    var filename = dllName ?? (steamLink ? "000-Qpro.SteamLink.dll" : "000-Qpro.VirtualDesktop.dll");
    var folder = Path.Combine(customLibs, id);
    Directory.CreateDirectory(folder);
    var dll = Path.Combine(folder, filename);
    File.Copy(supplied, dll, overwrite: true);
    File.WriteAllText(Path.Combine(folder, "module.json"), JsonSerializer.Serialize(new
    {
        ModuleId = id, DllFileName = filename, IsLocal = true, AuthorName = "Fwooffy", Version = "2.1.2",
        ModuleName = "QproFaceTracking - " + (steamLink ? "Steam Link" : "Virtual Desktop"),
        FileHash = hash ?? Convert.ToHexString(MD5.HashData(File.ReadAllBytes(dll))).ToLowerInvariant()
    }));
    return folder;
}

Check(!HubModuleInstallation.HasInstalled(customLibs) && !HubModuleInstallation.IsCurrent(customLibs, supplied, false), "An empty module list became ready.");
var legacy = Path.Combine(customLibs, "000-Qpro.VirtualDesktop.dll");
File.Copy(supplied, legacy);
Check(HubModuleInstallation.HasInstalled(customLibs) && !HubModuleInstallation.IsCurrent(customLibs, supplied, false),
    "A loose legacy DLL was not detected for migration or was mistaken for a current module card.");
File.Delete(legacy);
var vd = InstallFixture(customLibs, supplied, false);
Check(HubModuleInstallation.HasInstalled(customLibs) && HubModuleInstallation.IsCurrent(customLibs, supplied, false) &&
    !HubModuleInstallation.IsCurrent(customLibs, supplied, true), "Own source metadata and supplied DLL parity did not determine readiness.");
File.Copy(supplied, legacy);
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "Duplicate loose/nested Qpro copies became ready.");
File.Delete(legacy);
var foreign = InstallFixture(customLibs, supplied, false, "2a8c8080-2a76-46af-bf76-1da7c0127ef8", "LinkFT.dll");
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "A copied Qpro DLL under an official identity was ignored.");
File.Delete(Path.Combine(foreign, "LinkFT.dll"));
Check(HubModuleInstallation.IsCurrent(customLibs, supplied, false), "Foreign metadata alone blocked an otherwise valid Qpro module.");
var sl = InstallFixture(customLibs, supplied, true);
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false) && !HubModuleInstallation.IsCurrent(customLibs, supplied, true),
    "Two own source modules became ready simultaneously.");
File.Delete(Path.Combine(sl, "000-Qpro.SteamLink.dll")); File.Delete(Path.Combine(sl, "module.json")); Directory.Delete(sl);
InstallFixture(customLibs, supplied, false, hash: new string('0', 32));
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "A mismatched module card FileHash became ready.");
InstallFixture(customLibs, supplied, false);
var metadata = Path.Combine(vd, "module.json");
var validMetadata = File.ReadAllText(metadata);
foreach (var invalid in new[] {
    validMetadata.Replace(HubModuleInstallation.VirtualDesktopId, HubModuleInstallation.SteamLinkId),
    validMetadata.Replace("000-Qpro.VirtualDesktop.dll", "LinkFT.dll"),
    validMetadata.Replace("\"IsLocal\":true", "\"IsLocal\":\"true\""),
    validMetadata.Replace("\"Version\":\"2.1.2\"", "\"Version\":\"fixture\""),
    validMetadata.Replace("\"AuthorName\":\"Fwooffy\"", "\"AuthorName\":\"Other\""),
    validMetadata.Replace("\"IsLocal\":true", "\"IsLocal\":true,\"IsLocal\":false"),
    "{broken", new string('x', 65537)
})
{
    File.WriteAllText(metadata, invalid);
    Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "Malformed or borrowed module metadata became ready.");
}
File.WriteAllText(metadata, validMetadata);
var wrongSupplied = Path.Combine(fixture, "different-package.dll");
File.Copy(supplied, wrongSupplied); using (var output = File.Open(wrongSupplied, FileMode.Append)) output.WriteByte(0);
Check(!HubModuleInstallation.IsCurrent(customLibs, wrongSupplied, false), "A different packaged binary became ready.");
File.WriteAllText(Path.Combine(vd, "unknown.dll"), "unrelated fixture DLL");
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "An unexpected DLL in the own module directory became ready.");
Console.WriteLine("PASS: current own GUID card, source identity, MD5 metadata, SHA-256 package parity, duplicate/legacy detection and malformed metadata. Fixture: " + fixture);
