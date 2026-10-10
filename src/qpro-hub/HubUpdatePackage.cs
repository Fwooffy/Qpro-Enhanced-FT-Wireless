using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal static class HubUpdatePackage
{
    private const long MaximumExpandedBytes = 4_294_967_296;
    private const long MaximumFileBytes = 536_870_912;
    private const int MaximumEntries = 5_000;
    private sealed record ArchiveFile(ZipArchiveEntry Entry, string RelativePath);

    // Check the installed app before downloading. The apply engine repeats
    // this guard when replacing files; this check creates or changes nothing.
    internal static void ValidateInstallPath(string runtimeRoot) => EnsureNoReparse(Path.GetFullPath(runtimeRoot));

    internal static string PrepareStorage(string requested)
    {
        string storage = Path.GetFullPath(requested);
        EnsureNoReparse(storage);
        Directory.CreateDirectory(storage);
        EnsureNoReparse(storage);
        return storage;
    }

    internal static void Discard(HubStagedUpdate update, string updateStorageRoot)
    {
        string package = Directory.GetParent(Path.GetFullPath(update.RuntimeRoot))?.FullName
            ?? throw new InvalidDataException("The staged update has no package folder.");
        string staging = Directory.GetParent(package)?.FullName
            ?? throw new InvalidDataException("The staged update has no temporary folder.");
        if (!Path.GetFullPath(update.ExecutablePath).Equals(Path.Combine(package, "QproFaceTracking.exe"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(staging).StartsWith(".qpro-ready-", StringComparison.Ordinal))
            throw new InvalidDataException("The staged update paths do not match a verified temporary package.");
        DeleteOwnedStaging(Path.GetFullPath(updateStorageRoot), staging);
    }

    internal static void ValidateStagedRelease(string runtimeRoot, Version expectedVersion,
        CancellationToken cancellationToken = default)
    {
        string runtime = Path.GetFullPath(runtimeRoot);
        if (!Path.GetFileName(runtime).Equals("QproRuntime", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged update runtime folder is invalid.");
        string package = Directory.GetParent(runtime)?.FullName
            ?? throw new InvalidDataException("The staged update has no app folder.");
        EnsureNoReparse(package);
        ValidatePackage(package, runtime, expectedVersion, cancellationToken);
    }

    // Extract only into private temporary storage. Applying/restarting happens
    // separately after the Hub closes; this code never changes the running copy.
    internal static HubStagedUpdate Stage(string archivePath, HubUpdateRelease release, string existingRuntimeRoot,
        string updateStorageRoot, IProgress<HubUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (!release.CanInstall) throw new InvalidDataException("The update has no verified release ZIP.");
        string storage = PrepareStorage(updateStorageRoot);
        string previousRoot = Path.GetFullPath(existingRuntimeRoot);
        EnsureNoReparse(previousRoot);
        string staging = Path.Combine(storage, ".qpro-stage-" + Guid.NewGuid().ToString("N"));
        string final = Path.Combine(storage, ".qpro-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Checking update files"));
            using var archive = ZipFile.OpenRead(archivePath);
            List<ArchiveFile> files = InspectArchive(archive, out string packageName);
            long total = files.Sum(file => file.Entry.Length);
            long completed = 0;
            foreach (ArchiveFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Within(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                EnsureNoReparse(destination);
                using Stream source = file.Entry.Open();
                using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(source, target, file.Entry.Length, cancellationToken);
                completed += file.Entry.Length;
                progress?.Report(new("Preparing new version", completed, total));
            }
            string package = Within(staging, packageName);
            string runtime = Path.Combine(package, "QproRuntime");
            ValidatePackage(package, runtime, release.Version, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparse(storage);
            Directory.Move(staging, final);
            string installedPackage = Within(final, packageName);
            progress?.Report(new("Update ready"));
            return new(Path.Combine(installedPackage, "QproFaceTracking.exe"),
                Path.Combine(installedPackage, "QproRuntime"), previousRoot, 0, Array.Empty<string>());
        }
        finally
        {
            DeleteOwnedStaging(storage, staging);
        }
    }

    private static List<ArchiveFile> InspectArchive(ZipArchive archive, out string packageName)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("The update ZIP has an unexpected number of files.");
        var files = new List<ArchiveFile>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (name.Contains('\\') || name.StartsWith('/') || name.Length > 220 || name.Contains('\0'))
                throw new InvalidDataException("The update ZIP contains an unsafe file path.");
            bool isDirectory = name.EndsWith('/');
            string clean = isDirectory ? name[..^1] : name;
            string[] parts = clean.Split('/');
            if (parts.Any(part => !SafeComponent(part))) throw new InvalidDataException("The update ZIP contains an unsafe Windows file name.");
            if (parts.Length < (isDirectory ? 1 : 2)) throw new InvalidDataException("The update ZIP must contain one Qpro app folder.");
            // Unix symbolic links and Windows reparse entries must never be extracted.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update ZIP contains a linked file.");
            if (!allPaths.Add(clean)) throw new InvalidDataException("The update ZIP contains duplicate file paths.");
            roots.Add(parts[0]);
            for (int index = 1; index < parts.Length; index++) directories.Add(string.Join('/', parts[..index]));
            if (isDirectory) { directories.Add(clean); continue; }
            filePaths.Add(clean);
            if (entry.Length < 0 || entry.Length > MaximumFileBytes ||
                (entry.Length > 1_048_576 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 500))
                throw new InvalidDataException("The update ZIP contains an unexpectedly large file.");
            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes) throw new InvalidDataException("The extracted update is larger than the allowed limit.");
            files.Add(new(entry, clean));
        }
        if (roots.Count != 1 || filePaths.Overlaps(directories))
            throw new InvalidDataException("The update ZIP does not have one consistent app folder.");
        packageName = roots.Single();
        if (!packageName.StartsWith("QproFaceTracking", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update ZIP does not contain a Qpro app folder.");
        foreach (string required in new[] { "QproFaceTracking.exe", "QproRuntime/release-manifest.json", "SHA256SUMS.txt" })
            if (!filePaths.Contains(packageName + "/" + required))
                throw new InvalidDataException("The update ZIP is missing " + required + ".");
        return files;
    }

    private static void ValidatePackage(string package, string runtime, Version expected, CancellationToken cancellationToken)
    {
        // Walk without following directory links before opening any metadata.
        string[] suppliedFiles = SafeFiles(package).ToArray();
        string manifestPath = Path.Combine(runtime, "release-manifest.json");
        if (new FileInfo(manifestPath).Length > 65_536) throw new InvalidDataException("The release manifest is too large.");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("updateFormat", out JsonElement updateFormat) ||
            !updateFormat.TryGetInt32(out int format) || format != 1)
            throw new InvalidDataException("This ZIP does not support the Hub updater. Download it manually from the GitHub release page.");
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("name", out JsonElement name) || name.GetString() != "QproFaceTracking" ||
            !root.TryGetProperty("version", out JsonElement version) ||
            !HubUpdateReleaseParser.TryStableVersion(version.GetString() ?? "", out Version? supplied) || supplied != expected)
            throw new InvalidDataException("The downloaded app manifest does not match the selected release.");
        if (new FileInfo(Path.Combine(package, "QproFaceTracking.exe")).Length <= 0)
            throw new InvalidDataException("The update contains an empty app executable.");
        foreach (string required in new[] { "setup-runtime.ps1", "runtime-python.ps1", "receiver.py", "tongue_model_preview.py",
                     "platform-tools/adb.exe", "vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll" })
            if (!File.Exists(Within(runtime, required))) throw new InvalidDataException("The update runtime is missing " + required + ".");
        if (!Directory.Exists(Path.Combine(runtime, "models")) ||
            !Directory.GetFiles(Path.Combine(runtime, "models"), "qpro-stereo-tongue-v*-gate.pt").Any(path =>
                File.Exists(path.Replace("-gate.pt", "-direction.pt", StringComparison.Ordinal))))
            throw new InvalidDataException("The update has no bundled paired lower-face model.");
        string sumsPath = Path.Combine(package, "SHA256SUMS.txt");
        if (new FileInfo(sumsPath).Length > 2_097_152) throw new InvalidDataException("The update checksum list is too large.");
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(sumsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Match match = Regex.Match(line, @"^(?<hash>[0-9a-fA-F]{64})  (?<path>.+)$", RegexOptions.CultureInvariant);
            string relative = match.Groups["path"].Value;
            if (!match.Success || relative.Split('/').Any(part => !SafeComponent(part)) || relative.Contains('\\') ||
                !sums.TryAdd(relative, match.Groups["hash"].Value.ToLowerInvariant()))
                throw new InvalidDataException("The update checksum list contains an invalid or repeated path.");
        }
        string[] actualFiles = suppliedFiles
            .Where(path => !path.Equals(sumsPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (actualFiles.Length != sums.Count) throw new InvalidDataException("The update checksum list does not cover every supplied file.");
        foreach (string file in actualFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(package, file).Replace('\\', '/');
            if (!sums.TryGetValue(relative, out string? expectedHash) || !Hash(file, cancellationToken).Equals(expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("An extracted update file failed its checksum: " + relative);
        }
    }

    private static string Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131_072];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> SafeFiles(string root)
    {
        EnsureNoReparse(root);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
            foreach (string item in Directory.EnumerateFileSystemEntries(directories.Pop(), "*", SearchOption.TopDirectoryOnly))
            {
                EnsureNoReparse(item);
                if (Directory.Exists(item)) directories.Push(item);
                else yield return item;
            }
    }

    private static void CopyBounded(Stream source, Stream target, long expected, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[131_072];
        long written = 0;
        int read;
        while ((read = source.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            written += read;
            if (written > expected) throw new InvalidDataException("An update ZIP file expanded beyond its recorded size.");
            target.Write(buffer, 0, read);
        }
        if (written != expected) throw new InvalidDataException("An update ZIP file ended before its recorded size.");
    }

    private static string Within(string root, string relative)
    {
        string allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update path leaves its staging folder.");
        return path;
    }

    private static bool SafeComponent(string part)
    {
        if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
            part.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))) return false;
        string stem = part.Split('.')[0];
        return !Regex.IsMatch(stem, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void EnsureNoReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update path contains a linked folder or file. Choose a normal local folder.");
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static void DeleteOwnedStaging(string storage, string staging)
    {
        if (!Directory.Exists(staging)) return;
        string root = Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(staging);
        string name = Path.GetFileName(target);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !(name.StartsWith(".qpro-stage-", StringComparison.Ordinal) || name.StartsWith(".qpro-ready-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Unsafe update staging cleanup path.");
        EnsureNoReparse(target);
        var directories = new Stack<string>();
        directories.Push(target);
        while (directories.Count > 0)
            foreach (string item in Directory.EnumerateFileSystemEntries(directories.Pop(), "*", SearchOption.TopDirectoryOnly))
            {
                EnsureNoReparse(item);
                if (Directory.Exists(item)) directories.Push(item);
            }
        Directory.Delete(target, recursive: true);
    }
}
