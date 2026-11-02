using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QproFaceTracking.Hub;

// This entry point belongs only to the test project. Production Program.Main
// and HubForm are never called: no device, runtime or global settings are used.
internal static class LocalUpdaterProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        Fixture? fixture = null;
        try
        {
            fixture = Fixture.Load();
            if (args.Length == 2 && args[0] == "--apply-update")
            {
                fixture.CheckHelperPlan(args[1]);
                fixture.Trace("helper-started");
                return HubUpdateRunner.Run(args[1]);
            }
            if (args.Any(arg => arg != "--automatic")) throw new ArgumentException("Unknown local test argument.");
            bool automatic = args.Contains("--automatic");
            if (automatic) Environment.SetEnvironmentVariable("QPRO_LOCAL_UPDATER_AUTOMATIC", "1");
            fixture.CheckInstalledExecutable();
            using var installLock = HubUpdateRunner.AcquireInstallUseLock(fixture.Runtime);
            Version version = CurrentVersion();
            if (version == Version.Parse(fixture.Identity.TargetVersion))
                return fixture.VerifyReplacement(version);
            if (version != Version.Parse(fixture.Identity.SourceVersion))
                throw new InvalidDataException("The test executable version does not match the fixture.");

            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.Dark);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var form = new LocalUpdateWindow(fixture, version, automatic);
            Application.Run(form);
            return form.HelperStarted ? 0 : 2;
        }
        catch (Exception error)
        {
            // No fixture context means no UI and no writes anywhere.
            if (fixture is not null) fixture.WriteResult("failure.json", new { passed = false, error = error.ToString() });
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static Version CurrentVersion()
    {
        Version value = typeof(LocalUpdaterProgram).Assembly.GetName().Version!;
        return new(value.Major, value.Minor, value.Build);
    }
}

internal sealed record FixtureIdentity(int Schema, string Nonce, string SourceVersion, string TargetVersion,
    string ArchiveSha256, long ArchiveSize);

internal sealed class Fixture(string root, FixtureIdentity identity)
{
    internal string Root { get; } = root;
    internal FixtureIdentity Identity { get; } = identity;
    internal string App => Path.Combine(Root, "app");
    internal string Runtime => Path.Combine(App, "QproRuntime");
    internal string Storage => Path.Combine(Root, "storage");
    internal string ArchiveName => "QproCompanion.V" + Identity.TargetVersion + ".zip";
    internal string Archive => Path.Combine(Root, "assets", ArchiveName);
    internal Uri AssetUrl => new("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/download/v" +
        Identity.TargetVersion + "/" + ArchiveName);

