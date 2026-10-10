using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using QproFaceTracking.Hub;

var assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
// Read-only diagnostics use the same production inspection as the Hub. The
// installed DLL is inspected as assembly metadata and is never loaded.
if (args.Length == 4 && args[0] == "--inspect")
{
    Console.WriteLine(JsonSerializer.Serialize(HubModuleInstallation.Inspect(args[1], args[2], args[3] == "SteamLink")));
    return;
}
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
var missing = HubModuleInstallation.Inspect(customLibs, supplied, false);
Check(missing.State == HubModuleState.Missing && missing.ExpectedDirectory == Path.Combine(customLibs, HubModuleInstallation.VirtualDesktopId) &&
    missing.Detail.Contains("First-time setup") && missing.Detail.Contains("reopen VRCFaceTracking"), "Missing module guidance omitted the current installer folder or next step.");
var legacy = Path.Combine(customLibs, "000-Qpro.VirtualDesktop.dll");
File.Copy(supplied, legacy);
Check(HubModuleInstallation.HasInstalled(customLibs) && !HubModuleInstallation.IsCurrent(customLibs, supplied, false),
    "A loose legacy DLL was not detected for migration or was mistaken for a current module card.");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Legacy, "A loose module did not explain migration to the new directory.");
File.Delete(legacy);
var vd = InstallFixture(customLibs, supplied, false);
Check(HubModuleInstallation.HasInstalled(customLibs) && HubModuleInstallation.IsCurrent(customLibs, supplied, false) &&
    !HubModuleInstallation.IsCurrent(customLibs, supplied, true), "Own source metadata and supplied DLL parity did not determine readiness.");
var current = HubModuleInstallation.Inspect(customLibs, supplied, false);
Check(current.State == HubModuleState.Current && current.Detail.Contains(vd) && current.Status.Contains("input unchecked") &&
    current.Detail.Contains("live expressions"), "Disk readiness was mistaken for a proven live connection.");
Check(HubModuleInstallation.Inspect(Path.Combine(customLibs, "..", "CustomLibs"), supplied, false).IsCurrent,
    "Path normalization caused the installed Qpro source to conflict with itself.");
