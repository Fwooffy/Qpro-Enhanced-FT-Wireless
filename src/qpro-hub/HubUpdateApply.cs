using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed record HubUpdateApplyResult(int ChangedFiles, IReadOnlyList<string> Warnings, string? RetainedBackupDirectory = null);

internal sealed class HubUpdateApplyException : IOException
{
    internal bool RollbackComplete { get; }
    internal IReadOnlyList<string> RollbackWarnings { get; }
    internal string BackupDirectory { get; }

    internal HubUpdateApplyException(Exception cause, string backupDirectory, IReadOnlyList<string> warnings)
        : base(warnings.Count == 0
            ? "The update failed. Changed app files were rolled back; the previous version was kept. " + cause.Message
            : "The update failed and rollback could not finish. Keep the backup folder at " + backupDirectory +
              " and review these errors before using the app: " + string.Join("; ", warnings), cause)
    {
        RollbackComplete = warnings.Count == 0;
        RollbackWarnings = warnings;
        BackupDirectory = backupDirectory;
    }
}

internal static class HubUpdateApply
{
    private const string JournalName = ".qpro-update-transaction.json";
    private sealed record JournalFile(string RelativePath, string NewHash, string? OriginalHash,
        DateTime OriginalModified, FileAttributes OriginalAttributes, string TemporaryPath);
    private sealed record Journal(int Schema, string Package, string BackupDirectory, bool Completed,
        List<JournalFile> Files, List<string> CreatedDirectories);
    private sealed record Change(string RelativePath, string Source, string Destination, string NewHash,
        string? OriginalHash, DateTime OriginalModified, FileAttributes OriginalAttributes);

    // The caller stops its Hub before entering this method. This engine only
    // changes package files: it launches no executable, installer, or ADB command.
    internal static HubUpdateApplyResult Apply(string stagedRuntimeRoot, string destinationRuntimeRoot,
        Version expectedVersion, IProgress<HubUpdateProgress>? progress = null,
        Action<int, string>? afterFileApplied = null)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        string stagedRuntime = Normalize(stagedRuntimeRoot);
        string destinationRuntime = Normalize(destinationRuntimeRoot);
        RequireRuntimeShape(stagedRuntime);
        RequireRuntimeShape(destinationRuntime);
        string stagedPackage = Directory.GetParent(stagedRuntime)!.FullName;
        string destinationPackage = Directory.GetParent(destinationRuntime)!.FullName;
        if (Overlaps(stagedPackage, destinationPackage))
            throw new InvalidDataException("The staged update and installed app folders must be separate.");
        EnsureNoReparse(stagedPackage);
        EnsureNoReparse(destinationPackage);

