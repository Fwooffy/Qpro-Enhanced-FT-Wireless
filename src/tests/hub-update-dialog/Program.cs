using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class UpdateDialogTests
{
    private static int _passed;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Request(bool IncludePrereleases, CancellationToken Token, TaskCompletionSource<HubUpdateCheck> Result);

    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetColorMode(SystemColorMode.Dark);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Control.CheckForIllegalCrossThreadCalls = true;
        // A real message loop supplies the same synchronization context as the
        // app's modal update dialog. DoEvents alone creates a temporary loop.
        using var loop = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
        loop.Shown += (_, _) =>
        {
            try { Run(args.FirstOrDefault()); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { loop.Close(); }
        };
        Application.Run(loop);
    }

    static void Run(string? output)
    {
        var current = new Version(3, 0, 0);
        HubUpdateCheck stable = Available("3.0.1", prerelease: false);
        HubUpdateCheck beta = Available("3.0.2", prerelease: true);
        var preferenceType = typeof(HubForm).GetNestedType("UpdatePreference", BindingFlags.NonPublic)!;
        var old = JsonSerializer.Deserialize("{\"CheckAutomatically\":false}", preferenceType)!;
        Check(!(bool)preferenceType.GetProperty("CheckAutomatically")!.GetValue(old)!, "Old automatic preference changed.");
        Check(!(bool)preferenceType.GetProperty("IncludePrereleases")!.GetValue(old)!, "Old preferences unexpectedly enable prereleases.");
        var saved = Activator.CreateInstance(preferenceType, true, true)!;
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(saved, preferenceType), preferenceType)!;
        Check((bool)preferenceType.GetProperty("IncludePrereleases")!.GetValue(restored)!, "Saved prerelease preference was lost.");
        CheckInstallPaths();

        var requests = new List<Request>();
        Task<HubUpdateCheck> Recheck(bool include, CancellationToken token)
        {
            var request = new Request(include, token, new(TaskCreationOptions.RunContinuationsAsynchronously));
            requests.Add(request);
            return request.Result.Task;
        }
        var stage = new TaskCompletionSource<HubStagedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken stageToken = default;
        int stageCalls = 0;
        using (var dialog = new HubUpdateDialog(stable, current, true, null, (_, _, token) =>
            { stageCalls++; stageToken = token; return stage.Task; }, checkUpdates: Recheck))
        {
            dialog.Show(); Pump();
            var prereleases = Field<CheckBox>(dialog, "_prereleases");
            var install = Field<DarkButton>(dialog, "_install");
            var retry = Field<DarkButton>(dialog, "_checkAgain");
            Check(!dialog.IncludePrereleases && install.Enabled, "Default stable channel did not remain selected.");
            Screenshot(dialog, output, "updates-stable.png");
            prereleases.Checked = true; Pump();
            Check(requests.Count == 1 && requests[0].IncludePrereleases, "The prerelease toggle did not recheck in the open dialog.");
            Check(!install.Enabled && stageCalls == 0, "Changing channels started a download or left Update enabled.");
            requests[0].Result.SetResult(beta); Pump();
            Check(dialog.Release?.IsPrerelease == true && install.Enabled, "The prerelease result did not become available: " +
                dialog.Check?.Message + "; install=" + install.Enabled + "; status=" + Field<Label>(dialog, "_status").Text +
                "; request=" + (Field<CancellationTokenSource?>(dialog, "_checkRequest") is not null));
            Check(Field<Label>(dialog, "_title").Text.Contains("Prerelease") && Field<Label>(dialog, "_status").ForeColor == HubForm.Warning,
                "The test build did not have a readable warning.");
            Screenshot(dialog, output, "updates-prerelease.png");
            dialog.Size = dialog.MinimumSize; Pump();
            CheckLayout(dialog);
            Screenshot(dialog, output, "updates-prerelease-compact.png");
            dialog.Size = new Size(760, 670); Pump();

            prereleases.Checked = false; Pump();
            Request oldRequest = requests[^1];
            prereleases.Checked = true; Pump();
            Request newRequest = requests[^1];
            Check(oldRequest.Token.IsCancellationRequested, "The superseded channel check was not canceled.");
            newRequest.Result.SetResult(beta); Pump();
            oldRequest.Result.SetResult(stable); Pump();
            Check(dialog.Release?.Version == beta.Release!.Version && dialog.Release.IsPrerelease,
                "An old stable check replaced a newer prerelease result.");
            retry.PerformClick(); Pump();
            requests[^1].Result.SetResult(new(HubUpdateCheckState.Unavailable, "Sample result: GitHub could not be reached. Try Check again.")); Pump();
            Check(!install.Enabled && retry.Enabled && Field<Label>(dialog, "_status").ForeColor == HubForm.Bad,
                "An unavailable check did not expose a safe retry and red error.");
            retry.PerformClick(); Pump();
            var manual = beta with { State = HubUpdateCheckState.ManualDownloadOnly, Message = "Sample result: download this release manually.",
                Release = beta.Release! with { Tag = "v3.0.2-beta.1", PrereleaseSuffix = "beta.1", AssetName = null, DownloadUrl = null, Sha256 = null } };
            requests[^1].Result.SetResult(manual); Pump();
            Check(!install.Enabled && Field<Label>(dialog, "_status").ForeColor == HubForm.Warning,
                "A release without a checksum could be installed.");
            Check(Field<Label>(dialog, "_title").Text.Contains("3.0.2-beta.1") && Field<DarkButton>(dialog, "_github").Enabled,
                "A suffix-tag prerelease lost its version label or manual GitHub action.");
            retry.PerformClick(); Pump(); requests[^1].Result.SetResult(beta); Pump();
            install.PerformClick(); Pump();
            Check(stageCalls == 1 && !prereleases.Enabled && !retry.Enabled && !Field<CheckBox>(dialog, "_automatic").Enabled,
                "Channel preferences stayed editable during a download.");
            Field<DarkButton>(dialog, "_close").PerformClick(); Pump();
            Check(stageToken.IsCancellationRequested, "Close did not cancel a staged download.");
            stage.SetCanceled(stageToken); Pump();
            Check(dialog.IsDisposed && dialog.StagedUpdate is null, "Cancel did not close cleanly without an update.");
        }

        using (var failure = new HubUpdateDialog(beta, current, true, null,
            (_, _, _) => Task.FromException<HubStagedUpdate>(new IOException("Sample download failure")),
            includePrereleases: true, checkUpdates: Recheck))
        {
            failure.Show(); Pump(); Field<DarkButton>(failure, "_install").PerformClick(); Pump();
            Check(Field<Label>(failure, "_status").ForeColor == HubForm.Bad && Field<CheckBox>(failure, "_prereleases").Enabled,
                "A failed download did not restore channel controls.");
            failure.Close();
        }

        using (var ready = new HubUpdateDialog(beta, current, true, null,
            (_, _, _) => Task.FromResult(new HubStagedUpdate("sample.exe", "sample-runtime", "sample-old", 0, [])),
            includePrereleases: true, checkUpdates: Recheck))
        {
            ready.Show(); Pump(); Field<DarkButton>(ready, "_install").PerformClick(); Pump();
            Check(ready.StagedUpdate is not null && !Field<CheckBox>(ready, "_prereleases").Enabled && ready.DialogResult != DialogResult.OK,
                "Staging changed channels or applied the update without a second click.");
            Field<DarkButton>(ready, "_install").PerformClick(); Pump();
            Check(ready.DialogResult == DialogResult.OK, "Restart and update did not require the explicit second click.");
        }

        var attempts = new List<(IProgress<HubUpdateProgress> Progress, TaskCompletionSource<HubStagedUpdate> Result)>();
        using (var progressRetry = new HubUpdateDialog(stable, current, true, null, (_, progress, _) =>
        {
            var result = new TaskCompletionSource<HubStagedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            attempts.Add((progress, result)); return result.Task;
        }, checkUpdates: Recheck))
        {
            progressRetry.Show(); Pump(); Field<DarkButton>(progressRetry, "_install").PerformClick(); Pump();
            attempts[0].Result.SetException(new IOException("Sample first-attempt failure")); Pump();
            Field<DarkButton>(progressRetry, "_install").PerformClick(); Pump();
            Check(attempts.Count == 2, "A failed download could not be retried.");
            attempts[1].Progress.Report(new("Current attempt", 20, 100)); Pump();
            attempts[0].Progress.Report(new("Late obsolete progress", 80, 100)); Pump();
            Check(Field<Label>(progressRetry, "_status").Text.StartsWith("Current attempt") &&
                Field<ProgressBar>(progressRetry, "_progress").Value == 200, "An obsolete download callback changed the retry's progress.");
            attempts[1].Result.SetException(new IOException("Sample retry failure")); Pump();
            progressRetry.Close();
        }

        foreach (bool directDispose in new[] { false, true })
        {
            using var pending = new HubUpdateDialog(stable, current, true, null,
                (_, _, _) => throw new Exception("Unexpected download"), checkUpdates: Recheck);
            pending.Show(); Pump(); Field<CheckBox>(pending, "_prereleases").Checked = true; Pump();
            Request request = requests[^1];
            if (directDispose) pending.Dispose(); else pending.Close();
            Check(request.Token.IsCancellationRequested, "Closing/disposal left a channel check running.");
            request.Result.SetResult(beta); Pump();
            Check(pending.IsDisposed && pending.Check is null, "A late channel result updated a disposed dialog.");
        }

        using (var blocked = new HubUpdateDialog(beta, current, false, "Sample: stop tracking before updating.",
            (_, _, _) => throw new Exception("Blocked dialog downloaded an update"), includePrereleases: true, checkUpdates: Recheck))
        {
            blocked.Show(); Pump();
            Check(!Field<DarkButton>(blocked, "_install").Enabled, "A blocked app could install an update.");
            Field<DarkButton>(blocked, "_checkAgain").PerformClick(); Pump(); requests[^1].Result.SetResult(beta); Pump();
            Check(!Field<DarkButton>(blocked, "_install").Enabled, "A channel refresh cleared the installation block.");
            blocked.Close();
        }
        Console.WriteLine($"PASS: {_passed} update-dialog checks. Mocked metadata/downloads only; no network, app install, user preferences, or headset changes.");
    }

    static HubUpdateCheck Available(string version, bool prerelease)
    {
        var number = Version.Parse(version);
        var release = new HubUpdateRelease(number, "v" + version, "Sample release", new Uri("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/v" + version),
            "PREVIEW · SAMPLE DATA\n\nSample release notes for layout verification.\n\n• Improve app setup diagnostics.\n• Keep models and saved options when updating.\n\nThis fixture does not download or install an update.",
            "QproFaceTracking.V" + version + ".zip", new Uri("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/download/v" + version + "/QproFaceTracking.V" + version + ".zip"),
            1024, new string('a', 64), prerelease);
        return new(HubUpdateCheckState.Available, $"Sample result: QproFaceTracking V{version}" + (prerelease ? " prerelease is available." : " is available."), release);
    }
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); _passed++; }
    static void Pump()
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 150) { Application.DoEvents(); Thread.Sleep(5); }
    }
    static void Screenshot(Form dialog, string? output, string name)
    {
        if (output is null) return;
        Directory.CreateDirectory(output);
        using var image = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size));
        image.Save(Path.Combine(output, name));
    }
    static void CheckLayout(HubUpdateDialog dialog)
    {
        var notes = Field<RichTextBox>(dialog, "_notes");
        Check(notes.Height >= 60, "The compact dialog leaves no readable release notes.");
        foreach (string field in new[] { "_prereleases", "_automatic", "_install", "_close", "_github", "_components", "_checkAgain" })
        {
            Control control = Field<Control>(dialog, field);
            Rectangle bounds = dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            Check(dialog.ClientRectangle.Contains(bounds), $"The compact dialog clips {field}.");
        }
    }

    static void CheckInstallPaths()
    {
        var block = typeof(HubForm).GetMethod("HubUpdateBlockReason", BindingFlags.Static | BindingFlags.NonPublic)!;
        string root = Path.Combine(AppContext.BaseDirectory, "qpro-update-dialog-path-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "regular package");
        string runtime = Path.Combine(target, "QproRuntime");
        string link = Path.Combine(root, "linked package");
        Directory.CreateDirectory(runtime);
        try
        {
            Check(block.Invoke(null, [false, true, runtime]) is null, "A regular packaged folder was unnecessarily blocked.");
            var command = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) command.ArgumentList.Add(argument);
            using var process = Process.Start(command) ?? throw new Exception("Could not start the junction fixture.");
            string result = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new Exception("Could not create the junction fixture: " + result);
            string? reason = (string?)block.Invoke(null, [false, true, Path.Combine(link, "QproRuntime")]);
            Check(reason?.Contains("Download the release ZIP") == true && reason.Contains("regular local folder"),
                "A linked app folder did not receive manual update guidance before downloading.");
            Check(((string?)block.Invoke(null, [true, true, runtime]))?.StartsWith("Stop tracking") == true,
                "The app update block lost the tracking stop instruction.");
            Check(((string?)block.Invoke(null, [false, false, runtime]))?.Contains("source or preview") == true,
                "Source builds lost their manual update instruction.");
        }
        finally
        {
            // Remove this known junction itself, then its known empty target.
            if (Directory.Exists(link)) Directory.Delete(link);
            Check(Directory.Exists(runtime), "Junction cleanup changed its target.");
            Directory.Delete(runtime); Directory.Delete(target); Directory.Delete(root);
        }
    }
}