if (OperatingSystem.IsWindows())
{
    static void MakeJunction(string link, string target)
    {
        var command = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) command.ArgumentList.Add(argument);
        using var process = Process.Start(command) ?? throw new Exception("Could not start the junction fixture.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Could not create the junction fixture: " + output);
    }
    var packageLink = Path.Combine(fixture, "linked package");
    MakeJunction(packageLink, Path.GetDirectoryName(supplied)!);
    var linkedSource = Path.Combine(packageLink, Path.GetFileName(supplied));
    Check(HubModuleInstallation.Inspect(customLibs, linkedSource, false).IsCurrent,
        "A local directory junction made a readable packaged DLL fail the module check.");
    var nestedLink = Path.Combine(fixture, "nested package link");
    MakeJunction(nestedLink, packageLink);
    Check(HubModuleInstallation.Inspect(customLibs, Path.Combine(nestedLink, Path.GetFileName(supplied)), false).IsCurrent,
        "A chain of local directory junctions made the package unreadable.");
    var linkedCustomLibs = Path.Combine(fixture, "linked CustomLibs");
    MakeJunction(linkedCustomLibs, customLibs);
    Check(!HubModuleInstallation.Inspect(linkedCustomLibs, linkedSource, false).IsCurrent &&
        !HubModuleInstallation.HasInstalled(linkedCustomLibs), "Allowing linked package sources relaxed the CustomLibs destination check.");
    var linkedModuleRoot = Path.Combine(fixture, "linked-module-slot");
    Directory.CreateDirectory(linkedModuleRoot);
    MakeJunction(Path.Combine(linkedModuleRoot, HubModuleInstallation.VirtualDesktopId), vd);
    Check(HubModuleInstallation.Inspect(linkedModuleRoot, supplied, false).State == HubModuleState.Invalid,
        "An installed module directory junction became ready.");
    var nestedModuleLink = Path.Combine(vd, "unexpected linked folder");
    MakeJunction(nestedModuleLink, Path.GetDirectoryName(supplied)!);
    var linkedEntryResult = HubModuleInstallation.Inspect(customLibs, supplied, false);
    Check(linkedEntryResult.State == HubModuleState.Invalid && linkedEntryResult.Detail.Contains("linked entry"),
        "The Hub accepted a nested module link which PowerShell's tracking preflight rejects.");
    Directory.Delete(nestedModuleLink);
    Check(HubModuleInstallation.Inspect(customLibs, supplied, false).IsCurrent,
        "Removing the unrelated module link did not restore valid module readiness.");
    var linkedOfficialSource = Path.Combine(customLibs, "91a90618-b020-4064-8832-809b2ca2b3bc");
    MakeJunction(linkedOfficialSource, Path.GetDirectoryName(supplied)!);
    Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
        "A known linked official source module was ignored.");
    Directory.Delete(linkedOfficialSource);
    var linkedResearch = Path.Combine(fixture, "linked research");
    MakeJunction(linkedResearch, research);
    var knownBackup = Path.Combine(research, "vrcft-legacy-registry-module-backup");
    Directory.CreateDirectory(knownBackup);
    Check(HubModuleInstallation.HasSavedBackups(linkedResearch),
        "Read-only backup discovery rejected a regular backup below a linked package ancestor.");
    Directory.Delete(knownBackup);
    var backupTarget = Path.Combine(fixture, "backup target");
    Directory.CreateDirectory(backupTarget);
    MakeJunction(knownBackup, backupTarget);
    Check(!HubModuleInstallation.HasSavedBackups(linkedResearch), "A linked backup directory enabled uninstall.");
    Directory.Delete(knownBackup);
    Directory.Delete(linkedResearch);
    for (uint variant = 0; variant < 16; variant++)
    {
        var cloudTag = 0x9000001au | (variant << 12);
        Check(HubModuleInstallation.SupportedSourceReparseTag(cloudTag, directory: true) &&
            HubModuleInstallation.SupportedSourceReparseTag(cloudTag, directory: false), "A Windows Cloud Files reparse tag was rejected.");
    }
    Check(HubModuleInstallation.SupportedSourceReparseTag(0xa0000003u, directory: true) &&
        HubModuleInstallation.SupportedSourceReparseTag(0xa000000cu, directory: true), "A local directory link tag was rejected.");
    Check(!HubModuleInstallation.SupportedSourceReparseTag(0xa0000003u, directory: false) &&
        !HubModuleInstallation.SupportedSourceReparseTag(0xa000000cu, directory: false), "A linked source DLL was accepted.");
    Check(!HubModuleInstallation.SupportedSourceReparseTag(0x9001001au, directory: true) &&
        !HubModuleInstallation.SupportedSourceReparseTag(0x80000013u, directory: false), "An unknown reparse tag became an allowed source.");
    Check(HubModuleInstallation.Inspect(customLibs, @"\\invalid-host\invalid-share\Qpro.GazeBridge.dll", false).State == HubModuleState.Unreadable,
        "A network package source became ready.");
    Directory.Delete(Path.Combine(linkedModuleRoot, HubModuleInstallation.VirtualDesktopId));
    Directory.Delete(linkedCustomLibs);
    Directory.Delete(nestedLink);
    Directory.Delete(packageLink);
}
var inventoryDirectory = Path.Combine(vd, "inventory-fixture");
Directory.CreateDirectory(inventoryDirectory);
// The DLL, module card and this directory count as three installed entries.
// Probe both sides of the production PowerShell guard's boundary.
for (var entry = 0; entry < 4093; entry++) File.WriteAllText(Path.Combine(inventoryDirectory, entry + ".fixture"), "");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).IsCurrent,
    "A valid installed tree at PowerShell's inventory limit was rejected.");
