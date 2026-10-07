using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var previewRequested = args.Any(value => value.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (args.Length == 2 && args[0] == "--apply-update")
                return HubUpdateRunner.Run(args[1]);
            var executableRoot = Path.GetFullPath(AppContext.BaseDirectory);
            var packagedRuntime = Path.Combine(executableRoot, "QproRuntime");
            var rootArgument = Array.FindIndex(args, value => value.Equals("--root", StringComparison.OrdinalIgnoreCase));
            if (rootArgument >= 0 && rootArgument + 1 >= args.Length)
                throw new ArgumentException("--root requires a folder path.");
            var root = rootArgument >= 0
                ? Path.GetFullPath(args[rootArgument + 1])
                : File.Exists(Path.Combine(packagedRuntime, "release-manifest.json"))
                    ? packagedRuntime
                    : File.Exists(Path.Combine(executableRoot, "release-manifest.json"))
                        ? executableRoot
                        : Directory.GetCurrentDirectory();
            var selfTestArgument = Array.FindIndex(args, value => value.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
            if (selfTestArgument >= 0)
            {
                if (selfTestArgument + 1 >= args.Length) throw new ArgumentException("--self-test requires an output JSON path.");
                WriteSelfTest(root, Path.GetFullPath(args[selfTestArgument + 1]));
                return 0;
            }

            if (previewRequested)
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.Dark);
            var renderArgument = Array.FindIndex(args, value => value.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
            if (renderArgument >= 0)
            {
                if (renderArgument + 1 >= args.Length) throw new ArgumentException("--render-preview requires an output PNG path.");
                var scenarioArgument = Array.FindIndex(args, value => value.Equals("--preview-scenario", StringComparison.OrdinalIgnoreCase));
                if (scenarioArgument >= 0 && scenarioArgument + 1 >= args.Length)
                    throw new ArgumentException("--preview-scenario requires ready, unsupported, or runtime-error.");
                var scenario = scenarioArgument >= 0 ? args[scenarioArgument + 1] : "ready";
                var sourceArgument = Array.FindIndex(args, value => value.Equals("--preview-source", StringComparison.OrdinalIgnoreCase));
                if (sourceArgument >= 0 && sourceArgument + 1 >= args.Length)
                    throw new ArgumentException("--preview-source requires VirtualDesktop or SteamLink.");
                var source = sourceArgument >= 0 ? args[sourceArgument + 1] : "VirtualDesktop";
                using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
                if (args.Any(value => value.Equals("--preview-small", StringComparison.OrdinalIgnoreCase)))
                    form.Size = form.MinimumSize;
                var viewportArgument = Array.FindIndex(args, value => value.Equals("--preview-viewport", StringComparison.OrdinalIgnoreCase));
                Size? requestedViewport = null;
                if (viewportArgument >= 0)
                {
                    if (viewportArgument + 2 >= args.Length || !int.TryParse(args[viewportArgument + 1], out var width) || !int.TryParse(args[viewportArgument + 2], out var height) || width < 640 || height < 480 || width > 7680 || height > 4320)
                        throw new ArgumentException("--preview-viewport requires dimensions between 640 by 480 and 7680 by 4320.");
                    form.MinimumSize = Size.Empty;
                    requestedViewport = new Size(width, height);
                    form.Size = requestedViewport.Value;
                }
                form.Show();
                Application.DoEvents();
                var pageArgument = Array.FindIndex(args, value => value.Equals("--preview-page", StringComparison.OrdinalIgnoreCase));
                if (pageArgument >= 0)
                {
                    if (pageArgument + 1 >= args.Length) throw new ArgumentException("--preview-page requires a page name.");
                    var requested = args[pageArgument + 1];
                    var tab = FindControl(form, control => Equals(control.Tag, "workflow-tab") && control.Text.Contains(requested, StringComparison.OrdinalIgnoreCase)) as Button;
                    if (tab is null) throw new ArgumentException("Unknown preview page: " + requested);
                    tab.PerformClick();
                    Application.DoEvents();
                }
                form.ApplyPreviewScenario(scenario, source);
                Application.DoEvents();
                var expandArgument = Array.FindIndex(args, value => value.Equals("--preview-expand", StringComparison.OrdinalIgnoreCase));
                if (expandArgument >= 0)
                {
                    if (expandArgument + 1 >= args.Length) throw new ArgumentException("--preview-expand requires disclosure names separated by commas.");
                    foreach (var name in args[expandArgument + 1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        // Only informational disclosure controls may be clicked
                        // by preview tooling; installers and tracking actions are excluded.
                        var disclosure = FindControl(form, control => control is Button button && button.Visible &&
                            button.Text.Equals("Show " + name, StringComparison.OrdinalIgnoreCase)) as Button;
                        disclosure ??= FindControl(form, control => control is Button button && button.Visible &&
                            button.Text.StartsWith("Show ", StringComparison.Ordinal) &&
                            button.Text.EndsWith(name, StringComparison.OrdinalIgnoreCase)) as Button;
                        if (disclosure is null) throw new ArgumentException("Unknown preview disclosure: " + name);
                        disclosure.PerformClick();
                        Application.DoEvents();
                    }
                }
                if (args.Any(value => value.Equals("--preview-scroll-bottom", StringComparison.OrdinalIgnoreCase)))
                {
                    var scroll = FindControl(form, control => control is Panel panel && Equals(panel.Tag, "hub-page") && panel.Visible) as Panel;
                    if (scroll is not null) scroll.AutoScrollPosition = new Point(0, scroll.VerticalScroll.Maximum);
                    Application.DoEvents();
                }
                var scrollToArgument = Array.FindIndex(args, value => value.Equals("--preview-scroll-to", StringComparison.OrdinalIgnoreCase));
                if (scrollToArgument >= 0)
                {
                    if (scrollToArgument + 1 >= args.Length) throw new ArgumentException("--preview-scroll-to requires a section title.");
                    var scroll = FindControl(form, control => control is Panel panel && Equals(panel.Tag, "hub-page") && panel.Visible) as Panel;
                    var target = scroll is null ? null : FindControl(scroll, control => control.Visible &&
                        control.Text.Equals(args[scrollToArgument + 1], StringComparison.OrdinalIgnoreCase));
                    if (scroll is null || target is null) throw new ArgumentException("Unknown preview section: " + args[scrollToArgument + 1]);
                    // Scrolling exposes a section without invoking its setup action.
                    var section = target.Parent is TableLayoutPanel ? target.Parent : target;
                    var offset = scroll.PointToClient(section.PointToScreen(Point.Empty)).Y - scroll.AutoScrollPosition.Y;
                    scroll.AutoScrollPosition = new Point(0, Math.Max(0, offset - 12));
                    Application.DoEvents();
                }
                Control imageSource = form;
                if (requestedViewport is { } viewport && (viewport.Width > form.Width || viewport.Height > form.Height))
                {
                    // Windows caps top-level windows to this monitor's work area. Size the
                    // layout surface directly to preview a larger display without moving it.
                    var layoutSurface = form.Controls[0];
                    layoutSurface.Dock = DockStyle.None;
                    layoutSurface.Size = viewport;
                    layoutSurface.PerformLayout();
                    Application.DoEvents();
                    imageSource = layoutSurface;
                }
                using var preview = new Bitmap(imageSource.Width, imageSource.Height);
                imageSource.DrawToBitmap(preview, new Rectangle(Point.Empty, imageSource.Size));
                var output = Path.GetFullPath(args[renderArgument + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                preview.Save(output, ImageFormat.Png);
                Console.WriteLine($"Preview saved: {output}; scenario={scenario}; source={source}; size={imageSource.Width}x{imageSource.Height}; sample data only.");
                form.Close();
                return 0;
            }
            if (HubUpdateRunner.StartRecoveryIfNeeded(root)) return 0;
            IDisposable? installUseLock;
            try { installUseLock = HubUpdateRunner.AcquireInstallUseLock(root); }
            catch (IOException error)
            {
                MessageBox.Show(error.Message, "Qpro Hub is in use", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            using (installUseLock) Application.Run(new HubForm(root));
            return 0;
        }
        catch (Exception error)
        {
            // Preview tooling must report failures to its caller. A modal dialog
            // would leave an unattended screenshot check waiting indefinitely.
            if (previewRequested)
            {
                Console.Error.WriteLine("Hub preview failed: " + error);
                return 1;
            }
            string? log = null;
            try
            {
                var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking");
                Directory.CreateDirectory(localData);
                log = Path.Combine(localData, "qpro-hub-crash.txt");
                File.WriteAllText(log, error.ToString());
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                // Show the original startup failure even on a read-only install.
                log = null;
            }
            MessageBox.Show(error.Message + "\n\n" + (log is null ? error.ToString() : "Details: " + log),
                "QproFaceTracking Hub could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static Control? FindControl(Control root, Func<Control, bool> predicate)
    {
        if (predicate(root)) return root;
        foreach (Control child in root.Controls)
        {
            var match = FindControl(child, predicate);
            if (match is not null) return match;
        }
        return null;
    }

    private static void WriteSelfTest(string root, string outputPath)
    {
        var eyeProfiles = Directory.Exists(Path.Combine(root, "calibration"))
            ? Directory.GetFiles(Path.Combine(root, "calibration"), "qpro-independent-visual-axis-v*.json").Select(Path.GetFileName).Order().ToArray()
            : [];
        var tonguePairs = Directory.Exists(Path.Combine(root, "models"))
            ? Directory.GetFiles(Path.Combine(root, "models"), "qpro-stereo-tongue-v*-gate.pt")
                .Select(path => Path.GetFileName(path)!.Replace("-gate.pt", "", StringComparison.OrdinalIgnoreCase))
                .Where(prefix => File.Exists(Path.Combine(root, "models", prefix + "-direction.pt")))
                .Order().ToArray()
            : [];
        var requiredFiles = new[]
        {
            "build-and-run.ps1", "native-eye-local-branch-test.ps1", "prepare-eye-model.ps1", "prepare_eye_model.py", "research\\patch_seacliff_independent_axes.py", "native_raw_eye_probe.py", "install-vrcft-eye-bridge.ps1", "uninstall-vrcft-eye-bridge.ps1", "vrcft-module-installation.ps1", "runtime-python.ps1",
            "platform-tools\\adb.exe", "platform-tools\\AdbWinApi.dll", "platform-tools\\AdbWinUsbApi.dll",
            "python-runtime\\python.3.12.10.nupkg", "python-runtime\\LICENSE.txt", "python-runtime\\README.txt",
            "SFX\\succeed.wav", "SFX\\trainingComplete.wav", "SFX\\warning.wav",
            "calibration_inspect.py", "pupil_dilation.py", "pupil_gaze_calibration.py", "qpro_gpu.py",
            "tongue_visibility_calibration.py", "model_preview.py", "hybrid_preview.py", "train_model.py",
            "libquestpro-camera-streamer-v8.so", "questpro-camera-relay-v8", "questpro-camera-injector",
            "vd-label-bridge\\bin\\Release\\net10.0\\Qpro.VirtualDesktopLabelBridge.exe",
            "vrcft-gaze-bridge\\bin\\Release\\net10.0\\Qpro.GazeBridge.dll"
        };
        var result = new
        {
            ok = requiredFiles.All(path => File.Exists(Path.Combine(root, path))) && eyeProfiles.Length > 0 && tonguePairs.Length > 0,
            root,
            releaseMode = File.Exists(Path.Combine(root, "release-manifest.json")),
            eyeProfiles,
            tonguePairs,
            missingFiles = requiredFiles.Where(path => !File.Exists(Path.Combine(root, path))).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record FileChoice(string Label, string Primary, string? Secondary = null,
    bool IsExperimental = false, bool HasMoustacheIcon = false)
{
    public override string ToString() => Label;
}

internal sealed record DatasetInfo(string SessionPath, string CapturePath, string DisplayName, int SampleCount, bool Completed, bool LegacyDiagonalOnly = false);

internal sealed record DatasetChoice(DatasetInfo Dataset)
{
    public override string ToString() => $"{(Dataset.LegacyDiagonalOnly ? "Legacy diagonal-only · " : string.Empty)}{Dataset.DisplayName} · {Dataset.SampleCount} stills";
}

internal sealed record RecordedDatasetChoice(DatasetInfo Dataset, bool Trained)
{
    public override string ToString() => $"{(Dataset.LegacyDiagonalOnly ? "Legacy diagonal-only · " : string.Empty)}{Dataset.DisplayName} · {(Trained ? "trained" : Dataset.Completed ? "ready" : "incomplete")}";
}
