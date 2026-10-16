using System.Text.Json.Nodes;
using System.IO.Compression;
using QproFaceTracking.Hub;

try
{
    int checks = 0;
    void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
    }
    var trained = new JsonObject
    {
        ["modelKind"] = "camera-cheeks-experimental", ["hasCameraCheeks"] = true,
        ["isExperimental"] = true, ["parentModelKind"] = "mustachio-experimental",
        ["parentDisplayName"] = "Mustachio", ["parentVersion"] = 10,
        ["tongueParent"] = "C:\\private\\models\\qpro-stereo-tongue-v10-direction.pt",
        ["tongueParentSha256"] = new string('a', 64), ["trackingSource"] = "VirtualDesktop",
        ["createdUtc"] = "2026-10-02T00:00:00Z", ["origin"] = "prompted cheek camera training",
        ["capturePath"] = "C:\\private\\camera.qpcap",
        ["cheekTraining"] = new JsonObject
        {
            ["trainSamples"] = 100, ["validationSamples"] = 25,
            ["validation"] = new JsonObject { ["mae"] = .04 },
            ["validationGrade"] = "same-session-card-repetitions",
            ["independentWearerValidation"] = false,
            ["trainingWearers"] = 1,
            ["independentCaptureValidation"] = false,
            ["liveValidationComplete"] = false,
            ["freshLivePoseCheck"] = new JsonObject
            {
                ["leftFull"] = new JsonObject { ["left"] = .92, ["right"] = .001 },
                ["rightFull"] = new JsonObject { ["left"] = .0001, ["right"] = .999 }
            },
            ["capturePath"] = "C:\\private\\camera.qpcap",
        }
    };
    // These are the production operations used when renaming, exporting a
    // manifest, then importing it into metadata for a new local version.
    var renamed = new JsonObject { ["displayName"] = "Renamed" };
    HubModelMetadata.CopyPortableDetails(trained, renamed);
    var manifest = new JsonObject { ["format"] = "qpro-tongue-model-package-v1" };
    HubModelMetadata.CopyPortableDetails(renamed, manifest);
    var imported = new JsonObject { ["origin"] = "imported package" };
    HubModelMetadata.CopyPortableDetails(manifest, imported);
    foreach (JsonObject result in new[] { renamed, manifest, imported })
    {
        Check(HubModelMetadata.HasCameraCheeks(result), "combined capability was lost");
        Check(HubModelMetadata.IsExperimental(result), "experimental warning was lost");
        Check(HubModelMetadata.IsMustachio(result), "Mustachio parent classification was lost");
        Check(result["parentVersion"]!.GetValue<int>() == 10, "parent version was lost");
        Check(result["tongueParent"]!.GetValue<string>() == "qpro-stereo-tongue-v10-direction.pt", "parent path was not portable");
        Check(result["cheekTraining"]?["trainSamples"]?.GetValue<int>() == 100, "training counts were lost");
        Check(result["cheekTraining"]?["validation"]?["mae"]?.GetValue<double>() == .04, "held-out metric was lost");
        Check(result["cheekTraining"]?["independentWearerValidation"]?.GetValue<bool>() == false, "validation limitation was lost");
        Check(result["cheekTraining"]?["trainingWearers"]?.GetValue<int>() == 1, "training wearer count was lost");
        Check(result["cheekTraining"]?["independentCaptureValidation"]?.GetValue<bool>() == false, "independent capture limitation was lost");
        Check(result["cheekTraining"]?["liveValidationComplete"]?.GetValue<bool>() == false, "live validation state was lost");
        Check(result["cheekTraining"]?["freshLivePoseCheck"]?["leftFull"]?["left"]?.GetValue<double>() == .92, "fresh left pose aggregate was lost");
        Check(result["cheekTraining"]?["freshLivePoseCheck"]?["rightFull"]?["right"]?.GetValue<double>() == .999, "fresh right pose aggregate was lost");
        Check(result["capturePath"] is null && result["cheekTraining"]?["capturePath"] is null, "private recording path escaped into portable metadata");
        Check(result["trainedUtc"]?.GetValue<string>() == "2026-10-02T00:00:00Z", "original training date was lost");
        Check(result["trainingOrigin"]?.GetValue<string>() == "prompted cheek camera training", "training origin was lost");
    }
    renamed["cheekTraining"]!["trainSamples"] = 999;
    Check(trained["cheekTraining"]!["trainSamples"]!.GetValue<int>() == 100, "renaming aliased the original statistics");
    Check(imported["cheekTraining"]!["trainSamples"]!.GetValue<int>() == 100, "import aliased exported statistics");

    var legacy = new JsonObject { ["displayName"] = "Old tongue model" };
    var oldImport = new JsonObject();
    HubModelMetadata.CopyPortableDetails(legacy, oldImport);
    Check(!HubModelMetadata.HasCameraCheeks(oldImport), "old import gained a cheek head");
    Check(!HubModelMetadata.IsExperimental(oldImport), "ordinary old import gained a false warning");
    var tongueOnlyChild = new JsonObject
    {
        ["modelKind"] = "camera-cheeks-experimental", ["isExperimental"] = true,
        ["cheekTraining"] = trained["cheekTraining"]!.DeepClone()
    };
    var cleanChild = new JsonObject();
    HubModelMetadata.CopyPortableDetails(tongueOnlyChild, cleanChild);
    Check(!HubModelMetadata.HasCameraCheeks(cleanChild), "tongue-only refinement gained capability");
    Check(cleanChild["cheekTraining"] is null, "tongue-only refinement retained obsolete cheek statistics");
    Check(HubModelMetadata.IsExperimental(cleanChild), "experimental parent warning was removed");
    var incompleteFlag = new JsonObject { ["hasCameraCheeks"] = true, ["isExperimental"] = false };
    Check(HubModelMetadata.IsExperimental(incompleteFlag), "camera cheek head was falsely regular");
    string fixture = Path.Combine(Path.GetTempPath(), "qpro-model-import-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixture);
    try
    {
        string package = Path.Combine(fixture, "combined.qptonguemodel");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            {
                var packageManifest = new JsonObject { ["format"] = "qpro-tongue-model-package-v1", ["displayName"] = "Combined" };
                HubModelMetadata.CopyPortableDetails(trained, packageManifest);
                writer.Write(packageManifest.ToJsonString());
            }
            using (var writer = new StreamWriter(zip.CreateEntry("gate.pt").Open())) writer.Write("original gate bytes");
            using (var writer = new StreamWriter(zip.CreateEntry("direction.pt").Open())) writer.Write("combined direction bytes");
        }
        string models = Path.Combine(fixture, "models");
        Directory.CreateDirectory(models);
        string partial = Path.Combine(models, "qpro-stereo-tongue-v40-direction.torchscript.pt");
        File.WriteAllText(partial, "keep incomplete artifact");
        Check(HubModelPackage.NextVersion(models) == 41, "version allocation ignored an unpaired artifact");
        try
        {
            HubModelPackage.Import(package, models, 40, source => HubModelMetadata.Create(40, "Combined", null, "imported package", null, source));
            throw new InvalidOperationException("occupied import unexpectedly succeeded");
        }
        catch (IOException) { checks++; }
        Check(File.ReadAllText(partial) == "keep incomplete artifact", "failed import deleted an existing artifact");
        HubModelPackage.Import(package, models, 41, source => HubModelMetadata.Create(41, "Combined", null, "imported package", null, source));
        JsonObject importedMetadata = JsonNode.Parse(File.ReadAllText(Path.Combine(models, "qpro-stereo-tongue-v41.metadata.json")))!.AsObject();
        Check(HubModelMetadata.HasCameraCheeks(importedMetadata), "actual imported pair lost camera capability");
        Check(HubModelMetadata.IsMustachio(importedMetadata), "actual imported pair lost parent warning");
        Check(importedMetadata["cheekTraining"]?["trainSamples"]?.GetValue<int>() == 100, "actual import lost metrics");
        Check(File.ReadAllText(Path.Combine(models, "qpro-stereo-tongue-v41-gate.pt")) == "original gate bytes", "import changed gate bytes");
        Check(File.ReadAllText(Path.Combine(models, "qpro-stereo-tongue-v41-direction.pt")) == "combined direction bytes", "import changed direction bytes");
        string concurrent = Path.Combine(models, "qpro-stereo-tongue-v42-direction.pt");
        try
        {
            HubModelPackage.Import(package, models, 42, source =>
            {
                File.WriteAllText(concurrent, "another writer owns this model");
                return HubModelMetadata.Create(42, "Combined", null, "imported package", null, source);
            });
            throw new InvalidOperationException("concurrent import unexpectedly succeeded");
        }
        catch (IOException) { checks++; }
        Check(File.ReadAllText(concurrent) == "another writer owns this model", "rollback deleted another writer's checkpoint");
        Check(!File.Exists(Path.Combine(models, "qpro-stereo-tongue-v42-gate.pt")), "rollback left its own partial gate");
        Check(!File.Exists(Path.Combine(models, "qpro-stereo-tongue-v42.metadata.json")), "rollback left misleading metadata");
        Check(!Directory.EnumerateDirectories(models, ".qpro-import-*").Any(), "private import staging was not cleaned");
    }
    finally
    {
        string absoluteFixture = Path.GetFullPath(fixture);
        string allowedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absoluteFixture.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
        Directory.Delete(absoluteFixture, true);
    }
    DeveloperModelFixtures.Run(Check);
    if (args.Length >= 1) DeveloperModelFixtures.RunActualMetadata(args[0], Check);
    if (args.Length >= 2) LegacyModelIdentityFixtures.Run(args[1], Check);
    if (args.Length >= 3) LegacyModelIdentityFixtures.RunCameraBundles(args[1], args[2], Check);
    Console.WriteLine($"{checks} portable model metadata and developer selection checks passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
