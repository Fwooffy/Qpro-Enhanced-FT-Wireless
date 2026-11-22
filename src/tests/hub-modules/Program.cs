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
if (args.Length is not (1 or 4) || !File.Exists(args[0]) || AssemblyName.GetAssemblyName(args[0]).Name != "Qpro.GazeBridge")
    throw new Exception("Pass an existing packaged Qpro DLL, optionally followed by the three named foreign-source fixture DLLs. No DLL is loaded or executed.");
var fixture = Path.Combine(Path.GetTempPath(), "qpro-module-detection-" + Guid.NewGuid().ToString("N"));
var customLibs = Path.Combine(fixture, "CustomLibs");
Directory.CreateDirectory(customLibs);
var research = Path.Combine(fixture, "research");
Directory.CreateDirectory(research);
var sourceRecovery = Path.Combine(fixture, "module-recovery");
Check(!HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "An empty research folder enabled uninstall.");
Directory.CreateDirectory(Path.Combine(research, "vrcft-qpro-module-uninstall-backup-fixture"));
Directory.CreateDirectory(Path.Combine(research, "vrcft-official-virtual-desktop-backup-unrelated"));
Check(!HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "Rollback copies or unrelated names enabled uninstall.");
foreach (var name in new[] { "vrcft-legacy-registry-module-backup", "vrcft-official-virtual-desktop-backup",
    "vrcft-official-virtual-desktop-backup-20260101-120000", "vrcft-official-virtual-desktop-backup-0123456789abcdef0123456789abcdef" })
{
    var path = Path.Combine(research, name);
    Directory.CreateDirectory(path);
    Check(HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "A recognized recovery folder did not enable uninstall: " + name);
    Directory.Delete(path);
}
Directory.CreateDirectory(sourceRecovery);
var sourceBackup = Path.Combine(sourceRecovery, "vrcft-qpro-module-switch-backup-0123456789abcdef0123456789abcdef");
Directory.CreateDirectory(sourceBackup);
var sourceReceipt = Path.Combine(sourceBackup, "source-modules.json");
foreach (var state in new[] { "prepared", "committed" })
{
    File.WriteAllText(sourceReceipt, JsonSerializer.Serialize(new { format = "qpro-source-module-backup-v1", state }));
    Check(HubModuleInstallation.HasSavedBackups(research, sourceRecovery) && !HubModuleInstallation.IsCurrent(customLibs, args[0], false),
        "A durable " + state + " source backup did not expose recovery independently of tracking readiness.");
}
foreach (var state in new[] { "rolled-back", "restored" })
{
    File.WriteAllText(sourceReceipt, JsonSerializer.Serialize(new { format = "qpro-source-module-backup-v1", state }));
    Check(!HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "A consumed source transaction enabled recovery: " + state);
}
File.WriteAllText(sourceReceipt, "{broken");
Check(HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "A corrupt receipt hid the recovery action needed to review it.");
File.WriteAllText(sourceReceipt, "{\"format\":\"qpro-source-module-backup-v1\",\"state\":\"committed\",\"state\":\"restored\"}");
Check(HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "Contradictory receipt states hid pending recovery.");
File.WriteAllText(sourceReceipt, new string('x', 70000));
Check(HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "A receipt below the helper's one-megabyte limit hid recovery.");
File.WriteAllText(sourceReceipt, new string('x', 1048577));
Check(!HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "An oversized recovery receipt enabled discovery.");
File.WriteAllText(sourceReceipt, "{broken");
for (var index = 0; index < 512; index++) File.WriteAllText(Path.Combine(sourceRecovery, index + ".fixture"), "");
Check(!HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "An oversized recovery inventory was traversed.");
for (var index = 0; index < 512; index++) File.Delete(Path.Combine(sourceRecovery, index + ".fixture"));
File.Delete(sourceReceipt); Directory.Delete(sourceBackup);
var supplied = Path.Combine(fixture, "packaged", "Qpro.GazeBridge.dll");
Directory.CreateDirectory(Path.GetDirectoryName(supplied)!);
File.Copy(args[0], supplied);