var overLimit = Path.Combine(inventoryDirectory, "over-limit.fixture");
File.WriteAllText(overLimit, "");
var oversized = HubModuleInstallation.Inspect(customLibs, supplied, false);
Check(oversized.State == HubModuleState.Invalid && oversized.Detail.Contains("inventory limit"),
    "The Hub accepted an oversized installed tree which PowerShell's tracking preflight rejects.");
foreach (var entry in Directory.EnumerateFiles(inventoryDirectory)) File.Delete(entry);
Directory.Delete(inventoryDirectory);
var wrongSource = HubModuleInstallation.Inspect(customLibs, supplied, true);
Check(wrongSource.State == HubModuleState.WrongSource && wrongSource.Detail.Contains("Virtual Desktop") && wrongSource.Detail.Contains("Steam Link"),
    "Selected source mismatch was reported as a missing installation.");
File.Copy(supplied, legacy);
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "Duplicate loose/nested Qpro copies became ready.");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict, "Duplicate modules had no conflict diagnosis.");
File.Delete(legacy);
var foreign = InstallFixture(customLibs, supplied, false, "2a8c8080-2a76-46af-bf76-1da7c0127ef8", "LinkFT.dll");
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "A copied Qpro DLL under an official identity was ignored.");
File.Delete(Path.Combine(foreign, "LinkFT.dll"));
Check(HubModuleInstallation.IsCurrent(customLibs, supplied, false), "Foreign metadata alone blocked an otherwise valid Qpro module.");
foreach (var name in new[] { "VirtualDesktop.dll", "LinkFT.dll", "VRCFT-Steam_Link.dll" })
{
    var otherDll = Path.Combine(customLibs, name);
    File.WriteAllText(otherDll, "unrelated native source fixture");
    var otherHash = SHA256.HashData(File.ReadAllBytes(otherDll));
    var conflict = HubModuleInstallation.Inspect(customLibs, supplied, false);
    Check(conflict.State == HubModuleState.Conflict && conflict.Detail.Contains(otherDll) &&
        conflict.Detail.Contains("through VRCFaceTracking") && conflict.Detail.Contains("left those files unchanged"),
        "A loose competing source module had no actionable conflict warning.");
    Check(SHA256.HashData(File.ReadAllBytes(otherDll)).AsSpan().SequenceEqual(otherHash),
        "Inspecting a competing source changed its DLL.");
    File.Delete(otherDll);
}
var competingFolder = Path.Combine(customLibs, "79ccecf5-1374-4808-9d22-4d69c5799fba");
Directory.CreateDirectory(competingFolder);
var competingDll = Path.Combine(competingFolder, "NativeFixture.dll");
var competingCard = Path.Combine(competingFolder, "module.json");
File.WriteAllText(competingDll, "unrelated native module fixture");
foreach (var card in new[] {
    "{\"ModuleName\":\"Virtual Desktop\"}",
    "{\"DllFileName\":\"VRCFT-SteamLink.dll\"}",
    "{\"ModuleId\":\"2a8c8080-2a76-46af-bf76-1da7c0127ef8\"}",
    "{\"ModulePageUrl\":\"https://github.com/danwillm/VRCFT-SteamLink\"}" })
{
    File.WriteAllText(competingCard, card);
    var conflict = HubModuleInstallation.Inspect(customLibs, supplied, false);
    Check(conflict.State == HubModuleState.Conflict && conflict.Detail.Contains(competingFolder),
        "A competing source identified through its module card was ignored.");
    Check(File.ReadAllText(competingCard) == card && File.ReadAllText(competingDll) == "unrelated native module fixture",
        "Competing module inspection changed unrelated files.");
}
File.WriteAllText(competingCard, "{\"ModuleName\":\"Eye movement\",\"ModuleDescription\":\"Works with Virtual Desktop\"}");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).IsCurrent,
    "An unrelated feature module was mistaken for a competing face source.");
