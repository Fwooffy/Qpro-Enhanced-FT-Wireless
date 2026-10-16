using QproFaceTracking.Hub;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 4 && args[0] is "--crash-fixture" or "--crash-recovery-fixture")
{
    string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "hub-update-apply-fixtures")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(args[1]).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFullPath(args[2]).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe crash fixture path.");
    int stopAt = int.Parse(args[3]);
    if (args[0] == "--crash-fixture")
        HubUpdateApply.Apply(args[1], args[2], new Version(2, 1, 3), afterFileApplied: (count, _) => { if (count == stopAt) Environment.Exit(99); });
    else HubUpdateApply.RecoverPending(args[2], (count, _) => { if (count == stopAt) Environment.Exit(99); });
    throw new Exception("The expected crash boundary was not reached.");
}

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++;
}
void Reject(Action action, string message)
{
    try { action(); throw new Exception("Accepted " + message); }
    catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { passed++; }
}
string artifacts = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "hub-update-apply-fixtures"));
string fixture = Path.Combine(artifacts, "test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var version = new Version(2, 1, 3);
string[] essentials = ["build-and-run.ps1", "setup-runtime.ps1", "runtime-python.ps1", "receiver.py", "tongue_model_preview.py",
    "platform-tools/adb.exe", "vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll"];
void Write(string root, string relative, string content)
{
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}
string Hash(string path)
{
    using var source = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
}
void Seal(string package)
{
    string[] hashes = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFileName(path).Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => Hash(path) + "  " + Path.GetRelativePath(package, path).Replace('\\', '/')).ToArray();
    File.WriteAllLines(Path.Combine(package, "SHA256SUMS.txt"), hashes);
}
(string Stage, string Destination) Make(string name, bool equalModels = false)
{
    string parent = Path.Combine(fixture, name);
    string staged = Path.Combine(parent, "staging", "QproFaceTracking-2.1.3");
    string destination = Path.Combine(parent, "installed");
    foreach (var (root, identity) in new[] { (staged, "new"), (destination, "old") })
    {
        Write(root, "QproFaceTracking.exe", identity + " executable");
        Write(root, "QproRuntime/release-manifest.json", JsonSerializer.Serialize(new
        { name = "QproFaceTracking", version = identity == "new" ? "2.1.3" : "2.1.2", updateFormat = 1 }));
        foreach (string essential in essentials) Write(root, "QproRuntime/" + essential, identity + " " + essential);
        Write(root, "Docs/README.txt", identity + " documentation");
        Write(root, "QproRuntime/models/qpro-stereo-tongue-v8-gate.pt", (equalModels ? "same" : identity) + " gate");
        Write(root, "QproRuntime/models/qpro-stereo-tongue-v8-direction.pt", (equalModels ? "same" : identity) + " direction");
        foreach (string relative in new[] { "config/wireless-headset.json", "config/connection-mode.txt", "captures/mine.qpcap",
                     "training/mine.npy", "calibration/qpro-independent-visual-axis-v2.json", "research/seacliff_eye_model/bolt-independent-axes.ptl",
                     "research/seacliff_eye_model/bolt-independent-axes.manifest.json", ".venv/Scripts/python.exe", ".venv-rocm/qpro-rocm-ready.json",
                     "logs/current.log", "questpro-live-headset.txt", "questpro-relay-client-error.txt" })
            Write(root, "QproRuntime/" + relative, identity + " private " + relative);
    }
    Write(destination, "QproRuntime/models/qpro-stereo-tongue-v99-gate.pt", "personal gate");
    Write(destination, "QproRuntime/models/qpro-stereo-tongue-v99-direction.pt", "personal direction");
    Write(destination, "QproRuntime/models/qpro-stereo-tongue-v99.metadata.json", "personal metadata");
    Write(destination, "QproRuntime/unrelated-user.txt", "keep unknown file");
    Write(destination, "obsolete-script.py", "keep obsolete file");
    Write(staged, "QproRuntime/new-feature/nested/helper.py", "new helper");
    Write(staged, "QproRuntime/calibration/new-bundled-profile.json", "new profile with a unique filename");
    Write(staged, "QproRuntime/models/qpro-stereo-tongue-v9-gate.pt", "new noncolliding gate");
    Write(staged, "QproRuntime/models/qpro-stereo-tongue-v9-direction.pt", "new noncolliding direction");
    Write(staged, "QproRuntime/models/qpro-stereo-tongue-v9.metadata.json", "new noncolliding metadata");
    Write(staged, "QproRuntime/models/qpro-stereo-tongue-v9-gate.torchscript.pt", "new compiled gate");
    Seal(staged);
    Seal(destination);
    return (Path.Combine(staged, "QproRuntime"), Path.Combine(destination, "QproRuntime"));
}
Dictionary<string, string> Snapshot(string runtime)
{
    string root = Directory.GetParent(runtime)!.FullName;
    return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), Hash, StringComparer.OrdinalIgnoreCase);
}
bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
    left.Count == right.Count && left.All(file => right.TryGetValue(file.Key, out string? value) && value == file.Value);

void Crash((string Stage, string Destination) item, int stopAt, bool recovery = false)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(HubUpdateApply).Assembly.Location);
    start.ArgumentList.Add(recovery ? "--crash-recovery-fixture" : "--crash-fixture");
    start.ArgumentList.Add(item.Stage); start.ArgumentList.Add(item.Destination); start.ArgumentList.Add(stopAt.ToString());
    using var child = Process.Start(start) ?? throw new Exception("Could not launch isolated crash fixture.");
    Check(child.WaitForExit(30_000) && child.ExitCode == 99, "Crash fixture did not exit at its requested boundary.");
}