static string InstallFixture(string customLibs, string supplied, bool steamLink, string? moduleId = null, string? dllName = null, string? hash = null, string? displayName = null)
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
        ModuleName = displayName ?? "Qpro Companion \u2014 " + (steamLink ? "Steam Link" : "Virtual Desktop"),
        FileHash = hash ?? Convert.ToHexString(MD5.HashData(File.ReadAllBytes(dll))).ToLowerInvariant()
    }));
    return folder;
}

Check(!HubModuleInstallation.HasInstalled(customLibs) && !HubModuleInstallation.IsCurrent(customLibs, supplied, false), "An empty module list became ready.");
var missing = HubModuleInstallation.Inspect(customLibs, supplied, false);
Check(missing.State == HubModuleState.Missing && missing.ExpectedDirectory == Path.Combine(customLibs, HubModuleInstallation.VirtualDesktopId) &&
    missing.Detail.Contains("First-time setup") && missing.Detail.Contains("reopen VRCFaceTracking"), "Missing module guidance omitted the current installer folder or next step.");
foreach (var other in new[]
{
    (Id: "91a90618-b020-4064-8832-809b2ca2b3bc", Name: "Virtual Desktop", Dll: "VirtualDesktop.FaceTracking.dll"),
    (Id: "2a8c8080-2a76-46af-bf76-1da7c0127ef8", Name: "Steam Link", Dll: "VRCFT-SteamLink.dll"),
})
{
    var otherFolder = Path.Combine(customLibs, other.Id);
    Directory.CreateDirectory(otherFolder);
    var otherDll = Path.Combine(otherFolder, other.Dll);
    var otherCard = Path.Combine(otherFolder, "module.json");
    File.WriteAllText(otherDll, "A foreign source fixture; never loaded.");
    File.WriteAllText(otherCard, JsonSerializer.Serialize(new
    {
        ModuleId = other.Id, ModuleName = other.Name, DllFileName = other.Dll, IsLocal = false, Version = "1.5",
    }));
    var cardBefore = File.ReadAllBytes(otherCard);
    var dllBefore = File.ReadAllBytes(otherDll);
    foreach (var steamLink in new[] { false, true })
    {
        var conflict = HubModuleInstallation.Inspect(customLibs, supplied, steamLink);
        Check(conflict.State == HubModuleState.Conflict && conflict.Status == "Face source conflict" &&
            conflict.Detail.Contains(otherFolder) && conflict.Detail.Contains("Remove the " + other.Name + " module through VRCFaceTracking") &&
            conflict.Detail.Contains("close VRCFaceTracking, then retry"),
            "A source-only installation sent the user to install Qpro instead of resolving " + other.Name + ".");
        Check(!HubModuleInstallation.HasInstalled(customLibs) &&
            conflict.ExpectedDirectory == Path.Combine(customLibs, steamLink ? HubModuleInstallation.SteamLinkId : HubModuleInstallation.VirtualDesktopId),
            "A competing source module was claimed as Qpro-owned or replaced the expected installer destination.");
    }
    var invalidSelected = Path.Combine(customLibs, HubModuleInstallation.VirtualDesktopId);
    Directory.CreateDirectory(invalidSelected);
    Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Invalid,
        "A conflicting foreign source hid an invalid selected Qpro module folder.");
    Directory.Delete(invalidSelected);
    var existingQpro = InstallFixture(customLibs, supplied, false);
    Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
        "An already-installed Qpro source ignored the competing " + other.Name + " module.");
    File.Delete(Path.Combine(existingQpro, "000-Qpro.VirtualDesktop.dll"));
    File.Delete(Path.Combine(existingQpro, "module.json"));
    Directory.Delete(existingQpro);
    Check(File.ReadAllBytes(otherCard).AsSpan().SequenceEqual(cardBefore) &&
        File.ReadAllBytes(otherDll).AsSpan().SequenceEqual(dllBefore),
        "Read-only prechecks changed a foreign module card or DLL.");
    File.Delete(otherDll); File.Delete(otherCard); Directory.Delete(otherFolder);
}
var unrelatedOnly = Path.Combine(customLibs, "98973b67-7d93-4caf-b072-3c13bf96e694");
Directory.CreateDirectory(unrelatedOnly);
var unrelatedCard = Path.Combine(unrelatedOnly, "module.json");
var unrelatedDll = Path.Combine(unrelatedOnly, "OtherFeature.dll");
File.WriteAllText(unrelatedCard, "{\"ModuleName\":\"Eye movement\",\"ModuleDescription\":\"Works with Virtual Desktop\"}");
File.WriteAllText(unrelatedDll, "An unrelated feature fixture; never loaded.");
var unrelatedCardBefore = File.ReadAllText(unrelatedCard);
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Missing &&
    !HubModuleInstallation.HasInstalled(customLibs), "An unrelated module alone was mistaken for a competing face source.");