    internal static Fixture Load()
    {
        string root = Environment.GetEnvironmentVariable("QPRO_LOCAL_UPDATER_ROOT") ??
            throw new InvalidOperationException("Start this test with test_local_updater.ps1.");
        string nonce = Environment.GetEnvironmentVariable("QPRO_LOCAL_UPDATER_NONCE") ?? "";
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!Regex.IsMatch(Path.GetFileName(root), "^local-updater-[0-9a-f]{32}$") ||
            !Regex.IsMatch(nonce, "^[0-9a-f]{32}$") || !Directory.Exists(root))
            throw new InvalidDataException("The disposable test folder is invalid.");
        HubUpdatePackage.PrepareStorage(root); // Refuse linked ancestors too.
        string marker = Path.Combine(root, "local-updater.json");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096 ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The local test marker is missing or invalid.");
        var identity = JsonSerializer.Deserialize<FixtureIdentity>(File.ReadAllText(marker),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (identity is null || identity.Schema != 1 || identity.Nonce != nonce ||
            identity.SourceVersion != "2.9.99" || identity.TargetVersion != "3.0.0" ||
            identity.ArchiveSize <= 0 || !Regex.IsMatch(identity.ArchiveSha256, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("The local test identity does not match its launcher.");
        return new(root, identity);
    }

    internal string Within(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A test path leaves its disposable folder.");
        return full;
    }

    internal void CheckInstalledExecutable()
    {
        if (!string.Equals(Environment.ProcessPath, Path.Combine(App, "QproFaceTracking.exe"), StringComparison.OrdinalIgnoreCase) ||
            !HubUpdateRunner.IsPackagedInstall(Runtime))
            throw new InvalidDataException("This test must run from its own app folder.");
    }

    internal void CheckHelperPlan(string path)
    {
        string planPath = Within(path);
        string helperFolder = Path.GetDirectoryName(planPath)!;
        if (!helperFolder.StartsWith(Storage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(helperFolder), "^\\.qpro-apply-[0-9a-f]{32}$") ||
            Path.GetFileName(planPath) != "update-plan.json" || new FileInfo(planPath).Length > 16_384 ||
            !string.Equals(Environment.ProcessPath, Path.Combine(helperFolder, "QproUpdate.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The copied helper is outside this test.");
        HubUpdatePackage.PrepareStorage(helperFolder);
        var plan = JsonSerializer.Deserialize<HubUpdatePlan>(File.ReadAllText(planPath)) ??
            throw new InvalidDataException("The test helper plan is empty.");
        if (plan.RecoverOnly || plan.Version != Identity.TargetVersion ||
            !string.Equals(Path.GetFullPath(plan.DestinationRuntime), Runtime, StringComparison.OrdinalIgnoreCase) ||
            !Within(plan.StagedRuntime).StartsWith(Storage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The helper cannot change anything outside this fixture.");
    }

    internal HubUpdateClient Client() => new(new LocalReleaseHandler(this));

    internal void Trace(string phase) => File.AppendAllText(Path.Combine(Root, "events.jsonl"),
        JsonSerializer.Serialize(new { phase, pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow }) + "\n");

    internal void WriteResult(string name, object result)
    {
        string destination = Within(Path.Combine(Root, name));
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination, overwrite: true);
    }

    internal int VerifyReplacement(Version version)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runtime, "release-manifest.json")));
        if (manifest.RootElement.GetProperty("version").GetString() != Identity.TargetVersion ||
            Hash(Archive) != Identity.ArchiveSha256)
            throw new InvalidDataException("The replacement or local archive changed.");
        using var archive = ZipFile.OpenRead(Archive);
        using var reader = new StreamReader(archive.GetEntry("QproFaceTracking.V" + Identity.TargetVersion + "/SHA256SUMS.txt")!.Open());
        string[] sums = reader.ReadToEnd().Split('\n');
        string expectedExe = sums.Single(line => line.TrimEnd('\r').EndsWith("  QproFaceTracking.exe", StringComparison.Ordinal))[..64];
        string actualExe = Hash(Environment.ProcessPath!);
        if (actualExe != expectedExe) throw new InvalidDataException("The restarted executable has the wrong checksum.");
        using var preservation = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "preserve.json")));
        int preserved = 0;
        foreach (var file in preservation.RootElement.GetProperty("files").EnumerateObject())
        {
            if (Path.IsPathRooted(file.Name) || file.Name.Split('/', '\\').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("A preservation fixture path is invalid.");
            string path = Within(Path.Combine(App, file.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path) || Hash(path) != file.Value.GetString())
                throw new InvalidDataException("A sample model or setting was not preserved: " + file.Name);
            preserved++;
        }
        if (preserved < 4 || HubUpdateApply.HasPendingTransaction(Runtime) || HubUpdateApply.RequiresRecovery(Runtime))
            throw new InvalidDataException("The test did not finish its preservation or transaction checks.");
        Trace("replacement-verified");
        WriteResult("verification.json", new { passed = true, version = version.ToString(3),
            preservedFiles = preserved, executableSha256 = actualExe, trackingStarted = false,
            transport = "local files; no network", scope = "updater dialogs, staging, real helper, replacement and restart" });
        return 0;
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}

internal sealed class LocalReleaseHandler(Fixture fixture) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get) throw new HttpRequestException("Local test refuses non-GET requests.");
        HttpContent content;
        if (request.RequestUri == HubUpdateReleaseParser.LatestApi || request.RequestUri == HubUpdateReleaseParser.ReleasesApi)
        {
            object release = new { draft = false, prerelease = false, tag_name = "v" + fixture.Identity.TargetVersion,
                name = "Qpro Companion local updater test", body = "LOCAL TEST ONLY. This package contains inert sample tools and models.\n\n" +
                    "Update reads the local ZIP; Restart and update replaces only this disposable test copy. Nothing is published or installed on the headset.",
                assets = new[] { new { name = fixture.ArchiveName, state = "uploaded", size = fixture.Identity.ArchiveSize,
                    digest = "sha256:" + fixture.Identity.ArchiveSha256, browser_download_url = fixture.AssetUrl.AbsoluteUri } } };
            string json = request.RequestUri == HubUpdateReleaseParser.ReleasesApi
                ? JsonSerializer.Serialize(new[] { release }) : JsonSerializer.Serialize(release);
            content = new StringContent(json, Encoding.UTF8, "application/json");
            fixture.Trace("metadata-read-from-local-fixture");
        }
        else if (request.RequestUri == fixture.AssetUrl)
        {
            content = new StreamContent(new FileStream(fixture.Archive, FileMode.Open, FileAccess.Read, FileShare.Read,
                131_072, useAsync: true));
            content.Headers.ContentLength = new FileInfo(fixture.Archive).Length;
            fixture.Trace("zip-read-from-local-disk");
        }
        else throw new HttpRequestException("No network is allowed by the local updater test: " + request.RequestUri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
    }
}