void AddDeveloperBundle((string Stage, string Destination) item, bool personalCollision)
{
    string stage = Directory.GetParent(item.Stage)!.FullName;
    string destination = Directory.GetParent(item.Destination)!.FullName;
    foreach (string suffix in new[] { "-gate.pt", "-direction.pt", "-gate.torchscript.pt" })
        Write(stage, "QproRuntime/models/qpro-stereo-tongue-v15" + suffix, "developer " + suffix);
    Write(stage, "QproRuntime/models/qpro-stereo-tongue-v15.metadata.json", JsonSerializer.Serialize(new
    {
        modelKind = "developer-lower-face", bundledDeveloperModel = true, origin = "bundled developer model",
        version = 15, sourceVersion = 13, displayName = "Developer lower-face model", isExperimental = true,
        gateFile = "qpro-stereo-tongue-v15-gate.pt", directionFile = "qpro-stereo-tongue-v15-direction.pt",
        tongueParentSha256 = new string('a', 64), publicPromotionValidated = false,
        validationScope = new { independentWearerValidation = false, missedVisible = 1 }
    }));
    if (personalCollision)
    {
        Write(destination, "QproRuntime/models/qpro-stereo-tongue-v15-gate.pt", "personal v15 gate");
        Write(destination, "QproRuntime/models/qpro-stereo-tongue-v15-direction.pt", "personal v15 direction");
        Write(destination, "QproRuntime/models/qpro-stereo-tongue-v15.metadata.json", "{\"modelKind\":\"personal\",\"version\":15,\"displayName\":\"My refinement\"}");
        Write(destination, "QproRuntime/config/live-options.json", "{\"Schema\":1,\"Model\":\"qpro-stereo-tongue-v15-gate.pt\",\"Tongue\":true,\"Pupil\":true}");
    }
    Seal(stage);
    Seal(destination);
}

void ModelCollisionFixtures()
{
    var collision = Make("developer-personal-collision");
    AddDeveloperBundle(collision, personalCollision: true);
    var original = Snapshot(collision.Destination);
    string stagedMetadataHash = Hash(Path.Combine(collision.Stage, "models", "qpro-stereo-tongue-v15.metadata.json"));
    var result = HubUpdateApply.Apply(collision.Stage, collision.Destination, version);
    Check(Hash(Path.Combine(collision.Stage, "models", "qpro-stereo-tongue-v15.metadata.json")) == stagedMetadataHash, "Relocation changed the already-verified staging payload.");
    string localStem = Path.Combine(collision.Destination, "models", "qpro-stereo-tongue-v16");
    foreach (string suffix in new[] { "-gate.pt", "-direction.pt", "-gate.torchscript.pt" })
        Check(Hash(localStem + suffix) == Hash(Path.Combine(collision.Stage, "models", "qpro-stereo-tongue-v15" + suffix)), "Relocation modified a developer weight/compiled file.");
    foreach (var file in original.Where(file => file.Key.Contains("qpro-stereo-tongue-v15", StringComparison.Ordinal) || file.Key.EndsWith("live-options.json", StringComparison.Ordinal)))
        Check(Hash(Path.Combine(Directory.GetParent(collision.Destination)!.FullName, file.Key)) == file.Value, "Relocation changed the personal model or saved choice: " + file.Key);
    using (var metadata = JsonDocument.Parse(File.ReadAllText(localStem + ".metadata.json")))
    {
        JsonElement details = metadata.RootElement;
        Check(details.GetProperty("version").GetInt32() == 16, "The relocated sidecar must describe its local version.");
        Check(details.GetProperty("sourceVersion").GetInt32() == 13 && details.GetProperty("tongueParentSha256").GetString() == new string('a', 64), "Relocation changed parent lineage/hash.");
        Check(details.GetProperty("gateFile").GetString() == "qpro-stereo-tongue-v16-gate.pt" && details.GetProperty("directionFile").GetString() == "qpro-stereo-tongue-v16-direction.pt", "Relocated public pair filenames were not mapped.");
        Check(details.GetProperty("bundledDeveloperModel").GetBoolean() && !details.GetProperty("publicPromotionValidated").GetBoolean() && details.GetProperty("validationScope").GetProperty("missedVisible").GetInt32() == 1, "Relocation changed bundle ownership or validation facts.");
    }
    Check(result.Warnings.Any(text => text.Contains("installed as v16", StringComparison.Ordinal)), "The updater must explain the local developer version.");
    // A later app release with the same weights must keep the user's renamed
    // developer model, rather than allocating another copy of the bundle.
    JsonObject renamed = JsonNode.Parse(File.ReadAllText(localStem + ".metadata.json"))!.AsObject();
    renamed["displayName"] = "My renamed developer model";
    File.WriteAllText(localStem + ".metadata.json", renamed.ToJsonString());
    string renamedHash = Hash(localStem + ".metadata.json");
    Write(Directory.GetParent(collision.Stage)!.FullName, "QproRuntime/release-manifest.json", "{\"name\":\"QproFaceTracking\",\"version\":\"2.1.4\",\"updateFormat\":1}");
    Seal(Directory.GetParent(collision.Stage)!.FullName);
    var repeated = HubUpdateApply.Apply(collision.Stage, collision.Destination, new Version(2, 1, 4));
    Check(!File.Exists(Path.Combine(collision.Destination, "models", "qpro-stereo-tongue-v17-gate.pt")), "Repeated identical bundled weights created a duplicate model.");
    Check(Hash(localStem + ".metadata.json") == renamedHash && repeated.Warnings.Any(text => text.Contains("already available as v16", StringComparison.Ordinal)), "A later release replaced the existing developer name.");
    foreach (string suffix in new[] { "-gate.pt", "-direction.pt", ".metadata.json" })
        File.Delete(Path.Combine(collision.Destination, "models", "qpro-stereo-tongue-v15" + suffix));
    Write(Directory.GetParent(collision.Stage)!.FullName, "QproRuntime/release-manifest.json", "{\"name\":\"QproFaceTracking\",\"version\":\"2.1.5\",\"updateFormat\":1}");
    Seal(Directory.GetParent(collision.Stage)!.FullName);
    HubUpdateApply.Apply(collision.Stage, collision.Destination, new Version(2, 1, 5));
    Check(!File.Exists(Path.Combine(collision.Destination, "models", "qpro-stereo-tongue-v15-gate.pt")) && Hash(localStem + ".metadata.json") == renamedHash, "Removing the old personal collision caused the next update to duplicate the relocated developer bundle.");

    var free = Make("developer-no-collision");
    AddDeveloperBundle(free, personalCollision: false);
    var freeResult = HubUpdateApply.Apply(free.Stage, free.Destination, version);
    Check(File.Exists(Path.Combine(free.Destination, "models", "qpro-stereo-tongue-v15-gate.pt")) && File.Exists(Path.Combine(free.Destination, "models", "qpro-stereo-tongue-v15-direction.pt")), "A noncolliding bundle was not installed at v15.");
    Check(!freeResult.Warnings.Any(text => text.Contains("v15", StringComparison.Ordinal)), "A noncolliding developer bundle should not need relocation.");

    var reserved = Make("developer-reserved-stems");
    AddDeveloperBundle(reserved, personalCollision: true);
    Write(Directory.GetParent(reserved.Destination)!.FullName, "QproRuntime/models/qpro-stereo-tongue-v16.metadata.json", "personal partial metadata");
    Write(Directory.GetParent(reserved.Stage)!.FullName, "QproRuntime/models/qpro-stereo-tongue-v17-gate.pt", "incoming other gate");
    Write(Directory.GetParent(reserved.Stage)!.FullName, "QproRuntime/models/qpro-stereo-tongue-v17-direction.pt", "incoming other direction");
    Seal(Directory.GetParent(reserved.Stage)!.FullName);
    HubUpdateApply.Apply(reserved.Stage, reserved.Destination, version);
    Check(File.Exists(Path.Combine(reserved.Destination, "models", "qpro-stereo-tongue-v18-direction.pt")), "Relocation reused a partial local or incoming reserved model number.");
    Check(File.ReadAllText(Path.Combine(reserved.Destination, "models", "qpro-stereo-tongue-v16.metadata.json")) == "personal partial metadata", "A partial local model was overwritten.");

    var partialBundle = Make("developer-partial-bundle");
    AddDeveloperBundle(partialBundle, personalCollision: true);
    File.Delete(Path.Combine(partialBundle.Stage, "models", "qpro-stereo-tongue-v15-direction.pt"));
    Seal(Directory.GetParent(partialBundle.Stage)!.FullName);
    var partialOriginal = Snapshot(partialBundle.Destination);
    var partialResult = HubUpdateApply.Apply(partialBundle.Stage, partialBundle.Destination, version);
    Check(!File.Exists(Path.Combine(partialBundle.Destination, "models", "qpro-stereo-tongue-v16-gate.pt")) && partialResult.Warnings.Any(text => text.Contains("v15", StringComparison.Ordinal) && text.Contains("complete", StringComparison.Ordinal)), "An incomplete incoming bundle was relocated/installed.");
    Check(Hash(Path.Combine(partialBundle.Destination, "models", "qpro-stereo-tongue-v15-gate.pt")) == partialOriginal[Path.Combine("QproRuntime", "models", "qpro-stereo-tongue-v15-gate.pt")], "An incomplete bundle changed personal weights.");

    var rollback = Make("developer-relocation-rollback");
    AddDeveloperBundle(rollback, personalCollision: true);
    var rollbackOriginal = Snapshot(rollback.Destination);
    try
    {
        HubUpdateApply.Apply(rollback.Stage, rollback.Destination, version, afterFileApplied: (_, relative) =>
        { if (relative == "QproRuntime/models/qpro-stereo-tongue-v16.metadata.json") throw new IOException("Injected failure after relocated generated metadata"); });
        throw new Exception("Relocation transaction fault was ignored.");
    }
    catch (HubUpdateApplyException error)
    {
        Check(error.RollbackComplete && Same(rollbackOriginal, Snapshot(rollback.Destination)), "Relocation did not roll back generated metadata and the complete model group.");
    }
    Console.WriteLine($"PASS: {passed} developer model collision assertions; personal models and saved choices unchanged.");
}