Check(File.ReadAllText(unrelatedCard) == unrelatedCardBefore && File.Exists(unrelatedDll),
    "Inspecting an unrelated module changed its files.");
File.Delete(unrelatedDll); File.Delete(unrelatedCard); Directory.Delete(unrelatedOnly);
if (args.Length == 4)
{
    var profiles = new[]
    {
        (Id: "91a90618-b020-4064-8832-809b2ca2b3bc", Name: "Virtual Desktop", Author: "Virtual Desktop, Inc.", Assembly: "VirtualDesktop.FaceTracking", Source: args[1]),
        (Id: "2a8c8080-2a76-46af-bf76-1da7c0127ef8", Name: "SteamLink VRCFT Module", Author: "Ykeara", Assembly: "SteamLinkVRCFTModule", Source: args[2]),
        (Id: "b146eda9-be48-4016-ab63-680a694064bd", Name: "SteamLink VRCFT", Author: "danwillm", Assembly: "SLExtTrackingModule", Source: args[3]),
    };
    foreach (var profile in profiles)
    {
        Check(File.Exists(profile.Source) && AssemblyName.GetAssemblyName(profile.Source).Name == profile.Assembly,
            "The source-module test fixture has the wrong assembly identity: " + profile.Assembly);
        var otherFolder = Path.Combine(customLibs, profile.Id);
        Directory.CreateDirectory(otherFolder);
        var dllName = profile.Assembly + ".dll";
        var dll = Path.Combine(otherFolder, dllName);
        var card = Path.Combine(otherFolder, "module.json");
        var configuration = Path.Combine(otherFolder, "settings.json");
        var dependency = Path.Combine(otherFolder, "native-support.dll");
        File.Copy(profile.Source, dll);
        var validCard = JsonSerializer.Serialize(new
        {
            ModuleId = profile.Id, ModuleName = profile.Name, AuthorName = profile.Author,
            DllFileName = dllName, Version = "1.5", IsLocal = false,
        });
        File.WriteAllText(card, validCard);
        File.WriteAllText(configuration, "{\"retain\":true}");
        File.WriteAllText(dependency, "A native dependency fixture; never loaded.");
        var before = Directory.EnumerateFiles(otherFolder).ToDictionary(file => Path.GetFileName(file)!,
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        foreach (var steamLink in new[] { false, true })
        {
            var ready = HubModuleInstallation.Inspect(customLibs, supplied, steamLink);
            Check(ready.State == HubModuleState.SourceReplacement && ready.Status == "Ready to switch source" &&
                ready.Detail.Contains(otherFolder) && ready.Detail.Contains("verified recovery copy") && !ready.IsCurrent,
                "A recognized provider was not offered a backed-up source switch: " + profile.Name);
            Check(!HubModuleInstallation.HasInstalled(customLibs) &&
                !HubModuleInstallation.TryGetInstalledSource(customLibs, steamLink, out _),
                "A replaceable source provider was claimed as an installed Qpro module.");
        }
        File.WriteAllText(card, validCard.Replace("\"ModuleId\"", "\"moduleid\"", StringComparison.Ordinal)
            .Replace(profile.Author, profile.Author.ToUpperInvariant(), StringComparison.Ordinal));
        Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.SourceReplacement,
            "A supported provider's case-insensitive card spelling was rejected.");
        File.WriteAllText(card, validCard);
        var currentQpro = InstallFixture(customLibs, supplied, false);
        Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.SourceReplacement,
            "An existing Qpro source prevented a recognized competing provider from being switched.");
        File.Delete(Path.Combine(currentQpro, "000-Qpro.VirtualDesktop.dll"));
        File.Delete(Path.Combine(currentQpro, "module.json")); Directory.Delete(currentQpro);
        foreach (var invalid in new[]
        {
            validCard.Replace(profile.Author, "Unrecognized publisher", StringComparison.Ordinal),
            validCard.Replace(profile.Name, "Unrecognized source module", StringComparison.Ordinal),
            validCard.Replace(dllName, "../" + dllName, StringComparison.Ordinal),
            validCard[..^1] + ",\"authorname\":\"" + profile.Author + "\"}",
            "{broken", new string('x', 65537),
        })
        {
            File.WriteAllText(card, invalid);
            Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
                "Malformed or borrowed provider metadata enabled automatic replacement: " + profile.Name);
        }
        File.WriteAllText(card, validCard);
        File.Copy(Assembly.GetExecutingAssembly().Location, dll, overwrite: true);
        Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
            "Matching names without the expected assembly identity enabled source replacement.");
        File.Copy(profile.Source, dll, overwrite: true);
        var nested = Path.Combine(otherFolder, "nested"); Directory.CreateDirectory(nested);
        var hiddenQpro = Path.Combine(nested, "copied-qpro.dll"); File.Copy(supplied, hiddenQpro);
        File.SetAttributes(hiddenQpro, FileAttributes.Hidden);
        Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
            "A nested hidden Qpro DLL allowed the foreign folder to be claimed for replacement.");
        File.SetAttributes(hiddenQpro, FileAttributes.Normal); File.Delete(hiddenQpro); Directory.Delete(nested);
        var unknownSource = Path.Combine(customLibs, "LinkFT.dll"); File.WriteAllText(unknownSource, "An unknown source fixture.");
        Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
            "A recognized provider hid an unknown competing source.");
        Check(File.ReadAllText(unknownSource) == "An unknown source fixture.", "Unknown mixed source files changed during inspection.");
        File.Delete(unknownSource);
        if (profile.Assembly == "VirtualDesktop.FaceTracking")
        {
            var inventory = Path.Combine(otherFolder, "inventory"); Directory.CreateDirectory(inventory);
            for (var index = 0; index < 4092; index++) File.WriteAllText(Path.Combine(inventory, index + ".fixture"), "");
            Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
                "An oversized recognized provider tree enabled automatic replacement.");
            foreach (var file in Directory.EnumerateFiles(inventory)) File.Delete(file);
            Directory.Delete(inventory);
        }
        Check(before.All(pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(otherFolder, pair.Key!)))) == pair.Value),
            "Source-switch eligibility inspection changed the provider, its card, dependencies or settings.");
        foreach (var file in Directory.EnumerateFiles(otherFolder)) File.Delete(file);
        Directory.Delete(otherFolder);
    }
}
var legacy = Path.Combine(customLibs, "000-Qpro.VirtualDesktop.dll");
File.Copy(supplied, legacy);
Check(HubModuleInstallation.HasInstalled(customLibs) && !HubModuleInstallation.IsCurrent(customLibs, supplied, false),
    "A loose legacy DLL was not detected for migration or was mistaken for a current module card.");
Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Legacy, "A loose module did not explain migration to the new directory.");
File.Delete(legacy);
foreach (var steamLink in new[] { false, true })
{
    var source = steamLink ? "Steam Link" : "Virtual Desktop";
    foreach (var prefix in new[] { "Qpro Companion \u2014 ", "Qpro Companion \u2013 ", "Qpro Companion - ",
        "QproFaceTracking \u2013 ", "QproFaceTracking - " })
    {
        var name = prefix + source;
        var named = InstallFixture(customLibs, supplied, steamLink, displayName: name);
        Check(HubModuleInstallation.IsCurrent(customLibs, supplied, steamLink), "A supported exact module display name was rejected: " + name);
        Directory.Delete(named, recursive: true);
    }
    foreach (var name in new[] { "Qpro Companion \u2014 " + (steamLink ? "Virtual Desktop" : "Steam Link"),
        "Qpro Companion \u2014 " + source + " extra", "Qpro companion \u2014 " + source,
        "Other \u2014 " + source, "QproFaceTracking \u2014 " + source })
    {
        var named = InstallFixture(customLibs, supplied, steamLink, displayName: name);
        Check(HubModuleInstallation.Inspect(customLibs, supplied, steamLink).State == HubModuleState.Invalid,
            "An unsupported or wrong-source display name became ready: " + name);
        Directory.Delete(named, recursive: true);
    }
}
var vd = InstallFixture(customLibs, supplied, false);
Check(HubModuleInstallation.HasInstalled(customLibs) && HubModuleInstallation.IsCurrent(customLibs, supplied, false) &&
    !HubModuleInstallation.IsCurrent(customLibs, supplied, true), "Own source metadata and supplied DLL parity did not determine readiness.");
