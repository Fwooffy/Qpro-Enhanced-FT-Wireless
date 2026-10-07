using QproFaceTracking.Hub;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

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

try
{
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
