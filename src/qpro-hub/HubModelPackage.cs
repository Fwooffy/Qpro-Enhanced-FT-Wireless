using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal static class HubModelPackage
{
    private const string Format = "qpro-tongue-model-package-v1";
    private const long MaxCheckpointBytes = 268_435_456;

    internal static void Export(string package, string gatePath, string directionPath, int version,
        string displayName, JsonObject? metadata)
    {
        string destination = Path.GetFullPath(package);
        if (destination.Equals(Path.GetFullPath(gatePath), StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(Path.GetFullPath(directionPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an export file other than the model's gate or direction checkpoint.");

        using var gate = File.OpenRead(gatePath);
        using var direction = File.OpenRead(directionPath);
        if (!ValidCheckpointSize(gate.Length) || !ValidCheckpointSize(direction.Length))
            throw new InvalidDataException("The paired model is empty or too large to export.");

        var manifest = new JsonObject
        {
            ["format"] = Format,
            ["displayName"] = displayName,
            ["sourceVersion"] = version,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        HubModelMetadata.CopyPortableDetails(metadata, manifest);

        string staging = Path.Combine(Path.GetDirectoryName(destination)!, ".qpro-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownsStaging = false;
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsStaging = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create);
                using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), Encoding.UTF8))
                    writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                using (var entry = archive.CreateEntry("gate.pt", CompressionLevel.Optimal).Open())
                    gate.CopyTo(entry);
                using (var entry = archive.CreateEntry("direction.pt", CompressionLevel.Optimal).Open())
                    direction.CopyTo(entry);
            }
            File.Move(staging, destination, overwrite: true);
            ownsStaging = false;
        }
        finally
        {
            if (ownsStaging) File.Delete(staging);
        }
    }

    internal static int NextVersion(string models)
    {
        int maximum = 0;
        if (Directory.Exists(models))
            foreach (string path in Directory.EnumerateFiles(models, "qpro-stereo-tongue-v*"))
            {
                Match match = Regex.Match(Path.GetFileName(path), @"^qpro-stereo-tongue-v(?<v>\d+)(?:[.-]|$)");
                if (match.Success && int.TryParse(match.Groups["v"].Value, out int version))
                    maximum = Math.Max(maximum, version);
            }
        return checked(maximum + 1);
    }

    internal static JsonObject Import(string package, string models, int version,
        Func<JsonObject, JsonObject> metadata)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        Directory.CreateDirectory(models);
        string stem = $"qpro-stereo-tongue-v{version}";
        if (Directory.EnumerateFiles(models, stem + "*").Any(path =>
                Path.GetFileName(path).StartsWith(stem + "-", StringComparison.Ordinal) ||
                Path.GetFileName(path).StartsWith(stem + ".", StringComparison.Ordinal)))
            throw new IOException("This model version already has files. Existing models were kept; retry the import.");
        string staging = Path.Combine(models, ".qpro-import-" + Guid.NewGuid().ToString("N"));
        var created = new List<string>();
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(package);
            ZipArchiveEntry? gate = archive.GetEntry("gate.pt"), direction = archive.GetEntry("direction.pt");
            if (gate is null || direction is null || !ValidCheckpointSize(gate.Length) ||
                !ValidCheckpointSize(direction.Length))
                throw new InvalidDataException("This package does not contain a valid, reasonably sized paired model.");
            ZipArchiveEntry? entry = archive.GetEntry("manifest.json");
            if (entry is null || entry.Length > 262_144)
                throw new InvalidDataException("The model package manifest is missing or too large.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            JsonObject manifest = JsonNode.Parse(reader.ReadToEnd())?.AsObject()
                ?? throw new InvalidDataException("The model package manifest is invalid.");
            if (manifest["format"]?.GetValue<string>() != Format)
                throw new InvalidDataException("This model package format is not supported.");
            gate.ExtractToFile(Path.Combine(staging, "gate.pt"), false);
            direction.ExtractToFile(Path.Combine(staging, "direction.pt"), false);
            File.WriteAllText(Path.Combine(staging, "metadata.json"),
                metadata(manifest).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach ((string source, string ending) in new[] { ("gate.pt", "-gate.pt"),
                         ("direction.pt", "-direction.pt"), ("metadata.json", ".metadata.json") })
            {
                string destination = Path.Combine(models, stem + ending);
                File.Move(Path.Combine(staging, source), destination, false);
                created.Add(destination);
            }
            return manifest;
        }
        catch
        {
            // Only files created by this attempt belong to its rollback. A
            // pre-existing or concurrently created model must never be deleted.
            foreach (string path in created) File.Delete(path);
            throw;
        }
        finally
        {
            string fullStaging = Path.GetFullPath(staging);
            string allowedModels = Path.GetFullPath(models).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullStaging.StartsWith(allowedModels, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe model import staging path.");
            Directory.Delete(fullStaging, true);
        }
    }

    private static bool ValidCheckpointSize(long bytes) => bytes is > 0 and <= MaxCheckpointBytes;
}