(string Stage, string Destination) RetirementFixture(string name, string selection = "qpro-stereo-tongue-v13-gate.pt")
{
    var item = Make(name);
    AddDeveloperBundle(item, personalCollision: false);
    string stage = Directory.GetParent(item.Stage)!.FullName;
    string destination = Directory.GetParent(item.Destination)!.FullName;
    foreach (string suffix in new[] { "-gate.pt", "-direction.pt" })
        File.Delete(Path.Combine(item.Stage, "models", "qpro-stereo-tongue-v8" + suffix));
    JsonObject newMetadata = JsonNode.Parse(File.ReadAllText(Path.Combine(item.Stage, "models", "qpro-stereo-tongue-v15.metadata.json")))!.AsObject();
    newMetadata["modelKind"] = "developer-tongue";
    newMetadata["hasCameraCheeks"] = false;
    newMetadata["displayName"] = "Developer tongue model";
    Write(stage, "QproRuntime/models/qpro-stereo-tongue-v15.metadata.json", newMetadata.ToJsonString());
    foreach (int number in new[] { 13, 15 })
    {
        Write(destination, $"QproRuntime/models/qpro-stereo-tongue-v{number}-gate.pt", "old bundled gate " + number);
        Write(destination, $"QproRuntime/models/qpro-stereo-tongue-v{number}-direction.pt", "old bundled cheek direction " + number);
        Write(destination, $"QproRuntime/models/qpro-stereo-tongue-v{number}-gate.torchscript.pt", "old bundled compiled " + number);
        Write(destination, $"QproRuntime/models/qpro-stereo-tongue-v{number}.metadata.json", JsonSerializer.Serialize(new
        {
            version = number, displayName = "Renamed old bundle",
            modelKind = number == 13 ? "camera-cheeks-experimental" : "developer-lower-face",
            origin = number == 13 ? "developer tongue with an experimental camera cheek head" : "bundled developer model",
            bundledDeveloperModel = number == 15, hasCameraCheeks = true
        }));
    }
    JsonObject originalManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(item.Destination, "release-manifest.json")))!.AsObject();
    originalManifest["bundledTongueModel"] = "qpro-stereo-tongue-v15";
    originalManifest["fallbackTongueModel"] = "qpro-stereo-tongue-v8";
    Write(destination, "QproRuntime/release-manifest.json", originalManifest.ToJsonString());
    var retire = new JsonArray();
    foreach (int number in new[] { 8, 13, 15 })
    {
        string stem = "qpro-stereo-tongue-v" + number;
        var record = new JsonObject
        {
            ["stem"] = stem, ["gateSha256"] = Hash(Path.Combine(item.Destination, "models", stem + "-gate.pt")),
            ["directionSha256"] = Hash(Path.Combine(item.Destination, "models", stem + "-direction.pt"))
        };
        if (number == 8) record["requireOriginalPackage"] = true;
        else
        {
            record["modelKind"] = number == 13 ? "camera-cheeks-experimental" : "developer-lower-face";
            record["origin"] = number == 13 ? "developer tongue with an experimental camera cheek head" : "bundled developer model";
            record["hasCameraCheeks"] = true;
            if (number == 15) record["bundledDeveloperModel"] = true;
        }
        retire.Add(record);
    }
    JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(item.Stage, "release-manifest.json")))!.AsObject();
    manifest["bundledTongueModel"] = "qpro-stereo-tongue-v15";
    manifest["modelRetirement"] = new JsonObject
    { ["schema"] = 1, ["replacementStem"] = "qpro-stereo-tongue-v15", ["models"] = retire };
    Write(stage, "QproRuntime/release-manifest.json", manifest.ToJsonString());
    Write(destination, "QproRuntime/config/live-options.json", JsonSerializer.Serialize(new
    {
        Schema = 1, Model = selection, Tongue = true, Pupil = true, Smoothing = 64,
        Fps = "24", EyeProfile = "personal-eye-profile.json", ExtraFutureSetting = new { nested = "keep", values = new[] { 1, 2 } }
    }));
    Seal(stage); Seal(destination);
    return item;
}

