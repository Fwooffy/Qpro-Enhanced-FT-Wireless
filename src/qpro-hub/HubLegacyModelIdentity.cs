using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal static class HubLegacyModelIdentity
{
    private const string Stem = "qpro-stereo-tongue-v8";
    private const string GateHash = "57d9d04f1a569e40836cbb7a7af217986f8b65ffad103ff86bc2a2d07afc35ff";
    private const string DirectionHash = "1900e8761c9ceaf89069121af1016ba24c33849836a5b7ee94b4dfd9fc7db396";
    private const string CheekDirectionHash = "52797b420f40c4a39d58d3abd10031b8f6ec7f91cfb7568a5bf7d2e1ba3fa07d";
    private const string CombinedGateHash = "38e05b6625173cc2c7e612757c59134ae692403c2fcd7c9e630b1cf205577246";

    internal static bool IsObsoleteBundledModel(string runtimeRoot, string gate, string direction)
    {
        if (IsOriginalBundledV8(runtimeRoot, gate, direction)) return true;
        string fileName = Path.GetFileName(gate);
        if (!Regex.IsMatch(fileName, @"^qpro-stereo-tongue-v[1-9][0-9]*-gate\.pt$", RegexOptions.CultureInvariant)) return false;
        string stem = fileName[..^"-gate.pt".Length];
        string models = Path.Combine(runtimeRoot, "models");
        if (Path.GetFileName(direction) != stem + "-direction.pt" || !SamePath(Path.GetDirectoryName(gate), models) ||
            !SamePath(Path.GetDirectoryName(direction), models)) return false;
        try
        {
            string metadataPath = Path.Combine(models, stem + ".metadata.json");
            if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 131_072) return false;
            var metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
            bool originalCheeks = stem == "qpro-stereo-tongue-v13" && Text(metadata, "modelKind") == "camera-cheeks-experimental" &&
                Text(metadata, "origin") == "developer tongue with an experimental camera cheek head";
            bool combinedDeveloper = Text(metadata, "modelKind") == "developer-lower-face" &&
                Text(metadata, "origin") == "bundled developer model" && Flag(metadata, "bundledDeveloperModel");
            if ((!originalCheeks && !combinedDeveloper) || !Flag(metadata, "hasCameraCheeks") ||
                Hash(gate) != (originalCheeks ? GateHash : CombinedGateHash) || Hash(direction) != CheekDirectionHash) return false;
            string checksumRoot = ChecksumRoot(runtimeRoot);
            var checksums = ReadChecksums(Path.Combine(checksumRoot, "SHA256SUMS.txt"));
            return CompanionsAreOriginal(models, stem, checksumRoot, checksums, allowMetadata: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // Version numbers and unchanged weights alone do not establish ownership.
    // Keep unproven or personalized v8 copies eligible for carry-forward.
    internal static bool IsOriginalBundledV8(string runtimeRoot, string gate, string direction)
    {
        if (Path.GetFileName(gate) != Stem + "-gate.pt" || Path.GetFileName(direction) != Stem + "-direction.pt") return false;
        string models = Path.Combine(runtimeRoot, "models");
        if (!SamePath(Path.GetDirectoryName(gate), models) || !SamePath(Path.GetDirectoryName(direction), models) ||
            File.Exists(Path.Combine(models, Stem + ".metadata.json"))) return false;
        try
        {
            string manifestPath = Path.Combine(runtimeRoot, "release-manifest.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 131_072) return false;
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            if (Text(manifest, "bundledTongueModel") != Stem && Text(manifest, "fallbackTongueModel") != Stem) return false;
            string checksumRoot = ChecksumRoot(runtimeRoot);
            var checksums = ReadChecksums(Path.Combine(checksumRoot, "SHA256SUMS.txt"));
            string gateKey = Path.GetRelativePath(checksumRoot, gate).Replace('\\', '/');
            string directionKey = Path.GetRelativePath(checksumRoot, direction).Replace('\\', '/');
            if (!checksums.TryGetValue(gateKey, out string? gateExpected) || gateExpected != GateHash ||
                !checksums.TryGetValue(directionKey, out string? directionExpected) || directionExpected != DirectionHash ||
                Hash(gate) != GateHash || Hash(direction) != DirectionHash) return false;
            return CompanionsAreOriginal(models, Stem, checksumRoot, checksums, allowMetadata: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string ChecksumRoot(string runtimeRoot) =>
        !File.Exists(Path.Combine(runtimeRoot, "SHA256SUMS.txt")) &&
        Path.GetFileName(runtimeRoot.TrimEnd(Path.DirectorySeparatorChar)) == "QproRuntime"
            ? Path.GetDirectoryName(runtimeRoot)! : runtimeRoot;

    private static bool CompanionsAreOriginal(string models, string stem, string checksumRoot,
        Dictionary<string, string> checksums, bool allowMetadata)
    {
        foreach (string companion in Directory.EnumerateFiles(models).Where(path =>
            Path.GetFileName(path).StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)))
        {
            string suffix = Path.GetFileName(companion)[stem.Length..];
            if (suffix is "-gate.pt" or "-direction.pt" || allowMetadata && suffix == ".metadata.json") continue;
            if (suffix is not ("-gate.torchscript.pt" or "-direction.torchscript.pt") ||
                !checksums.TryGetValue(Path.GetRelativePath(checksumRoot, companion).Replace('\\', '/'), out string? expected) ||
                Hash(companion) != expected) return false;
        }
        return true;
    }

    private static Dictionary<string, string> ReadChecksums(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path) || new FileInfo(path).Length > 2_097_152) return result;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Match match = Regex.Match(line, @"^(?<hash>[0-9a-f]{64})  (?<path>.+)$", RegexOptions.CultureInvariant);
            if (!match.Success || result.Count >= 5000 || !result.TryAdd(match.Groups["path"].Value, match.Groups["hash"].Value)) return [];
        }
        return result;
    }

    private static string? Text(JsonObject? value, string key) =>
        value?[key] is JsonValue field && field.TryGetValue<string>(out string? text) ? text : null;

    private static bool Flag(JsonObject? value, string key) =>
        value?[key] is JsonValue field && field.TryGetValue<bool>(out bool flag) && flag;

    private static bool SamePath(string? left, string right) => left is not null &&
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