var current = HubModuleInstallation.Inspect(customLibs, supplied, false);
Check(current.State == HubModuleState.Current && current.Detail.Contains(vd) && current.Status == "Installed · files verified" &&
    current.Detail.Contains("not live expressions"), "Disk readiness was mistaken for a proven live connection.");
Check(current.Detail.Contains("Activity > Tracking health > Show tracking details > Module output") &&
    current.Detail.Contains("VRCFaceTracking's preview"), "Installed files had no visible path to check actual expressions.");
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
    foreach (var sourceId in new[] { "91a90618-b020-4064-8832-809b2ca2b3bc", "2a8c8080-2a76-46af-bf76-1da7c0127ef8", "b146eda9-be48-4016-ab63-680a694064bd" })
    {
        var linkedOfficialSource = Path.Combine(customLibs, sourceId);
        MakeJunction(linkedOfficialSource, Path.GetDirectoryName(supplied)!);
        Check(HubModuleInstallation.Inspect(customLibs, supplied, false).State == HubModuleState.Conflict,
            "A known linked source module was ignored or became replaceable: " + sourceId);
        Directory.Delete(linkedOfficialSource);
    }
    var recoveryTarget = Path.Combine(fixture, "source recovery target"); Directory.CreateDirectory(recoveryTarget);
    var targetReceipt = Path.Combine(recoveryTarget, "source-modules.json");
    File.WriteAllText(targetReceipt, "{\"format\":\"qpro-source-module-backup-v1\",\"state\":\"committed\"}");
    MakeJunction(sourceBackup, recoveryTarget);
    Check(!HubModuleInstallation.HasSavedBackups(research, sourceRecovery), "A linked source recovery transaction enabled recovery.");
    Directory.Delete(sourceBackup);
    var recoveryLink = Path.Combine(fixture, "linked source recovery"); MakeJunction(recoveryLink, sourceRecovery);
    Check(!HubModuleInstallation.HasSavedBackups(research, recoveryLink), "A linked durable source recovery root enabled recovery.");
    Directory.Delete(recoveryLink); File.Delete(targetReceipt); Directory.Delete(recoveryTarget);
    var linkedResearch = Path.Combine(fixture, "linked research");
    MakeJunction(linkedResearch, research);
    var knownBackup = Path.Combine(research, "vrcft-legacy-registry-module-backup");
    Directory.CreateDirectory(knownBackup);
    Check(HubModuleInstallation.HasSavedBackups(linkedResearch, sourceRecovery),
        "Read-only backup discovery rejected a regular backup below a linked package ancestor.");
    Directory.Delete(knownBackup);
    var backupTarget = Path.Combine(fixture, "backup target");
    Directory.CreateDirectory(backupTarget);
    MakeJunction(knownBackup, backupTarget);
    Check(!HubModuleInstallation.HasSavedBackups(linkedResearch, sourceRecovery), "A linked backup directory enabled uninstall.");
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