internal sealed class LocalUpdateWindow : Form
{
    private readonly Fixture _fixture;
    private readonly Version _current;
    private readonly bool _automatic;
    private readonly Button _check = new() { Text = "Check local update", AutoSize = true, Padding = new Padding(12, 8, 12, 8) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(630, 0), Margin = new Padding(0, 0, 0, 18) };
    internal bool HelperStarted { get; private set; }

    internal LocalUpdateWindow(Fixture fixture, Version current, bool automatic)
    {
        _fixture = fixture; _current = current; _automatic = automatic;
        Text = "Qpro Companion — local updater test";
        Size = new Size(720, 330); StartPosition = FormStartPosition.CenterScreen;
        BackColor = HubForm.Background; ForeColor = Color.WhiteSmoke; Font = new Font("Segoe UI", 10F);
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), FlowDirection = FlowDirection.TopDown };
        content.Controls.Add(new Label { Text = "Local updater test", AutoSize = true, Font = new Font(Font.FontFamily, 18F, FontStyle.Bold) });
        _status.Text = $"Test copy: V{current} → V{fixture.Identity.TargetVersion}\n\n" +
            "Uses a ZIP on this PC and sample models/settings. The normal app, shared runtime and headset are not used.";
        content.Controls.Add(_status); content.Controls.Add(_check); Controls.Add(content);
        _check.Click += async (_, _) => await CheckAsync();
        Shown += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (!_check.Enabled) return;
        _check.Enabled = false;
        try
        {
            using var client = _fixture.Client();
            var check = await client.CheckAsync(_current, CancellationToken.None);
            if (check.State != HubUpdateCheckState.Available || check.Release is not { } release)
                throw new InvalidDataException("The local update was not offered: " + check.Message);
            _fixture.Trace("update-offered");
            using (var notice = new HubUpdateAvailableDialog(release))
            {
                if (_automatic) notice.Shown += (_, _) => notice.BeginInvoke(() => Field<DarkButton>(notice, "_view").PerformClick());
                if (notice.ShowDialog(this) != DialogResult.OK) return;
            }
            using var dialog = new HubUpdateDialog(check, _current, true, null,
                async (selected, progress, cancellation) =>
                {
                    using var local = _fixture.Client();
                    return await local.StageAsync(selected, _fixture.Runtime, _fixture.Storage, progress, cancellation);
                }, checkUpdates: async (include, cancellation) =>
                {
                    using var local = _fixture.Client();
                    return await local.CheckAsync(_current, cancellation, include);
                });
            dialog.Shown += (_, _) =>
            {
                // Other apps/components are outside the local test's scope.
                Field<DarkButton>(dialog, "_github").Visible = false;
                Field<DarkButton>(dialog, "_components").Visible = false;
            };
            using var automation = new System.Windows.Forms.Timer { Interval = 100 };
            Exception? automationError = null;
            if (_automatic)
            {
                bool clicked = false;
                var elapsed = Stopwatch.StartNew();
                automation.Tick += (_, _) =>
                {
                    var install = Field<DarkButton>(dialog, "_install");
                    var status = Field<Label>(dialog, "_status");
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(120) || status.ForeColor == HubForm.Bad)
                    {
                        automationError = new IOException("Local staging did not complete: " + status.Text);
                        automation.Stop(); dialog.Close(); return;
                    }
                    if (!install.Enabled) return;
                    if (!clicked) { clicked = true; install.PerformClick(); }
                    else if (dialog.StagedUpdate is not null) { automation.Stop(); install.PerformClick(); }
                };
                dialog.Shown += (_, _) => automation.Start();
            }
            DialogResult result = dialog.ShowDialog(this);
            if (automationError is not null) throw automationError;
            if (dialog.StagedUpdate is not { } staged) return;
            if (result != DialogResult.OK)
            { HubUpdatePackage.Discard(staged, _fixture.Storage); return; }
            _fixture.Trace("helper-launch-requested");
            HubUpdateRunner.StartHelper(staged, _fixture.Runtime, release.Version);
            HelperStarted = true;
            Close(); // Release the install lock and mapped EXE by exiting.
        }
        catch (Exception error)
        {
            _status.Text = "Local test failed: " + error.Message; _status.ForeColor = HubForm.Bad;
            _fixture.WriteResult("failure.json", new { passed = false, error = error.ToString() });
            if (_automatic) { Environment.ExitCode = 1; Close(); }
        }
        finally { if (!IsDisposed) _check.Enabled = true; }
    }

    private static T Field<T>(object target, string name) where T : class =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) ??
            throw new MissingFieldException(target.GetType().Name, name));
}