File.WriteAllText(competingCard, "{broken");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).IsCurrent,
    "An unrelated unreadable card was mistaken for a competing face source.");
File.Delete(competingDll);
File.Delete(competingCard);
Directory.Delete(competingFolder);
var officialDll = Path.Combine(foreign, "NativeFixture.dll");
File.WriteAllText(officialDll, "unrelated official source fixture");
File.WriteAllText(Path.Combine(foreign, "module.json"), "{broken");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
    "A known official source GUID with a generic DLL name was ignored.");
File.Delete(officialDll);
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).IsCurrent,
    "An empty official module directory prevented Qpro readiness.");
var sl = InstallFixture(customLibs, supplied, true);
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false) && !HubModuleInstallation.IsCurrent(customLibs, supplied, true),
    "Two own source modules became ready simultaneously.");
Check(HubModuleInstallation.Inspect(customLibs, supplied, true).State == HubModuleState.Conflict, "Two source directories had no conflict diagnosis.");
File.Delete(Path.Combine(sl, "000-Qpro.SteamLink.dll")); File.Delete(Path.Combine(sl, "module.json")); Directory.Delete(sl);
InstallFixture(customLibs, supplied, false, hash: new string('0', 32));
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "A mismatched module card FileHash became ready.");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Invalid &&
    HubModuleInstallation.Inspect(customLibs, supplied, false).Detail.Contains("file hash"), "A card/DLL mismatch did not explain repair.");
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
    Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Invalid, "Invalid metadata had no card repair diagnosis.");
}
File.WriteAllText(metadata, validMetadata);
Check(HubModuleInstallation.Inspect(customLibs, Path.Combine(fixture, "missing-package.dll"), false).State == HubModuleState.Unreadable,
    "A missing packaged DLL was incorrectly described as a missing installed module.");
Check(HubModuleInstallation.Inspect(customLibs, Path.Combine(fixture, "missing-package.dll"), false).Detail.Contains("Always keep on this device"),
    "An unavailable package omitted OneDrive hydration guidance.");
var wrongSupplied = Path.Combine(fixture, "different-package.dll");
File.Copy(supplied, wrongSupplied); using (var output = File.Open(wrongSupplied, FileMode.Append)) output.WriteByte(0);
Check(!HubModuleInstallation.IsCurrent(customLibs, wrongSupplied, false), "A different packaged binary became ready.");
Check(HubModuleInstallation.Inspect(customLibs, wrongSupplied, false).State == HubModuleState.UpdateNeeded,
    "Different DLLs with the same visible release version did not explain the module update.");
File.WriteAllText(Path.Combine(vd, "unknown.dll"), "unrelated fixture DLL");
Check(!HubModuleInstallation.IsCurrent(customLibs, supplied, false), "An unexpected DLL in the own module directory became ready.");
File.Delete(Path.Combine(vd, "unknown.dll"));
File.Delete(Path.Combine(vd, "000-Qpro.VirtualDesktop.dll")); File.Delete(metadata); Directory.Delete(vd);
sl = InstallFixture(customLibs, supplied, true);
Check(HubModuleInstallation.Inspect(customLibs, supplied, true).IsCurrent &&
    HubModuleInstallation.TryGetInstalledSource(customLibs, false, out var installedSource) && installedSource,
    "The Steam Link installer's own GUID module folder was not recognized.");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.WrongSource,
    "A Steam Link directory with Virtual Desktop selected had no source-switch guidance.");
File.Delete(Path.Combine(sl, "module.json"));
Check(HubModuleInstallation.Inspect(customLibs, supplied, true).State == HubModuleState.Invalid,
    "A DLL without its new module card became ready.");
Console.WriteLine($"PASS: {assertions} module folder, source identity, hash, migration and user guidance checks. Fixture: " + fixture);
