using System.Security.Cryptography;
using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

internal static class LegacyModelIdentityFixtures
{
    internal static void RunCameraBundles(string originalModels, string combinedModels, Action<bool, string> check)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "qpro-camera-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "models"));
        try
        {
            foreach (int version in new[] { 13, 15, 27 })
            {
                string stem = "qpro-stereo-tongue-v" + version;
                string gate = Path.Combine(fixture, "models", stem + "-gate.pt");
                string direction = Path.Combine(fixture, "models", stem + "-direction.pt");
                string sidecar = Path.Combine(fixture, "models", stem + ".metadata.json");
                string sourceGate = Path.Combine(version == 13 ? originalModels : combinedModels,
                    version == 13 ? "qpro-stereo-tongue-v8-gate.pt" : "qpro-stereo-tongue-v15-gate.pt");
                File.Copy(sourceGate, gate);
                File.Copy(Path.Combine(combinedModels, "qpro-stereo-tongue-v15-direction.pt"), direction);
                var metadata = new JsonObject
                {
                    ["modelKind"] = version == 13 ? "camera-cheeks-experimental" : "developer-lower-face",
                    ["origin"] = version == 13 ? "developer tongue with an experimental camera cheek head" : "bundled developer model",
                    ["hasCameraCheeks"] = true, ["bundledDeveloperModel"] = version != 13
                };
                File.WriteAllText(sidecar, metadata.ToJsonString());
                check(IsBundle(), "exact obsolete camera bundle was not recognized at v" + version);
                string developerGate = Path.Combine(fixture, "models", "qpro-stereo-tongue-v35-gate.pt");
                var developerMetadata = new JsonObject { ["modelKind"] = "developer-tongue",
                    ["origin"] = "bundled developer model", ["bundledDeveloperModel"] = true };
                HubModelOption[] choices = [new(gate, version, metadata), new(developerGate, 35, developerMetadata),
                    new(Path.Combine(fixture, "models", "qpro-stereo-tongue-v12-gate.pt"), 12,
                        new JsonObject { ["modelKind"] = "mustachio-experimental" })];
                check(Choose(gate) == developerGate,
                    "retired local selection did not fall back to the current developer bundle at v" + version);
                metadata["origin"] = "imported package";
                File.WriteAllText(sidecar, metadata.ToJsonString());
                check(!IsBundle(), "an imported camera model was excluded at v" + version);
                check(Choose(gate) == gate, "local discovery replaced a saved imported model at v" + version);
                metadata["origin"] = version == 13 ? "developer tongue with an experimental camera cheek head" : "bundled developer model";
                metadata["modelKind"] = "personal-refinement";
                File.WriteAllText(sidecar, metadata.ToJsonString());
                check(!IsBundle(), "a personal refinement was excluded at v" + version);
                metadata["modelKind"] = version == 13 ? "camera-cheeks-experimental" : "developer-lower-face";
                if (version != 13)
                {
                    metadata["bundledDeveloperModel"] = false;
                    File.WriteAllText(sidecar, metadata.ToJsonString());
                    check(!IsBundle(), "an exported combined copy gained bundled ownership at v" + version);
                    metadata["bundledDeveloperModel"] = true;
                }
                File.WriteAllText(sidecar, metadata.ToJsonString());
                string extra = Path.Combine(fixture, "models", stem + ".private.json");
                File.WriteAllText(extra, "personal companion");
                check(!IsBundle(), "a personalized companion was excluded at v" + version);
                File.Delete(extra);
                File.WriteAllText(gate, "personal gate at same model number");
                check(!IsBundle(), "same-version personal weights were excluded at v" + version);
                File.Delete(sidecar);
                check(!IsBundle(), "same-version weights without ownership were excluded at v" + version);
                bool IsBundle() => HubLegacyModelIdentity.IsObsoleteBundledModel(fixture, gate, direction);
                string? Choose(string saved) => HubModelSelection.ChooseGatePath(choices.Where(option =>
                    !HubLegacyModelIdentity.IsObsoleteBundledModel(fixture, option.GatePath,
                        option.GatePath[..^"-gate.pt".Length] + "-direction.pt")), saved);
            }
        }
        finally
        {
            string absolute = Path.GetFullPath(fixture);
            string allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(absolute, true);
        }
    }

    internal static void Run(string originalModels, Action<bool, string> check)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "qpro-original-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "models"));
        const string stem = "qpro-stereo-tongue-v8";
        string gate = Path.Combine(fixture, "models", stem + "-gate.pt");
        string direction = Path.Combine(fixture, "models", stem + "-direction.pt");
        string sidecar = Path.Combine(fixture, "models", stem + ".metadata.json");
        string manifest = Path.Combine(fixture, "release-manifest.json");
        string sums = Path.Combine(fixture, "SHA256SUMS.txt");
        File.Copy(Path.Combine(originalModels, stem + "-gate.pt"), gate);
        File.Copy(Path.Combine(originalModels, stem + "-direction.pt"), direction);
        string gateHash = Hash(gate), directionHash = Hash(direction);
        try
        {
            File.WriteAllText(manifest, "{\"bundledTongueModel\":\"" + stem + "\"}");
            string originalSums = $"{gateHash}  models/{stem}-gate.pt\n{directionHash}  models/{stem}-direction.pt\n";
            check(!IsBundle(), "unchanged v8 without checksum provenance was excluded from carry-forward");
            File.WriteAllText(sums, originalSums);
            check(IsBundle(), "exact original v8 with manifest/checksums was not recognized");
            string developerGate = Path.Combine(fixture, "models", "qpro-stereo-tongue-v35-gate.pt");
            HubModelOption[] choices = [new(gate, 8, null), new(developerGate, 35,
                new JsonObject { ["modelKind"] = "developer-tongue", ["origin"] = "bundled developer model", ["bundledDeveloperModel"] = true }),
                new(Path.Combine(fixture, "models", "qpro-stereo-tongue-v12-gate.pt"), 12,
                    new JsonObject { ["modelKind"] = "mustachio-experimental" })];
            check(Choose(gate) == developerGate, "retired v8 selection fell back to Mustachio instead of the developer model");
            File.WriteAllText(sidecar, "{\"origin\":\"imported package\"}");
            check(!IsBundle(), "an imported v8 sidecar was ignored");
            check(Choose(gate) == gate, "local discovery replaced a saved imported v8 model");
            File.WriteAllText(sidecar, "not-json");
            check(!IsBundle(), "a malformed personal v8 sidecar was ignored");
            File.Delete(sidecar);
            File.WriteAllText(manifest, "{\"bundledTongueModel\":\"qpro-stereo-tongue-v15\"}");
            check(!IsBundle(), "v8 weights were excluded without original manifest identity");
            File.WriteAllText(manifest, "{\"fallbackTongueModel\":\"" + stem + "\"}");
            check(IsBundle(), "exact original fallback v8 was not recognized");
            File.AppendAllText(sums, $"{gateHash}  models/{stem}-gate.pt\n");
            check(!IsBundle(), "duplicate checksum records were trusted");
            File.WriteAllText(sums, originalSums);
            string extra = Path.Combine(fixture, "models", stem + ".private.json");
            File.WriteAllText(extra, "personal companion");
            check(!IsBundle(), "additional personal v8 files were ignored");
            File.Delete(extra);
            string scripted = Path.Combine(fixture, "models", stem + "-gate.torchscript.pt");
            File.WriteAllText(scripted, "original scripted companion");
            check(!IsBundle(), "unproven scripted v8 was excluded");
            File.AppendAllText(sums, $"{Hash(scripted)}  models/{Path.GetFileName(scripted)}\n");
            check(IsBundle(), "verified original scripted v8 companion was not recognized");
            File.AppendAllText(scripted, "changed");
            check(!IsBundle(), "changed scripted v8 was excluded");
            File.Delete(scripted);
            File.WriteAllText(sums, originalSums);
            byte initial;
            using (var file = new FileStream(gate, FileMode.Open, FileAccess.ReadWrite))
            {
                initial = (byte)file.ReadByte(); file.Position = 0; file.WriteByte((byte)(initial ^ 1));
            }
            check(!IsBundle(), "modified personal v8 gate was excluded by version or checksum record alone");
            using (var file = new FileStream(gate, FileMode.Open, FileAccess.Write)) file.WriteByte(initial);
            check(IsBundle(), "restored exact v8 identity was not recognized");
            File.WriteAllText(direction, "personal direction");
            check(!IsBundle(), "modified personal v8 direction was excluded");
            File.WriteAllText(sums, $"{Hash(gate)}  models/{stem}-gate.pt\n{Hash(direction)}  models/{stem}-direction.pt\n");
            check(!IsBundle(), "personal checksums could replace pinned original identity");
            check(Hash(Path.Combine(originalModels, stem + "-gate.pt")) == gateHash &&
                Hash(Path.Combine(originalModels, stem + "-direction.pt")) == directionHash,
                "the source original pair changed during read-only fixture checks");
            string? Choose(string saved) => HubModelSelection.ChooseGatePath(choices.Where(option =>
                !HubLegacyModelIdentity.IsObsoleteBundledModel(fixture, option.GatePath,
                    option.GatePath[..^"-gate.pt".Length] + "-direction.pt")), saved);
        }
        finally
        {
            string absolute = Path.GetFullPath(fixture);
            string allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(absolute, true);
        }
        bool IsBundle() => HubLegacyModelIdentity.IsOriginalBundledV8(fixture, gate, direction);
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}