void ModelRetirementFixtures()
{
    var normal = RetirementFixture("retire-bundles");
    JsonObject originalOptions = JsonNode.Parse(File.ReadAllText(Path.Combine(normal.Destination, "config", "live-options.json")))!.AsObject();
    string personalHash = Hash(Path.Combine(normal.Destination, "models", "qpro-stereo-tongue-v99-direction.pt"));
    var result = HubUpdateApply.Apply(normal.Stage, normal.Destination, version);
    foreach (int number in new[] { 8, 13, 15 })
        Check(!Directory.GetFiles(Path.Combine(normal.Destination, "models"), "qpro-stereo-tongue-v" + number + "*").Any(), "An exact old bundle was not retired completely: " + number);
    string replacement = Path.Combine(normal.Destination, "models", "qpro-stereo-tongue-v16");
    Check(File.Exists(replacement + "-gate.pt") && File.Exists(replacement + "-direction.pt") && File.Exists(replacement + ".metadata.json"), "Retirement did not keep the relocated complete replacement.");
    Check(Hash(replacement + "-direction.pt") == Hash(Path.Combine(normal.Stage, "models", "qpro-stereo-tongue-v15-direction.pt")), "Retirement changed incoming developer weights.");
    Check(Hash(Path.Combine(normal.Destination, "models", "qpro-stereo-tongue-v99-direction.pt")) == personalHash, "An unrelated personal model changed.");
    JsonObject migrated = JsonNode.Parse(File.ReadAllText(Path.Combine(normal.Destination, "config", "live-options.json")))!.AsObject();
    Check(migrated["Model"]!.GetValue<string>() == "qpro-stereo-tongue-v16-gate.pt", "Retired selection did not map to the actual allocated developer version.");
    migrated["Model"] = originalOptions["Model"]!.DeepClone();
    Check(JsonNode.DeepEquals(migrated, originalOptions), "Migration changed another saved setting or unknown future setting.");
    Check(result.Warnings.Count(text => text.StartsWith("Replaced the verified old bundled model", StringComparison.Ordinal)) == 3, "Retired bundle decisions were not reported.");

    var personal = RetirementFixture("retire-preserve-personal", "qpro-stereo-tongue-v15-gate.pt");
    string personalRoot = Directory.GetParent(personal.Destination)!.FullName;
    // Imported copies can have identical weights: origin/ownership is decisive.
    Write(personalRoot, "QproRuntime/models/qpro-stereo-tongue-v8.metadata.json", "{\"origin\":\"imported model\",\"displayName\":\"Personal v8\"}");
    JsonObject imported13 = JsonNode.Parse(File.ReadAllText(Path.Combine(personal.Destination, "models", "qpro-stereo-tongue-v13.metadata.json")))!.AsObject();
    imported13["origin"] = "imported model";
    Write(personalRoot, "QproRuntime/models/qpro-stereo-tongue-v13.metadata.json", imported13.ToJsonString());
    Write(personalRoot, "QproRuntime/models/qpro-stereo-tongue-v15-direction.pt", "different personal v15 weights");
    Seal(personalRoot);
    var personalBefore = Snapshot(personal.Destination);
    HubUpdateApply.Apply(personal.Stage, personal.Destination, version);
    foreach (var file in personalBefore.Where(file => file.Key.Contains("qpro-stereo-tongue-v8", StringComparison.Ordinal) ||
        file.Key.Contains("qpro-stereo-tongue-v13", StringComparison.Ordinal) || file.Key.Contains("qpro-stereo-tongue-v15", StringComparison.Ordinal) || file.Key.EndsWith("live-options.json", StringComparison.Ordinal)))
        Check(File.Exists(Path.Combine(personalRoot, file.Key)) && Hash(Path.Combine(personalRoot, file.Key)) == file.Value, "A personal/imported collision or selection was changed: " + file.Key);
    Check(File.Exists(Path.Combine(personal.Destination, "models", "qpro-stereo-tongue-v16-gate.pt")), "Personal collisions prevented installation of the new developer model.");

    var extra = RetirementFixture("retire-extra-local-files", "qpro-stereo-tongue-v13-gate.pt");
    Write(Directory.GetParent(extra.Destination)!.FullName, "QproRuntime/models/qpro-stereo-tongue-v13-personal.bin", "unrecognized local companion");
    Write(Directory.GetParent(extra.Destination)!.FullName, "QproRuntime/models/qpro-stereo-tongue-v15-gate.torchscript.pt", "locally rebuilt compiled model");
    string optionsHash = Hash(Path.Combine(extra.Destination, "config", "live-options.json"));
    HubUpdateApply.Apply(extra.Stage, extra.Destination, version);
    Check(File.Exists(Path.Combine(extra.Destination, "models", "qpro-stereo-tongue-v13-gate.pt")) && File.Exists(Path.Combine(extra.Destination, "models", "qpro-stereo-tongue-v15-gate.pt")), "Unknown or changed local companions must preserve their entire model group.");
    Check(Hash(Path.Combine(extra.Destination, "config", "live-options.json")) == optionsHash, "A retained selected model was migrated unnecessarily.");

    var provenance = RetirementFixture("retire-no-provenance");
    string checksumPath = Path.Combine(Directory.GetParent(provenance.Destination)!.FullName, "SHA256SUMS.txt");
    File.WriteAllLines(checksumPath, File.ReadAllLines(checksumPath).Where(line => !line.Contains("qpro-stereo-tongue-v8-", StringComparison.Ordinal)));
    HubUpdateApply.Apply(provenance.Stage, provenance.Destination, version);
    Check(File.Exists(Path.Combine(provenance.Destination, "models", "qpro-stereo-tongue-v8-gate.pt")), "v8 without original package checksums must be preserved.");

    var deduped = RetirementFixture("retire-existing-replacement", "qpro-stereo-tongue-v8-gate.pt");
    string dedupedRoot = Directory.GetParent(deduped.Destination)!.FullName;
    foreach (string suffix in new[] { "-gate.pt", "-direction.pt", ".metadata.json" })
        Write(dedupedRoot, "QproRuntime/models/qpro-stereo-tongue-v21" + suffix, File.ReadAllText(Path.Combine(deduped.Stage, "models", "qpro-stereo-tongue-v15" + suffix)));
    HubUpdateApply.Apply(deduped.Stage, deduped.Destination, version);
    Check(JsonNode.Parse(File.ReadAllText(Path.Combine(deduped.Destination, "config", "live-options.json")))!["Model"]!.GetValue<string>() == "qpro-stereo-tongue-v21-gate.pt", "Retired selection did not map to an existing hash-identical developer bundle.");
    Check(!File.Exists(Path.Combine(deduped.Destination, "models", "qpro-stereo-tongue-v16-gate.pt")), "Deduplication created another developer pair during retirement.");

    var relocatedOld = RetirementFixture("retire-relocated-old-bundle", "qpro-stereo-tongue-v21-gate.pt");
    string relocatedRoot = Directory.GetParent(relocatedOld.Destination)!.FullName;
    foreach (string suffix in new[] { "-gate.pt", "-direction.pt", "-gate.torchscript.pt", ".metadata.json" })
        Write(relocatedRoot, "QproRuntime/models/qpro-stereo-tongue-v21" + suffix, File.ReadAllText(Path.Combine(relocatedOld.Destination, "models", "qpro-stereo-tongue-v15" + suffix)));
    JsonObject relocatedMetadata = JsonNode.Parse(File.ReadAllText(Path.Combine(relocatedOld.Destination, "models", "qpro-stereo-tongue-v21.metadata.json")))!.AsObject();
    relocatedMetadata["version"] = 21;
    relocatedMetadata["sourceVersion"] = 15;
    Write(relocatedRoot, "QproRuntime/models/qpro-stereo-tongue-v21.metadata.json", relocatedMetadata.ToJsonString());
    Write(relocatedRoot, "QproRuntime/models/qpro-stereo-tongue-v15.metadata.json", "{\"origin\":\"imported model\",\"displayName\":\"Personal v15\"}");
    Seal(relocatedRoot);
    HubUpdateApply.Apply(relocatedOld.Stage, relocatedOld.Destination, version);
    Check(!File.Exists(Path.Combine(relocatedOld.Destination, "models", "qpro-stereo-tongue-v21-gate.pt")), "An exactly identified old developer bundle at a relocated version was left behind.");
    Check(File.Exists(Path.Combine(relocatedOld.Destination, "models", "qpro-stereo-tongue-v15-gate.pt")), "A personal imported v15 was removed while retiring the old relocated bundle.");
    Check(JsonNode.Parse(File.ReadAllText(Path.Combine(relocatedOld.Destination, "config", "live-options.json")))!["Model"]!.GetValue<string>() == "qpro-stereo-tongue-v16-gate.pt", "A relocated old bundle selection did not migrate to the new allocated version.");

    var invalidContract = RetirementFixture("retire-invalid-contract");
    string invalidRoot = Directory.GetParent(invalidContract.Stage)!.FullName;
    JsonObject invalidManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(invalidContract.Stage, "release-manifest.json")))!.AsObject();
    invalidManifest["modelRetirement"]!["models"]![0]!["stem"] = "qpro-stereo-tongue-v99";
    Write(invalidRoot, "QproRuntime/release-manifest.json", invalidManifest.ToJsonString());
    Seal(invalidRoot);
    var invalidBefore = Snapshot(invalidContract.Destination);
    Reject(() => HubUpdateApply.Apply(invalidContract.Stage, invalidContract.Destination, version), "an arbitrary personal version retirement contract");
    Check(Same(invalidBefore, Snapshot(invalidContract.Destination)), "An invalid retirement contract changed installed files.");

    var incomplete = RetirementFixture("retire-missing-replacement");
    File.Delete(Path.Combine(incomplete.Stage, "models", "qpro-stereo-tongue-v15-direction.pt"));
    Seal(Directory.GetParent(incomplete.Stage)!.FullName);
    var untouched = Snapshot(incomplete.Destination);
    Reject(() => HubUpdateApply.Apply(incomplete.Stage, incomplete.Destination, version), "retirement with incomplete replacement");
    Check(Same(untouched, Snapshot(incomplete.Destination)), "Incomplete replacement retired an old model or changed its selection.");

    foreach (string boundary in new[] { "QproRuntime/config/live-options.json", "QproRuntime/models/qpro-stereo-tongue-v8-gate.pt", "QproRuntime/models/qpro-stereo-tongue-v13.metadata.json", "QproRuntime/models/qpro-stereo-tongue-v15-direction.pt" })
    {
        var rollback = RetirementFixture("retire-rollback-" + passed);
        var before = Snapshot(rollback.Destination);
        try
        {
            HubUpdateApply.Apply(rollback.Stage, rollback.Destination, version, afterFileApplied: (_, relative) =>
            { if (relative == boundary) throw new IOException("Injected retirement transaction failure"); });
            throw new Exception("Retirement rollback boundary was not reached.");
        }
        catch (HubUpdateApplyException error)
        {
            Check(error.RollbackComplete && Same(before, Snapshot(rollback.Destination)), "Retirement rollback did not restore all original weights/settings: " + boundary);
        }
    }

    var crash = RetirementFixture("retire-crash");
    var crashBefore = Snapshot(crash.Destination);
    int deleteBoundary = 0;
    // Determine the actual file order on a separate isolated fixture.
    var order = RetirementFixture("retire-order");
    HubUpdateApply.Apply(order.Stage, order.Destination, version, afterFileApplied: (count, relative) =>
    { if (relative == "QproRuntime/models/qpro-stereo-tongue-v13-direction.pt") deleteBoundary = count; });
    Check(deleteBoundary > 0, "A retirement deletion was absent from the transaction.");
    Crash(crash, deleteBoundary);
    Check(HubUpdateApply.RequiresRecovery(crash.Destination), "A crash after retirement must retain an unfinished recovery journal.");
    HubUpdateApply.RecoverPending(crash.Destination);
    Check(Same(crashBefore, Snapshot(crash.Destination)), "Interrupted retirement did not recover deleted weights and the saved selection.");
    Console.WriteLine($"PASS: {passed} bundled model retirement assertions; exact identity, settings migration and durable deletion rollback checked.");
}

