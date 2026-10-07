using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace QproFaceTracking.Hub;

// The current, trusted single-file Hub is copied outside its install folder.
// It waits for the parent to finish headset/ADB cleanup before replacing files.
internal static class HubUpdateRunner
{
    internal static IDisposable? AcquireInstallUseLock(string runtimeRoot)
    {
        if (!IsPackagedInstall(runtimeRoot)) return null;
        var mutex = new Mutex(false, HubUpdateApply.InstallMutexName(Directory.GetParent(Path.GetFullPath(runtimeRoot))!.FullName));
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new IOException("This copy of the Hub is already open, or its update is being applied. Use the open Hub, or wait for the update to finish.");
        }
        if (HubUpdateApply.RequiresRecovery(runtimeRoot))
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw new IOException("An interrupted update requires recovery. Reopen this Hub to run its recovery helper.");
        }
        if (HubUpdateApply.HasPendingTransaction(runtimeRoot))
        {
            // A committed transaction only needs backup cleanup. A locked backup
            // must not prevent opening an otherwise complete installed version.
            try { HubUpdateApply.RecoverPending(runtimeRoot); }
            catch (IOException) { /* Keep the completed journal and backup for a later retry. */ }
        }
        return new InstallUseLock(mutex);
    }

    private sealed class InstallUseLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    internal static void CleanupCompletedHelpers(string storage)
    {
        if (!Directory.Exists(storage)) return;
        HubUpdatePackage.PrepareStorage(storage);
        foreach (string directory in Directory.EnumerateDirectories(storage, ".qpro-apply-*", SearchOption.TopDirectoryOnly).Take(32))
        {
            string suffix = Path.GetFileName(directory)[".qpro-apply-".Length..];
            if (suffix.Length != 32 || !suffix.All(char.IsAsciiHexDigit) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateDirectories(directory).Any()) continue;
            string marker = Path.Combine(directory, "update-complete.txt");
            if (!File.Exists(marker) || File.GetLastWriteTimeUtc(marker) > DateTime.UtcNow.AddDays(-1)) continue;
            string[] files = Directory.GetFiles(directory);
            if (files.Any(file => !new[] { "QproUpdate.exe", "update-plan.json", "update-complete.txt", "update-result.txt" }
                    .Contains(Path.GetFileName(file), StringComparer.Ordinal) ||
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)) continue;
            // Failed updates and retained transaction backups are never pruned.
            // An executable still mapped by Windows cannot be opened for writing.
            string executable = Path.Combine(directory, "QproUpdate.exe");
            try
            {
                using (File.Open(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                foreach (string file in files) File.Delete(file);
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool IsPackagedInstall(string runtimeRoot)
    {
        string runtime = Path.GetFullPath(runtimeRoot);
        string? install = Directory.GetParent(runtime)?.FullName;
        return Path.GetFileName(runtime).Equals("QproRuntime", StringComparison.OrdinalIgnoreCase) && install is not null &&
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(install, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Environment.ProcessPath, Path.Combine(install, "QproFaceTracking.exe"), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(runtime, "release-manifest.json"));
    }

    internal static void StartHelper(HubStagedUpdate update, string destinationRuntime, Version version)
    {
        if (!IsPackagedInstall(destinationRuntime)) throw new InvalidOperationException("Only a packaged release can update itself.");
        HubUpdatePackage.ValidateStagedRelease(update.RuntimeRoot, version);
        string checksum = Path.Combine(Directory.GetParent(update.RuntimeRoot)!.FullName, "SHA256SUMS.txt");
        string storage = Directory.GetParent(Directory.GetParent(update.RuntimeRoot)!.FullName)!.Parent!.FullName;
        string helperDirectory = Path.Combine(HubUpdatePackage.PrepareStorage(storage), ".qpro-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        string helper = Path.Combine(helperDirectory, "QproUpdate.exe");
        File.Copy(Environment.ProcessPath!, helper);
        using var parent = Process.GetCurrentProcess();
        var plan = new HubUpdatePlan(1, update.RuntimeRoot, Path.GetFullPath(destinationRuntime), version.ToString(3),
            parent.Id, parent.StartTime.ToUniversalTime().Ticks, HashFile(checksum));
        string planPath = Path.Combine(helperDirectory, "update-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = helperDirectory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        using var process = Process.Start(start) ?? throw new IOException("The update helper could not start.");
    }

    internal static bool StartRecoveryIfNeeded(string runtimeRoot)
    {
        if (!IsPackagedInstall(runtimeRoot) || !HubUpdateApply.RequiresRecovery(runtimeRoot)) return false;
        string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "updates");
        string helperDirectory = Path.Combine(HubUpdatePackage.PrepareStorage(storage), ".qpro-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        string helper = Path.Combine(helperDirectory, "QproUpdate.exe");
        File.Copy(Environment.ProcessPath!, helper);
        using var parent = Process.GetCurrentProcess();
        var plan = new HubUpdatePlan(1, "", Path.GetFullPath(runtimeRoot), "0.0.0", parent.Id,
            parent.StartTime.ToUniversalTime().Ticks, "", RecoverOnly: true);
        string planPath = Path.Combine(helperDirectory, "update-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = helperDirectory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        using var process = Process.Start(start) ?? throw new IOException("The update recovery helper could not start.");
        return true;
    }

    internal static int Run(string planPath)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        try
        {
            string path = Path.GetFullPath(planPath);
            if (!string.Equals(Path.GetDirectoryName(path), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path) != "update-plan.json" || new FileInfo(path).Length > 16_384)
                throw new InvalidDataException("The update helper plan is invalid.");
            var plan = JsonSerializer.Deserialize<HubUpdatePlan>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The update helper plan is empty.");
            if (plan.Schema != 1 || !HubUpdateReleaseParser.TryStableVersion(plan.Version, out var version) || plan.ParentId <= 0 ||
                plan.ParentStartedUtcTicks <= 0 || !plan.RecoverOnly && plan.ChecksumListSha256.Length != 64)
                throw new InvalidDataException("The update helper plan is unsupported.");
            using var form = new HubUpdateApplyWindow(plan, version!);
            Application.Run(form);
            return form.Succeeded ? 0 : 1;
        }
        catch (Exception error)
        {
            MessageBox.Show("The update could not start. The installed app has not changed.\n\n" + error.Message,
                "Qpro update needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static void WaitForParent(HubUpdatePlan plan)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentId);
            // A reused PID belongs to another app and must not be waited on or terminated.
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks) return;
            if (!parent.WaitForExit(120_000))
                throw new IOException("The Hub is still open. Finish tracking cleanup, then try Update again. No app files were changed.");
        }
        catch (ArgumentException) { /* The Hub has already exited. */ }
    }
}

internal sealed record HubUpdatePlan(int Schema, string StagedRuntime, string DestinationRuntime, string Version,
    int ParentId, long ParentStartedUtcTicks, string ChecksumListSha256, bool RecoverOnly = false);

internal sealed class HubUpdateApplyWindow : Form
{
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(24), ForeColor = Color.WhiteSmoke, Text = "Waiting for the Hub to finish closing…" };
    private bool _applying = true;
    internal bool Succeeded { get; private set; }

    internal HubUpdateApplyWindow(HubUpdatePlan plan, Version version)
    {
        Text = "Updating QproFaceTracking"; Size = new Size(640, 220); MinimumSize = new Size(540, 200);
        StartPosition = FormStartPosition.CenterScreen; BackColor = HubForm.Background;
        Font = new Font("Segoe UI", 10F); MinimizeBox = false; MaximizeBox = false;
        Controls.Add(_status);
        FormClosing += (_, e) => { if (_applying) e.Cancel = true; };
        Shown += async (_, _) =>
        {
            try
            {
                IProgress<HubUpdateProgress> progress = new Progress<HubUpdateProgress>(value => { if (!IsDisposed) _status.Text = value.Phase + "…"; });
                var result = await Task.Run(() =>
                {
                    HubUpdateRunner.WaitForParent(plan);
                    if (plan.RecoverOnly)
                    {
                        progress.Report(new("Recovering the interrupted update"));
                        HubUpdateApply.RecoverPending(plan.DestinationRuntime);
                        return new HubUpdateApplyResult(0, Array.Empty<string>());
                    }
                    string sums = Path.Combine(Directory.GetParent(plan.StagedRuntime)!.FullName, "SHA256SUMS.txt");
                    if (!HubUpdateRunner.HashFile(sums).Equals(plan.ChecksumListSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("The prepared update changed after verification. Download it again; the installed app is unchanged.");
                    return HubUpdateApply.Apply(plan.StagedRuntime, plan.DestinationRuntime, version, progress);
                });
                Succeeded = true;
                try
                {
                    if (!plan.RecoverOnly)
                    {
                        var staged = new HubStagedUpdate(Path.Combine(Directory.GetParent(plan.StagedRuntime)!.FullName, "QproFaceTracking.exe"),
                            plan.StagedRuntime, plan.DestinationRuntime, 0, Array.Empty<string>());
                        string storage = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
                        if (result.RetainedBackupDirectory is null)
                            await Task.Run(() => HubUpdatePackage.Discard(staged, storage));
                    }
                }
                catch (Exception error) { _status.Text = "Update applied; temporary files could not be removed: " + error.Message; }
                if (result.Warnings.Count > 0)
                    MessageBox.Show(this, string.Join("\n", result.Warnings), "Update complete — existing files kept", MessageBoxButtons.OK, MessageBoxIcon.Information);
                var install = Directory.GetParent(plan.DestinationRuntime)!.FullName;
                var start = new ProcessStartInfo(Path.Combine(install, "QproFaceTracking.exe")) { UseShellExecute = false, WorkingDirectory = install };
                using var reopened = Process.Start(start) ?? throw new IOException("The updated Hub could not reopen. Open QproFaceTracking.exe in your existing folder.");
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "update-complete.txt"), "Update applied and the Hub reopened."); }
                catch (IOException) { /* The installed update is already complete. */ }
            }
            catch (Exception error)
            {
                string details = error.ToString();
                string log = Path.Combine(AppContext.BaseDirectory, "update-result.txt");
                try { File.WriteAllText(log, details); } catch (IOException) { }
                string recovery = plan.RecoverOnly ? "Recovery could not finish. Keep the journal and backup folder identified below; the Hub will remain closed."
                    : error is HubUpdateApplyException apply && !apply.RollbackComplete
                    ? "Some files could not be restored. Keep the recovery folder and send update-result.txt for help."
                    : Succeeded ? "The update was applied. Open the Hub from your existing folder."
                    : "The update did not complete. Your original files were kept or restored.";
                MessageBox.Show(this, recovery + "\n\n" + error.Message + "\n\nDetails: " + log,
                    "Qpro update needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _applying = false; Close(); }
        };
    }
}