        string mutexName = InstallMutexName(destinationPackage);
        using var transactionLock = new Mutex(false, mutexName);
        bool acquired;
        try { acquired = transactionLock.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another update is already applying to this app folder.");
        try
        {
            RecoverPendingLocked(destinationPackage);
            if (HasPendingTransaction(destinationRuntime))
                throw new IOException("A previous update's recovery files could not be cleaned up. Keep its backup and try again after closing any app using those files.");
            // Recheck the clean staged payload after the original Hub exits.
            // The caller also pins SHA256SUMS itself in its handoff plan.
            HubUpdatePackage.ValidateStagedRelease(stagedRuntime, expectedVersion);
            ValidateDestination(destinationPackage, destinationRuntime, expectedVersion);
            var warnings = new List<string>();
            List<Change> changes = Plan(stagedPackage, destinationPackage, warnings);
            progress?.Report(new("Applying the verified update"));
            string stagingContainer = Directory.GetParent(stagedPackage)!.FullName;
            EnsureNoReparse(stagingContainer);
            string backupDirectory = Within(stagingContainer, ".qpro-transaction-" + Guid.NewGuid().ToString("N"));
            var createdDirectories = new List<string>();
            var temporaryFiles = new List<string>();
            Journal? journal = null;
            int changed = 0;
            try
            {
                Directory.CreateDirectory(backupDirectory);
                EnsureNoReparse(backupDirectory);
                // Back up and flush every original before publishing the journal.
                // No installed path changes until recovery has a complete plan.
                foreach (Change change in changes)
                {
                    if (change.OriginalHash is not null)
                    {
                        string backup = Within(backupDirectory, change.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        EnsureNoReparse(backup);
                        CopyDurably(change.Destination, backup);
                        if (Hash(backup) != change.OriginalHash)
                            throw new IOException("An original app file could not be backed up: " + change.RelativePath);
                    }
                    for (string? parent = Path.GetDirectoryName(change.Destination); parent is not null &&
                         !parent.Equals(destinationPackage, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(parent);
                         parent = Path.GetDirectoryName(parent))
                        if (!createdDirectories.Contains(parent, StringComparer.OrdinalIgnoreCase)) createdDirectories.Add(parent);
                    temporaryFiles.Add(Within(Path.GetDirectoryName(change.Destination)!, ".qpro-update-" + Guid.NewGuid().ToString("N") + ".tmp"));
                }
                journal = new(1, destinationPackage, backupDirectory, false,
                    changes.Select((change, index) => new JournalFile(change.RelativePath, change.NewHash, change.OriginalHash,
                        change.OriginalModified, change.OriginalAttributes, Path.GetRelativePath(destinationPackage, temporaryFiles[index]).Replace('\\', '/'))).ToList(),
                    createdDirectories.Select(path => Path.GetRelativePath(destinationPackage, path).Replace('\\', '/')).ToList());
                WriteJournal(destinationPackage, journal);
                foreach (Change change in changes)
                {
                    EnsureNoReparse(change.Source);
                    EnsureNoReparse(change.Destination);
                    if (!Hash(change.Source).Equals(change.NewHash, StringComparison.Ordinal))
                        throw new IOException("A staged file changed while the update was applying: " + change.RelativePath);
                    bool exists = File.Exists(change.Destination);
                    if (exists != (change.OriginalHash is not null) ||
                        exists && !Hash(change.Destination).Equals(change.OriginalHash, StringComparison.Ordinal))
                        throw new IOException("An installed file changed while the update was applying: " + change.RelativePath);
                    CreateParents(destinationPackage, Path.GetDirectoryName(change.Destination)!, []);
                    string temporary = temporaryFiles[changed];
                    CopyDurably(change.Source, temporary);
                    EnsureNoReparse(temporary);
                    if (!Hash(temporary).Equals(change.NewHash, StringComparison.Ordinal))
                        throw new IOException("An update file could not be copied intact: " + change.RelativePath);
                    EnsureNoReparse(change.Destination);
                    // The temporary file is on the destination volume. Replacing
                    // its directory entry never writes through an existing hard link.
                    File.Move(temporary, change.Destination, overwrite: exists);
                    changed++;
                    afterFileApplied?.Invoke(changed, change.RelativePath);
                    progress?.Report(new("Updating app files", changed, changes.Count));
                }
                WriteJournal(destinationPackage, journal with { Completed = true });
            }
            catch (Exception error)
            {
                var rollbackWarnings = new List<string>();
                if (File.Exists(Path.Combine(destinationPackage, JournalName)))
                {
                    try { RecoverPendingLocked(destinationPackage); }
                    catch (Exception recovery) { rollbackWarnings.Add(recovery.Message); }
                }
                else CleanupBackup(backupDirectory, rollbackWarnings);
                throw new HubUpdateApplyException(error, backupDirectory, rollbackWarnings);
            }
            CleanupTemporaryFiles(temporaryFiles, warnings);
            bool backupRemoved = CleanupBackup(backupDirectory, warnings);
            if (backupRemoved)
            {
                try { File.Delete(Path.Combine(destinationPackage, JournalName)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { warnings.Add("Update applied; its completed recovery journal could not be removed: " + error.Message); }
            }
            return new(changed, warnings, backupRemoved ? null : backupDirectory);
        }
        finally { transactionLock.ReleaseMutex(); }
    }

    // The packaged Hub holds this same lock while open, so a second launch
    // cannot start reading app files partway through an update transaction.
    internal static string InstallMutexName(string package) => "Local\\QproUpdateApply-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(package).ToUpperInvariant())));

    internal static bool HasPendingTransaction(string runtime) =>
        File.Exists(Path.Combine(Directory.GetParent(Normalize(runtime))!.FullName, JournalName));

    internal static bool RequiresRecovery(string runtime)
    {
        string package = Directory.GetParent(Normalize(runtime))!.FullName;
        string path = Path.Combine(package, JournalName);
        if (!File.Exists(path)) return false;
        try
        {
            return !ReadJournal(package, path).Completed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return true; }
    }

    // Run from the copied helper, after the installed Hub has exited. Windows
    // cannot restore an executable while that same executable is still mapped.
    internal static void RecoverPending(string runtime, Action<int, string>? afterFileRecovered = null)
    {
        string package = Directory.GetParent(Normalize(runtime))!.FullName;
        using var transactionLock = new Mutex(false, InstallMutexName(package));
        bool acquired;
        try { acquired = transactionLock.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Close the other Hub before recovering its interrupted update.");
        try { RecoverPendingLocked(package, afterFileRecovered); }
        finally { transactionLock.ReleaseMutex(); }
    }

    private static void CopyDurably(string source, string destination)
    {
        EnsureNoReparse(source);
        EnsureNoReparse(destination);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void WriteJournal(string package, Journal journal)
    {
        string destination = Path.Combine(package, JournalName);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        EnsureNoReparse(destination);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, journal);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RecoverPendingLocked(string package, Action<int, string>? afterFileRecovered = null)
    {
        string path = Path.Combine(package, JournalName);
        EnsureNoReparse(path);
        if (!File.Exists(path)) return;
        try
        {
            Journal journal = ReadJournal(package, path);
            if (!journal.Completed)
            {
                // Preflight all targets and backups before changing any file.
                // A foreign edit is evidence to retain, never permission to overwrite.
                foreach (JournalFile file in journal.Files)
                {
                    string destination = Within(package, file.RelativePath);
                    string? current = File.Exists(destination) ? Hash(destination) : null;
                    if (current != file.OriginalHash && current != file.NewHash)
                        throw new IOException("An installed file changed outside the interrupted update: " + file.RelativePath);
                    if (file.OriginalHash is not null)
                    {
                        string backup = Within(journal.BackupDirectory, file.RelativePath);
                        EnsureNoReparse(backup);
                        if (!File.Exists(backup) || Hash(backup) != file.OriginalHash)
                            throw new IOException("An original-file backup is missing or changed: " + file.RelativePath);
                    }
                }
                int recovered = 0;
                foreach (JournalFile file in journal.Files.AsEnumerable().Reverse())
                {
                    string destination = Within(package, file.RelativePath);
                    string? current = File.Exists(destination) ? Hash(destination) : null;
                    if (current == file.OriginalHash)
                    {
                        if (current is not null)
                        {
                            File.SetLastWriteTimeUtc(destination, file.OriginalModified);
                            File.SetAttributes(destination, file.OriginalAttributes);
                        }
                        continue;
                    }
                    if (current != file.NewHash) throw new IOException("A file changed during recovery: " + file.RelativePath);
                    EnsureNoReparse(destination);
                    if (file.OriginalHash is null) File.Delete(destination);
                    else
                    {
                        string temporary = Within(package, file.TemporaryPath);
                        if (File.Exists(temporary)) File.Delete(temporary);
                        CopyDurably(Within(journal.BackupDirectory, file.RelativePath), temporary);
                        if (Hash(temporary) != file.OriginalHash) throw new IOException("A recovery copy failed its checksum.");
                        EnsureNoReparse(destination);
                        if (!File.Exists(destination) || Hash(destination) != file.NewHash)
                            throw new IOException("A file changed during recovery: " + file.RelativePath);
                        File.Move(temporary, destination, overwrite: true);
                        File.SetLastWriteTimeUtc(destination, file.OriginalModified);
                        File.SetAttributes(destination, file.OriginalAttributes);
                    }
                    afterFileRecovered?.Invoke(++recovered, file.RelativePath);
                }
                // Persist rollback completion before cleanup, so a second crash
                // cannot make partially removed backups look like lost originals.
                WriteJournal(package, journal with { Completed = true });
            }
            var warnings = new List<string>();
            CleanupTemporaryFiles(journal.Files.Select(file => Within(package, file.TemporaryPath)), warnings);
            foreach (string directory in journal.CreatedDirectories.OrderByDescending(value => value.Length))
            {
                string created = Within(package, directory);
                if (Directory.Exists(created) && !Directory.EnumerateFileSystemEntries(created).Any())
                    Directory.Delete(created, recursive: false);
            }
            if (!CleanupBackup(journal.BackupDirectory, warnings) || warnings.Count != 0)
                throw new IOException(string.Join("; ", warnings));
            File.Delete(path);
        }
        catch (Exception error)
        {
            throw new IOException("An interrupted update needs recovery before this Hub can open. Keep " + path +
                " and its referenced backup folder. " + error.Message, error);
        }

    }

    private static Journal ReadJournal(string package, string path)
    {
        EnsureNoReparse(path);
        if (new FileInfo(path).Length > 8_388_608) throw new InvalidDataException("The recovery journal is too large.");
        Journal journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The recovery journal is empty.");
        if (journal.Schema != 1 || !Normalize(journal.Package).Equals(package, StringComparison.OrdinalIgnoreCase) ||
            journal.Files is null || journal.Files.Count > 5000 || journal.CreatedDirectories is null || journal.CreatedDirectories.Count > 5000 ||
            !Regex.IsMatch(Path.GetFileName(journal.BackupDirectory), @"^\.qpro-transaction-[0-9a-f]{32}$") ||
            Overlaps(package, Normalize(journal.BackupDirectory)))
            throw new InvalidDataException("The recovery journal is invalid.");
        EnsureNoReparse(journal.BackupDirectory);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalFile file in journal.Files)
        {
            if (file is null) throw new InvalidDataException("The recovery journal contains an empty file entry.");
            CheckRelative(file.RelativePath);
            CheckRelative(file.TemporaryPath);
            if (!unique.Add(file.RelativePath) || file.RelativePath.Equals(JournalName, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(file.NewHash, @"^[0-9a-f]{64}$") ||
                file.OriginalHash is not null && !Regex.IsMatch(file.OriginalHash, @"^[0-9a-f]{64}$") ||
                Path.GetDirectoryName(file.RelativePath) != Path.GetDirectoryName(file.TemporaryPath) ||
                !Regex.IsMatch(Path.GetFileName(file.TemporaryPath), @"^\.qpro-update-[0-9a-f]{32}\.tmp$"))
                throw new InvalidDataException("The recovery journal contains an invalid file entry.");
            ValidateFileTarget(package, Within(package, file.RelativePath));
            ValidateFileTarget(package, Within(package, file.TemporaryPath));
        }
        foreach (string directory in journal.CreatedDirectories)
        {
            CheckRelative(directory);
            EnsureNoReparse(Within(package, directory));
            if (!journal.Files.Any(file => file.RelativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The recovery journal contains an unrelated directory.");
        }
        return journal;
    }

    private static void CheckRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Split('/').Any(part =>
            part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))))
            throw new InvalidDataException("The recovery journal contains an unsafe path.");
    }

    private static List<Change> Plan(string stagedPackage, string destinationPackage, List<string> warnings)
    {
        var changes = new List<Change>();
        var models = new Dictionary<string, List<(string Source, string Relative)>>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in EnumerateSafeFiles(stagedPackage))
        {
            string relative = Path.GetRelativePath(stagedPackage, source).Replace('\\', '/');
            if (relative.Equals(JournalName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package contains a reserved updater recovery file.");
            if (Preserve(relative)) continue;
            if (relative.StartsWith("QproRuntime/calibration/", StringComparison.OrdinalIgnoreCase))
            {
                string destination = Within(destinationPackage, relative);
                ValidateFileTarget(destinationPackage, destination);
                if (!File.Exists(destination)) Add(source, relative);
                continue;
            }
            if (relative.StartsWith("QproRuntime/models/", StringComparison.OrdinalIgnoreCase))
            {
                string name = relative["QproRuntime/models/".Length..];
                Match match = Regex.Match(name, @"^(?<stem>qpro-stereo-tongue-v\d+)(?:[-.].*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                string group = match.Success ? match.Groups["stem"].Value : name;
                if (!models.TryGetValue(group, out var files)) models[group] = files = [];
                files.Add((source, relative));
            }
            else Add(source, relative);
        }
        foreach (var model in models)
        {
            // Gate, direction, compiled variants and metadata form one model.
            // A partial collision must never create a pair from mixed versions.
            bool pair = model.Value.Any(file => file.Relative.EndsWith(model.Key + "-gate.pt", StringComparison.OrdinalIgnoreCase)) &&
                model.Value.Any(file => file.Relative.EndsWith(model.Key + "-direction.pt", StringComparison.OrdinalIgnoreCase));
            if (!pair)
            {
                warnings.Add("The supplied model files for " + model.Key + " were not installed because they do not contain a complete gate/direction pair.");
                continue;
            }
            bool conflict = false;
            foreach (var file in model.Value)
            {
                string destination = Within(destinationPackage, file.Relative);
                ValidateFileTarget(destinationPackage, destination);
                if (File.Exists(destination) && Hash(destination) != Hash(file.Source)) conflict = true;
            }
            if (conflict)
            {
                warnings.Add("Existing model " + model.Key + " was kept. The new bundled copy was not installed because its filenames conflict with different existing model files.");
                continue;
            }
            foreach (var file in model.Value) Add(file.Source, file.Relative);
        }
        // Record the new identity last. Keep the current executable available
        // until the code and helpers that it uses have been replaced.
        return changes.OrderBy(change => change.RelativePath.Equals("QproFaceTracking.exe", StringComparison.OrdinalIgnoreCase) ? 3
                : change.RelativePath.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ? 2
                : change.RelativePath.Equals("QproRuntime/release-manifest.json", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string source, string relative)
        {
            string destination = Within(destinationPackage, relative);
            ValidateFileTarget(destinationPackage, destination);
            string supplied = Hash(source);
            string? original = File.Exists(destination) ? Hash(destination) : null;
            if (supplied == original) return;
            changes.Add(new(relative, source, destination, supplied, original,
                original is null ? default : File.GetLastWriteTimeUtc(destination),
                original is null ? FileAttributes.Normal : File.GetAttributes(destination)));
        }
    }

    private static bool Preserve(string relative)
    {
        if (!relative.StartsWith("QproRuntime/", StringComparison.OrdinalIgnoreCase)) return false;
        string runtime = relative["QproRuntime/".Length..];
        string first = runtime.Split('/')[0];
        return new[] { "config", "captures", "training", "logs" }.Contains(first, StringComparer.OrdinalIgnoreCase) ||
            first.Equals(".venv", StringComparison.OrdinalIgnoreCase) || first.StartsWith(".venv-", StringComparison.OrdinalIgnoreCase) ||
            runtime.StartsWith("research/seacliff_eye_model/", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(runtime, @"^questpro-(?:live|relay-client)-[^/]*\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void CleanupTemporaryFiles(IEnumerable<string> files, List<string> warnings)
    {
        foreach (string file in files)
            try
            {
                EnsureNoReparse(file);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception error) { warnings.Add("Temporary update file was kept: " + file + ": " + error.Message); }
    }

    private static bool CleanupBackup(string backupDirectory, List<string> warnings)
    {
        try
        {
            if (!Directory.Exists(backupDirectory)) return true;
            foreach (string file in EnumerateSafeFiles(backupDirectory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            foreach (string directory in Directory.GetDirectories(backupDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                EnsureNoReparse(directory);
                Directory.Delete(directory, recursive: false);
            }
            EnsureNoReparse(backupDirectory);
            Directory.Delete(backupDirectory, recursive: false);
            return true;
        }
        catch (Exception error)
        {
            warnings.Add("Update backup folder was kept: " + backupDirectory + ": " + error.Message);
            return false;
        }
    }

    private static void CreateParents(string package, string parent, List<string> createdDirectories)
    {
        var missing = new Stack<string>();
        for (string? current = parent; current is not null && !current.Equals(package, StringComparison.OrdinalIgnoreCase); current = Directory.GetParent(current)?.FullName)
        {
            EnsureNoReparse(current);
            if (Directory.Exists(current)) break;
            if (File.Exists(current)) throw new IOException("An app folder path is occupied by a file: " + current);
            missing.Push(current);
        }
        while (missing.Count > 0)
        {
            string directory = missing.Pop();
            EnsureNoReparse(directory);
            Directory.CreateDirectory(directory);
            EnsureNoReparse(directory);
            createdDirectories.Add(directory);
        }
    }

    private static void ValidateDestination(string package, string runtime, Version incoming)
    {
        foreach (string required in new[] { "QproFaceTracking.exe", "SHA256SUMS.txt", "QproRuntime/release-manifest.json", "QproRuntime/build-and-run.ps1", "QproRuntime/setup-runtime.ps1" })
        {
            string path = Within(package, required);
            ValidateFileTarget(package, path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("This folder is not a complete ready-to-run Qpro release: " + required);
        }
        string manifestPath = Path.Combine(runtime, "release-manifest.json");
        if (new FileInfo(manifestPath).Length > 65_536) throw new InvalidDataException("The installed app manifest is invalid.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement identity = manifest.RootElement;
        if (identity.ValueKind != JsonValueKind.Object || !identity.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || name.GetString() != "QproFaceTracking" ||
            !identity.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            !HubUpdateReleaseParser.TryStableVersion(version.GetString() ?? "", out Version? installed))
            throw new InvalidDataException("This folder is not an installed Qpro release.");
        if (incoming <= installed)
            throw new InvalidDataException("This update is no longer newer than the installed version. Check for updates again.");
    }

    private static void ValidateFileTarget(string package, string path)
    {
        if (!Contained(package, path)) throw new InvalidDataException("An update file leaves the installed app folder.");
        EnsureNoReparse(path);
        if (Directory.Exists(path)) throw new IOException("An app file path is occupied by a folder: " + path);
        for (string? parent = Path.GetDirectoryName(path); parent is not null && Contained(package, parent); parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new IOException("An app folder path is occupied by a file: " + parent);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            EnsureNoReparse(directory);
            foreach (string item in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNoReparse(item);
                if (Directory.Exists(item)) pending.Push(item);
                else yield return item;
            }
        }
    }

    private static void RequireRuntimeShape(string runtime)
    {
        EnsureNoReparse(runtime);
        if (!Path.GetFileName(runtime).Equals("QproRuntime", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(runtime))
            throw new InvalidDataException("Automatic updates require a ready-to-run release with a QproRuntime folder.");
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Contained(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string left, string right) => Contained(left, right) || Contained(right, left);
    private static string Within(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || !Contained(root, path))
            throw new InvalidDataException("An update path leaves its allowed folder.");
        return path;
    }

    private static void EnsureNoReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Directory.GetParent(current)?.FullName)
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The update path contains a linked folder or file: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