try
{
    if (args.Length == 1 && args[0] == "--model-collision-fixtures")
    {
        ModelCollisionFixtures();
        return;
    }
    if (args.Length == 1 && args[0] == "--model-retirement-fixtures")
    {
        ModelRetirementFixtures();
        return;
    }
    var normal = Make("normal");
    var before = Snapshot(normal.Destination);
    HubUpdateApplyResult applied = HubUpdateApply.Apply(normal.Stage, normal.Destination, version);
    string installed = Directory.GetParent(normal.Destination)!.FullName;
    Check(applied.ChangedFiles > 0, "An update must replace app files.");
    Check(File.ReadAllText(Path.Combine(installed, "QproFaceTracking.exe")) == "new executable", "New executable was not applied.");
    Check(File.ReadAllText(Path.Combine(normal.Destination, "receiver.py")) == "new receiver.py", "New runtime code was not applied.");
    Check(applied.Warnings.Any(warning => warning.Contains("v8", StringComparison.Ordinal)), "Conflicting developer filename must be explained.");
    foreach (var file in before.Where(file => file.Key.Contains("models", StringComparison.OrdinalIgnoreCase) ||
                 file.Key.Contains("config", StringComparison.OrdinalIgnoreCase) || file.Key.Contains("captures", StringComparison.OrdinalIgnoreCase) ||
                 file.Key.Contains("training", StringComparison.OrdinalIgnoreCase) || file.Key.Contains("calibration", StringComparison.OrdinalIgnoreCase) ||
                 file.Key.Contains("seacliff_eye_model", StringComparison.OrdinalIgnoreCase) || file.Key.Contains(".venv", StringComparison.OrdinalIgnoreCase) ||
                 file.Key.Contains("logs", StringComparison.OrdinalIgnoreCase) || file.Key.Contains("questpro-", StringComparison.OrdinalIgnoreCase) ||
                 file.Key.Contains("unrelated", StringComparison.OrdinalIgnoreCase) || file.Key.Contains("obsolete", StringComparison.OrdinalIgnoreCase)))
        Check(Hash(Path.Combine(installed, file.Key)) == file.Value, "User data changed: " + file.Key);
    Check(File.ReadAllText(Path.Combine(normal.Destination, "models", "qpro-stereo-tongue-v9-direction.pt")) == "new noncolliding direction", "New model pair was not copied.");
    Check(File.Exists(Path.Combine(normal.Destination, "models", "qpro-stereo-tongue-v9.metadata.json")), "New model metadata was not copied.");
    Check(File.Exists(Path.Combine(normal.Destination, "models", "qpro-stereo-tongue-v9-gate.torchscript.pt")), "New model compiled variant was not copied.");
    Check(File.ReadAllText(Path.Combine(normal.Destination, "calibration", "new-bundled-profile.json")) == "new profile with a unique filename", "New profile names should be added while preserving existing calibrations.");
    Check(applied.RetainedBackupDirectory is null, "A completed backup cleanup must not mark a retained backup.");
    Check(!Directory.GetDirectories(Directory.GetParent(Directory.GetParent(normal.Stage)!.FullName)!.FullName, ".qpro-transaction-*").Any(), "Successful backups should be cleaned up.");
    var sameVersion = Snapshot(normal.Destination);
    Reject(() => HubUpdateApply.Apply(normal.Stage, normal.Destination, version), "repeated update at the installed version");
    Check(Same(sameVersion, Snapshot(normal.Destination)), "A repeated update changed the installed app.");

    var equal = Make("equal", equalModels: true);
    Check(!HubUpdateApply.Apply(equal.Stage, equal.Destination, version).Warnings.Any(warning => warning.Contains("v8", StringComparison.Ordinal)), "Equal model files should be skipped without a collision warning.");

    var partial = Make("partial");
    File.Delete(Path.Combine(partial.Destination, "models", "qpro-stereo-tongue-v8-direction.pt"));
    var partialResult = HubUpdateApply.Apply(partial.Stage, partial.Destination, version);
    Check(!File.Exists(Path.Combine(partial.Destination, "models", "qpro-stereo-tongue-v8-direction.pt")) && partialResult.Warnings.Count > 0,
        "A colliding gate must not be paired with a new developer direction.");

    for (int failAt = 1; failAt <= applied.ChangedFiles; failAt++)
    {
        var rollback = Make("rollback-" + failAt);
        var original = Snapshot(rollback.Destination);
        try
        {
            HubUpdateApply.Apply(rollback.Stage, rollback.Destination, version, afterFileApplied: (count, _) =>
            { if (count == failAt) throw new IOException("Injected failure after " + count); });
            throw new Exception("The injected transaction failure was ignored.");
        }
        catch (HubUpdateApplyException error)
        {
            Check(error.RollbackComplete, "Rollback unexpectedly failed: " + error.Message);
            Check(Same(original, Snapshot(rollback.Destination)), "Rollback did not restore every original byte/remove new files at step " + failAt);
            Check(!Directory.Exists(Path.Combine(rollback.Destination, "new-feature")), "Rollback left a newly created directory.");
        }
    }

    // Process termination bypasses catch/finally. Recover from the durable plan,
    // including the manifest/EXE window and a second crash during rollback.
    for (int failAt = 1; failAt <= applied.ChangedFiles; failAt++)
    {
        var interrupted = Make("crash-" + failAt);
        var original = Snapshot(interrupted.Destination);
        Crash(interrupted, failAt);
        Check(HubUpdateApply.HasPendingTransaction(interrupted.Destination), "An interrupted transaction lost its journal.");
        Check(HubUpdateApply.RequiresRecovery(interrupted.Destination), "An interrupted transaction must block normal Hub startup.");
        if (failAt == applied.ChangedFiles) Crash(interrupted, 1, recovery: true);
        HubUpdateApply.RecoverPending(interrupted.Destination);
        Check(!HubUpdateApply.HasPendingTransaction(interrupted.Destination), "Completed recovery retained its journal.");
        Check(Same(original, Snapshot(interrupted.Destination)), "Crash recovery did not restore the original package at step " + failAt);
        Check(HubUpdateApply.Apply(interrupted.Stage, interrupted.Destination, version).ChangedFiles > 0,
            "An interrupted release cannot be retried after recovery.");
    }
    var foreignEdit = Make("crash-foreign-edit");
    Crash(foreignEdit, applied.ChangedFiles);
    Write(Directory.GetParent(foreignEdit.Destination)!.FullName, "QproRuntime/receiver.py", "user edit after crash");
    var edited = Snapshot(foreignEdit.Destination);
    Reject(() => HubUpdateApply.RecoverPending(foreignEdit.Destination), "recovery over an unrelated edit");
    Check(Same(edited, Snapshot(foreignEdit.Destination)), "Recovery changed files before detecting an unrelated edit.");
    Check(HubUpdateApply.HasPendingTransaction(foreignEdit.Destination), "Ambiguous recovery must retain its journal.");

    var retryInterrupted = Make("crash-direct-retry");
    Crash(retryInterrupted, applied.ChangedFiles - 2);
    Check(HubUpdateApply.Apply(retryInterrupted.Stage, retryInterrupted.Destination, version).ChangedFiles > 0,
        "Apply must recover the old manifest before comparing the incoming release version.");

    var damagedBackup = Make("crash-damaged-backup");
    Crash(damagedBackup, 1);
    string damagedContainer = Directory.GetParent(Directory.GetParent(damagedBackup.Stage)!.FullName)!.FullName;
    string damagedTransaction = Directory.GetDirectories(damagedContainer, ".qpro-transaction-*").Single();
    Write(damagedTransaction, "Docs/README.txt", "corrupt original backup");
    var beforeDamagedRecovery = Snapshot(damagedBackup.Destination);
    Reject(() => HubUpdateApply.RecoverPending(damagedBackup.Destination), "recovery from a corrupt original backup");
    Check(Same(beforeDamagedRecovery, Snapshot(damagedBackup.Destination)), "Corrupt-backup recovery altered installed files.");
    Check(Directory.Exists(damagedTransaction), "Corrupt-backup recovery discarded its evidence.");

    var damaged = Make("tampered");
    var untouched = Snapshot(damaged.Destination);
    File.AppendAllText(Path.Combine(damaged.Stage, "receiver.py"), "tampered after staging");
    Reject(() => HubUpdateApply.Apply(damaged.Stage, damaged.Destination, version), "staged file changed after ZIP validation");
    Check(Same(untouched, Snapshot(damaged.Destination)), "A rejected staged update wrote into the installed app.");

    var wrongVersion = Make("wrong-version");
    untouched = Snapshot(wrongVersion.Destination);
    Reject(() => HubUpdateApply.Apply(wrongVersion.Stage, wrongVersion.Destination, new Version(9, 0, 0)), "wrong expected version");
    Check(Same(untouched, Snapshot(wrongVersion.Destination)), "A version mismatch changed the installed app.");
    Reject(() => HubUpdateApply.Apply(normal.Stage, normal.Stage, version), "same staged/destination root");
    string overlap = Path.Combine(Directory.GetParent(normal.Stage)!.FullName, "nested", "QproRuntime");
    Directory.CreateDirectory(overlap);
    Reject(() => HubUpdateApply.Apply(normal.Stage, overlap, version), "nested destination overlap");

    var notReady = Make("not-ready");
    File.Delete(Path.Combine(Directory.GetParent(notReady.Destination)!.FullName, "QproFaceTracking.exe"));
    Reject(() => HubUpdateApply.Apply(notReady.Stage, notReady.Destination, version), "incomplete destination release");
    string sourceRuntime = Path.Combine(fixture, "source", "src");
    Directory.CreateDirectory(sourceRuntime);
    Reject(() => HubUpdateApply.Apply(notReady.Stage, sourceRuntime, version), "source runtime layout");
    var downgrade = Make("downgrade");
    Write(Directory.GetParent(downgrade.Destination)!.FullName, "QproRuntime/release-manifest.json", "{\"name\":\"QproFaceTracking\",\"version\":\"4.0.0\"}");
    untouched = Snapshot(downgrade.Destination);
    Reject(() => HubUpdateApply.Apply(downgrade.Stage, downgrade.Destination, version), "downgrade to an older version");
    Check(Same(untouched, Snapshot(downgrade.Destination)), "A downgrade attempt changed the installed app.");

    var blocked = Make("blocked-target");
    untouched = Snapshot(blocked.Destination);
    File.Delete(Path.Combine(blocked.Destination, "receiver.py"));
    Directory.CreateDirectory(Path.Combine(blocked.Destination, "receiver.py"));
    Reject(() => HubUpdateApply.Apply(blocked.Stage, blocked.Destination, version), "directory occupies destination file");
    Check(Hash(Path.Combine(Directory.GetParent(blocked.Destination)!.FullName, "QproFaceTracking.exe")) == untouched["QproFaceTracking.exe"], "Preflight failure changed the executable.");

    var locked = Make("locked-dll");
    untouched = Snapshot(locked.Destination);
    string lockedDll = Path.Combine(locked.Destination, "vrcft-gaze-bridge", "bin", "Release", "net10.0", "Qpro.GazeBridge.dll");
    using (var hold = new FileStream(lockedDll, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { HubUpdateApply.Apply(locked.Stage, locked.Destination, version); throw new Exception("A locked DLL was overwritten."); }
        catch (HubUpdateApplyException error) { Check(error.RollbackComplete, "Locked-DLL rollback could not restore earlier replacements."); }
    }
    Check(Same(untouched, Snapshot(locked.Destination)), "Locked DLL failure left a partially applied update.");

    var incomplete = Make("rollback-warning");
    try
    {
        HubUpdateApply.Apply(incomplete.Stage, incomplete.Destination, version, afterFileApplied: (_, relative) =>
        {
            Write(Directory.GetParent(incomplete.Destination)!.FullName, relative, "changed by another writer");
            throw new IOException("Fail after a concurrent edit");
        });
        throw new Exception("Concurrent-edit fault was ignored.");
    }
    catch (HubUpdateApplyException error)
    {
        Check(!error.RollbackComplete && error.RollbackWarnings.Count > 0, "Incomplete rollback must report a clear warning.");
        Check(Directory.Exists(error.BackupDirectory), "Incomplete rollback must retain original-file backups.");
        Check(File.ReadAllText(Path.Combine(error.BackupDirectory, "Docs", "README.txt")) == "old documentation", "Retained original backup was incorrect.");
    }

    var concurrent = Make("concurrent");
    bool otherRejected = false;
    HubUpdateApply.Apply(concurrent.Stage, concurrent.Destination, version, afterFileApplied: (count, _) =>
    {
        if (count != 1) return;
        Task.Run(() =>
        {
            try { HubUpdateApply.Apply(concurrent.Stage, concurrent.Destination, version); }
            catch (IOException error) { otherRejected = error.Message.Contains("already applying", StringComparison.Ordinal); }
        }).GetAwaiter().GetResult();
    });
    Check(otherRejected, "Two transactions must not apply to the same destination concurrently.");

    var retained = Make("retained-backup");
    FileStream? backupLock = null;
    try
    {
        HubUpdateApplyResult retainedResult = HubUpdateApply.Apply(retained.Stage, retained.Destination, version, afterFileApplied: (_, relative) =>
        {
            if (relative != "QproFaceTracking.exe") return;
            string container = Directory.GetParent(Directory.GetParent(retained.Stage)!.FullName)!.FullName;
            string transaction = Directory.GetDirectories(container, ".qpro-transaction-*").Single();
            backupLock = new FileStream(Path.Combine(transaction, "Docs", "README.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        Check(retainedResult.RetainedBackupDirectory is not null && Directory.Exists(retainedResult.RetainedBackupDirectory),
            "A retained backup must be reported as a typed directory path.");
        Check(retainedResult.Warnings.Any(warning => warning.Contains("backup folder was kept", StringComparison.Ordinal)), "Backup cleanup failure must explain the retained backup.");
        Check(File.ReadAllText(Path.Combine(Directory.GetParent(retained.Destination)!.FullName, "QproFaceTracking.exe")) == "new executable",
            "A backup cleanup failure must not undo a successful update.");
    }
    finally { backupLock?.Dispose(); }
    Check(HubUpdateApply.HasPendingTransaction(retained.Destination), "Incomplete committed cleanup must retain a completed journal.");
    Check(!HubUpdateApply.RequiresRecovery(retained.Destination), "Committed cleanup must not block a complete installed version.");
    HubUpdateApply.RecoverPending(retained.Destination);
    Check(!HubUpdateApply.HasPendingTransaction(retained.Destination), "Completed-journal recovery did not finish cleanup.");
    Check(File.ReadAllText(Path.Combine(Directory.GetParent(retained.Destination)!.FullName, "QproFaceTracking.exe")) == "new executable",
        "Recovery of a committed transaction must not roll back a successfully installed update.");

    if (OperatingSystem.IsWindows())
    {
        var junction = Make("junction-target");
        string linkedDirectory = Path.Combine(junction.Destination, "new-feature");
        string outsideDirectory = Path.Combine(fixture, "outside-directory");
        Directory.CreateDirectory(outsideDirectory);
        Write(outsideDirectory, "sentinel.txt", "outside directory must stay unchanged");
        // NTFS directory junctions need no Developer Mode/symlink privilege.
        // Both paths are freshly generated fixture paths under this workspace.
        using var create = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /c mklink /J \"" + linkedDirectory + "\" \"" + outsideDirectory + "\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new Exception("Junction fixture command could not start.");
        create.WaitForExit();
        if (create.ExitCode != 0)
        {
            Console.WriteLine("SKIP: Windows denied creation of the isolated junction fixture.");
            // Read-only fallback checks a standard existing Windows junction.
            // No destination exists at the child path, so no update can apply.
            string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Local Settings");
            if ((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0)
            {
                try
                {
                    HubUpdateApply.Apply(junction.Stage, Path.Combine(alias, "qpro-update-nonexistent-" + Guid.NewGuid().ToString("N"), "QproRuntime"), version);
                    throw new Exception("An existing Windows junction ancestor was accepted.");
                }
                catch (IOException error) when (error.Message.Contains("linked folder or file", StringComparison.Ordinal)) { passed++; }
            }
        }
        else
        {
            try
            {
                Reject(() => HubUpdateApply.Apply(junction.Stage, junction.Destination, version), "linked destination ancestor");
                Check(File.ReadAllText(Path.Combine(outsideDirectory, "sentinel.txt")) == "outside directory must stay unchanged", "Updater followed a destination directory junction.");
                Check(File.ReadAllText(Path.Combine(Directory.GetParent(junction.Destination)!.FullName, "QproFaceTracking.exe")) == "old executable", "Junction rejection occurred after destination writes.");
            }
            finally { Directory.Delete(linkedDirectory, recursive: false); }
        }
    }

    var linked = Make("linked-target");
    string linkedReceiver = Path.Combine(linked.Destination, "receiver.py");
    string outside = Path.Combine(fixture, "outside-receiver.py");
    File.WriteAllText(outside, "outside file must stay unchanged");
    File.Delete(linkedReceiver);
    bool linkAvailable = true;
    try { File.CreateSymbolicLink(linkedReceiver, outside); }
    catch (Exception error) when (error is UnauthorizedAccessException or IOException) { linkAvailable = false; }
    if (linkAvailable)
    {
        Reject(() => HubUpdateApply.Apply(linked.Stage, linked.Destination, version), "linked destination file");
        Check(File.ReadAllText(outside) == "outside file must stay unchanged", "Updater followed a destination link.");
    }
    else Console.WriteLine("SKIP: file symlink fixture requires Windows symlink permission.");
    Console.WriteLine($"PASS: {passed} in-place apply fixture assertions; rollback checked after each of {applied.ChangedFiles} applied files.");
}
finally
{
    string allowed = artifacts.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(fixture).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup target.");
    Directory.Delete(fixture, recursive: true);
}
