using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using QproFaceTracking.Hub;

internal static class SetupNavigationTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _checks;
    private static string? _captures;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private static T Field<T>(HubForm form, string name) =>
        (T)(typeof(HubForm).GetField(name, Private)?.GetValue(form)
            ?? throw new Exception("Missing fixture field: " + name));

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static Panel PageOf(Control control)
    {
        for (Control? parent = control; parent is not null; parent = parent.Parent)
            if (parent is Panel page && Equals(page.Tag, "hub-page")) return page;
        throw new Exception("Control has no workflow page: " + control.GetType().Name);
    }

    private static void Pump(HubForm form)
    {
        var until = Environment.TickCount64 + 140;
        do { Application.DoEvents(); Thread.Sleep(5); } while (Environment.TickCount64 < until);
        form.FlushPreviewLayout();
        Application.DoEvents();
    }

    private static Button NamedButton(HubForm form, string name) => Descendants(form)
        .OfType<Button>().Single(button => button.AccessibleName == name);

    private static Button Disclosure(HubForm form, string text) => Descendants(form)
        .OfType<Button>().Single(button => button.Text == text);

    private static string OperationState(HubForm form) => string.Join("|", new[]
    {
        "_setupActionRunning", "_utilityActionRunning", "_datasetOperationBusy", "_starting",
        "_stopping", "_trackingCleanupPending", "_gazeStartupInProgress",
    }.Select(name => name + "=" + Field<bool>(form, name))) +
        "|workers=" + Field<List<System.Diagnostics.Process>>(form, "_trackingProcesses").Count +
        "|progress=" + Field<Control>(form, "_setupProgressContainer").Visible +
        "|run=" + Field<Label>(form, "_runStatus").Text +
        "|log=" + Field<RichTextBox>(form, "_log").Text;

    private static string PreferenceFingerprint(string root)
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "CustomLibs"),
            Path.Combine(root, "config"),
        };
        var lines = new List<string>();
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) { lines.Add(path + "|missing"); continue; }
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                // This is only an unchanged-state assertion. Contents and hashes
                // are never printed or stored as test artifacts.
                using var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                lines.Add(file + "|" + Convert.ToHexString(SHA256.HashData(input)));
            }
        }
        return string.Join("\n", lines);
    }

    private static void Capture(HubForm form, string name)
    {
        if (_captures is null) return;
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine(_captures, name + ".png"), ImageFormat.Png);
    }

    private static void CheckGeometry(HubForm form, Panel page, string context)
    {
        Check(!page.HorizontalScroll.Visible && page.AutoScrollPosition.X == 0,
            context + ": setup gained a horizontal scrollbar/offset.");
        var viewport = page.RectangleToScreen(page.ClientRectangle);
        var leaves = Descendants(page).Where(control => control.Visible && control.Width > 0 && control.Height > 0 &&
            control.Controls.Count == 0 && control is Label or ButtonBase or ComboBox or TextBoxBase).ToArray();
        foreach (var control in leaves)
        {
            var bounds = control.RectangleToScreen(control.ClientRectangle);
            if (bounds.Left < viewport.Left || bounds.Right > viewport.Right)
            {
                Console.Error.WriteLine($"GEOMETRY viewport X={viewport.X} Width={viewport.Width}; leaf X={bounds.X} Width={bounds.Width}");
                for (Control? ancestor = control.Parent; ancestor is not null && ancestor != page.Parent; ancestor = ancestor.Parent)
                {
                    var ancestorBounds = ancestor.RectangleToScreen(ancestor.ClientRectangle);
                    Console.Error.WriteLine($"GEOMETRY {ancestor.GetType().Name} X={ancestorBounds.X} Width={ancestorBounds.Width} Padding={ancestor.Padding.Horizontal}");
                    if (ancestor is TableLayoutPanel table)
                        Console.Error.WriteLine("GEOMETRY columns=" + string.Join(",", table.GetColumnWidths()));
                }
            }
            Check(bounds.Left >= viewport.Left && bounds.Right <= viewport.Right,
                context + ": a visible setup control extends outside the page width: " + control.Text);
        }
        for (var left = 0; left < leaves.Length; left++)
        for (var right = left + 1; right < leaves.Length; right++)
        {
            var intersection = Rectangle.Intersect(
                leaves[left].RectangleToScreen(leaves[left].ClientRectangle),
                leaves[right].RectangleToScreen(leaves[right].ClientRectangle));
            Check(intersection.Width <= 0 || intersection.Height <= 0,
                context + ": controls overlap: " + leaves[left].Text + " / " + leaves[right].Text);
        }
        var steps = Descendants(page).OfType<Button>().Where(button =>
            button.AccessibleName?.StartsWith("Setup step ", StringComparison.Ordinal) == true).ToArray();
        Check(steps.Length == 3 && steps.All(button => button.Width > 60 && button.Height >= 32),
            context + ": compact step navigation is missing or compressed.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--offscreen") continue;
            if (args[index] == "--captures" && index + 1 < args.Length) _captures = Path.GetFullPath(args[++index]);
            else throw new ArgumentException("Use --offscreen and/or --captures <directory>.");
        }
        if (_captures is not null) Directory.CreateDirectory(_captures);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.Dark);
        var root = Path.Combine(Path.GetTempPath(), "qpro-setup-navigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var beforePreferences = PreferenceFingerprint(root);
        using (var form = new HubForm(root, rememberLaunch: false, previewOnly: true)
               { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                 ShowInTaskbar = false, MinimumSize = Size.Empty, Size = new Size(1280, 900) })
        {
            form.ApplyPreviewScenario("setup-connect", "VirtualDesktop");
            form.Show(); Pump(form);
            var setupTab = NamedButton(form, "First-time setup");
            var page = PageOf(Field<Button>(form, "_setupRuntimeButton"));
            var livePage = PageOf(Field<CheckBox>(form, "_tongue"));
            var stepButtons = new[] { "Setup step 1: Connect", "Setup step 2: PC runtime", "Setup step 3: VRCFT module" }
                .Select(name => NamedButton(form, name)).ToArray();
            var back = NamedButton(form, "Previous setup step");
            var next = NamedButton(form, "Next setup step");
            var fieldNames = new[] { "_connectionMode", "_wirelessAddress", "_pairingEndpoint", "_pairingCode",
                "_reconnectUsbButton", "_forgetUsbButton", "_connectWirelessButton", "_enableWirelessButton",
                "_setupRuntimeButton", "_trackingSourceSetup", "_trackingSourceLive", "_setupBridgeButton",
                "_setupSteamLinkModuleButton", "_setupRuntimeStatus", "_setupBridgeStatus", "_tongueModels",
                "_setupGazeButton", "_inspectGazeButton", "_start", "_stop" };
            var original = fieldNames.ToDictionary(name => name, name => Field<Control>(form, name));
            var handles = original.ToDictionary(item => item.Key, item => item.Value.Handle);
            var noWork = OperationState(form);
            var startClicks = 0;
            Field<Button>(form, "_start").Click += (_, _) => startClicks++;

            void AssertNoWork(string context)
            {
                Check(OperationState(form) == noWork && startClicks == 0,
                    context + ": navigation started work or changed action feedback/log state.");
                Check(!Directory.EnumerateFileSystemEntries(root).Any(),
                    context + ": preview navigation wrote files in the isolated app root.");
            }
            void AssertStep(int step, bool wireless, string context)
            {
                Check(page.Visible && !livePage.Visible && Field<int>(form, "_selectedSetupStep") == step,
                    context + ": wrong setup step/page selected.");
                Check(Field<ComboBox>(form, "_connectionMode").Visible == (step == 0) &&
                    Field<Button>(form, "_reconnectUsbButton").Visible == (step == 0 && !wireless) &&
                    Field<Button>(form, "_connectWirelessButton").Visible == (step == 0 && wireless),
                    context + ": connection controls leaked into another step or wrong transport.");
                Check(Field<Button>(form, "_setupRuntimeButton").Visible == (step == 1) &&
                    Field<Label>(form, "_setupRuntimeStatus").Visible == (step == 1),
                    context + ": runtime controls are missing or leaked into another step.");
                var steamLink = Field<ComboBox>(form, "_trackingSourceSetup").SelectedIndex == 1;
                Check(Field<ComboBox>(form, "_trackingSourceSetup").Visible == (step == 2) &&
                    Field<Button>(form, "_setupBridgeButton").Visible == (step == 2 && !steamLink) &&
                    Field<Button>(form, "_setupSteamLinkModuleButton").Visible == (step == 2 && steamLink) &&
                    Field<Label>(form, "_setupBridgeStatus").Visible == (step == 2),
                    context + ": selected module action/status is missing or another action leaked.");
                Check(back.Enabled == (step > 0) && next.Text == (step == 2 ? "Open Facial tracking" : "Next step"),
                    context + ": navigation state/copy does not match the step.");
                Check(stepButtons.Count(button => button.AccessibleDescription?.StartsWith("Current setup step.", StringComparison.Ordinal) == true) == 1,
                    context + ": selected step is not exposed accessibly.");
                foreach (var item in original)
                    Check(ReferenceEquals(Field<Control>(form, item.Key), item.Value) && item.Value.Handle == handles[item.Key],
                        context + ": navigation recreated an original control: " + item.Key);
                AssertNoWork(context);
            }

            for (var step = 0; step < 3; step++)
            {
                stepButtons[step].PerformClick(); Pump(form);
                AssertStep(step, wireless: false, "USB step " + (step + 1));
                CheckGeometry(form, page, "USB step " + (step + 1));
                Capture(form, "setup-usb-step-" + (step + 1));
            }
            back.PerformClick(); Pump(form); AssertStep(1, false, "Back to runtime");
            back.PerformClick(); Pump(form); AssertStep(0, false, "Back to connect");
            next.PerformClick(); Pump(form); AssertStep(1, false, "Continue to runtime");
            next.PerformClick(); Pump(form); AssertStep(2, false, "Continue to module");
            next.PerformClick(); Pump(form);
            Check(livePage.Visible && !page.Visible, "Final navigation did not open Facial tracking.");
            AssertNoWork("Open Facial tracking");
            setupTab.PerformClick(); Pump(form); AssertStep(2, false, "Return to setup");

            // These are the three observed-state labels used by production.
            // Assert actual text/color event propagation, including a restart
            // warning. This does not invoke the live device/status pipeline.
            var sources = new[] { "_usbStatus", "_runtimeStatus", "_bridgeStatus" }.Select(name => Field<Label>(form, name)).ToArray();
            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                var mirror = stepButtons[index].Parent!.Controls.OfType<Label>().Single();
                Check(PageOf(source) == livePage && !ReferenceEquals(source, mirror), "Setup moved an original live status label.");
                source.Text = index == 2 ? "Installed · restart VRCFT" : "Checking · isolated fixture " + index;
                source.ForeColor = HubForm.Warning; Pump(form);
                Check(mirror.Text == source.Text && mirror.ForeColor == source.ForeColor,
                    "Setup overview did not mirror warning text/color for check " + index);
                if (index == 2)
                    Check(mirror.Text.Contains("restart VRCFT", StringComparison.Ordinal) &&
                        !mirror.Text.Contains("Complete", StringComparison.OrdinalIgnoreCase),
                        "Overview upgraded a module restart warning to completed setup.");
                source.ForeColor = HubForm.Bad; Pump(form);
                Check(mirror.ForeColor == HubForm.Bad && mirror.Text == source.Text, "A color-only observed-state update was lost.");
                source.Text += " · changed"; Pump(form);
                Check(mirror.Text == source.Text && mirror.ForeColor == HubForm.Bad, "A text-only observed-state update was lost.");
            }
            Capture(form, "setup-module-restart-warning");

            foreach (var source in new[] { "VirtualDesktop", "SteamLink" })
            foreach (var scenario in new[] { "setup-connect", "setup-runtime", "setup-module", "setup-complete" })
            {
                form.ApplyPreviewScenario(scenario, source); Pump(form);
                noWork = OperationState(form);
                for (var step = 0; step < 3; step++)
                {
                    stepButtons[step].PerformClick(); Pump(form);
                    AssertStep(step, false, scenario + "/" + source + "/" + step);
                }
                Check(Field<ComboBox>(form, "_trackingSourceSetup").SelectedIndex == (source == "SteamLink" ? 1 : 0),
                    "Changing preview stages changed the selected streaming app.");
                Capture(form, scenario + "-" + source);
            }

            form.ApplySetupWirelessPreview();
            var address = Field<TextBox>(form, "_wirelessAddress");
            var pairingEndpoint = Field<TextBox>(form, "_pairingEndpoint");
            var pairingCode = Field<TextBox>(form, "_pairingCode");
            address.Text = "192.0.2.15:5555";
            pairingEndpoint.Text = "192.0.2.15:37123";
            pairingCode.Text = "012345";
            Field<ComboBox>(form, "_tongueModels").SelectedIndex = 1;
            var options = form.CaptureLiveOptions();
            noWork = OperationState(form);
            foreach (var size in new[] { new Size(880, 700), new Size(780, 580), new Size(1280, 900) })
            {
                form.Size = size; Pump(form);
                for (var step = 0; step < 3; step++)
                {
                    stepButtons[step].PerformClick(); Pump(form);
                    var context = "Wi-Fi " + size.Width + "x" + size.Height + " step " + (step + 1);
                    AssertStep(step, true, context);
                    CheckGeometry(form, page, context);
                    Check(Field<ComboBox>(form, "_connectionMode").SelectedIndex == 1 && address.Text == "192.0.2.15:5555" &&
                        pairingEndpoint.Text == "192.0.2.15:37123" && pairingCode.Text == "012345" &&
                        Field<ComboBox>(form, "_trackingSourceSetup").SelectedIndex == 1 && form.CaptureLiveOptions() == options,
                        context + ": navigating/resizing lost entered connection or feature/model choices.");
                    Capture(form, "setup-wifi-" + size.Width + "-step-" + (step + 1));
                }
            }

            form.ApplyPreviewScenario("magisk", "SteamLink"); Pump(form);
            noWork = OperationState(form);
            var optional = Disclosure(form, "Show optional tools and recovery");
            Check(!Field<Button>(form, "_setupGazeButton").Visible && optional.Visible,
                "Optional gaze actions were visible before opening the extras.");
            optional.PerformClick(); Pump(form);
            Disclosure(form, "Show gaze setup").PerformClick(); Pump(form);
            Check(Field<Button>(form, "_setupGazeButton").Visible && Field<Button>(form, "_inspectGazeButton").Visible &&
                !Field<Button>(form, "_setupGazeButton").Enabled && !Field<Button>(form, "_inspectGazeButton").Enabled &&
                !Field<CheckBox>(form, "_gaze").Checked && !Field<CheckBox>(form, "_gaze").Enabled,
                "Opening optional tools bypassed the Magisk gaze gate.");
            Check(Descendants(page).OfType<Label>().Any(label => label.Visible &&
                label.Text.Contains("Magisk gaze users: skip gaze setup", StringComparison.Ordinal)),
                "Magisk skip guidance is missing from setup.");
            AssertNoWork("Open optional Magisk gaze");
            CheckGeometry(form, page, "Expanded optional Magisk gaze");
            Capture(form, "setup-magisk-optional-open");

            var optionalGroups = new[]
            {
                ("Optional features", new[] { "gaze setup", "hands setup and uninstall", "AMD setup" }),
                ("Checks & Updates", new[] { "headset compatibility", "Windows Streamer updates" }),
                ("Recovery", new[] { "PC runtime repair and recovery", "VRCFaceTracking module maintenance", "gaze recovery tools" }),
            };
            var actionFields = new[] { "_setupGazeButton", "_inspectGazeButton", "_installHandsButton", "_removeHandsButton",
                "_setupAmdButton", "_uninstallBridgeButton", "_recoverGazeButton", "_resetLegacyGazeButton",
                "_checkStreamerUpdater", "_pauseStreamerUpdater", "_restoreStreamerUpdater" };
            var preservedActions = actionFields.ToDictionary(name => name, name => Field<Control>(form, name));
            var preservedHandles = preservedActions.ToDictionary(item => item.Key, item => item.Value.Handle);
            var savedOptions = form.CaptureLiveOptions();
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 760), new Size(780, 580) })
            {
                form.Size = size; Pump(form);
                foreach (var (category, tools) in optionalGroups)
                {
                    NamedButton(form, "Optional tools category: " + category).PerformClick(); Pump(form);
                    AssertNoWork("Select optional category " + category);
                    Check(Descendants(page).Count(control => control.Visible && Equals(control.Tag, "optional-tool-body")) <= 1,
                        "Changing categories exposed more than one optional tool.");
                    foreach (var tool in tools)
                    {
                        var toggle = Descendants(page).OfType<Button>().Single(button => button.Visible &&
                            (button.Text == "Show " + tool || button.Text == "Hide " + tool));
                        if (toggle.Text.StartsWith("Hide ", StringComparison.Ordinal)) { toggle.PerformClick(); Pump(form); }
                        toggle.PerformClick(); Pump(form);
                        var bodies = Descendants(page).Where(control => control.Visible && Equals(control.Tag, "optional-tool-body")).ToArray();
                        Check(bodies.Length == 1 && bodies[0].AccessibleName == "Optional tool: " + tool,
                            "The selected optional tool is not the sole visible body: " + tool);
                        Check(Descendants(page).OfType<Button>().Count(button => button.Visible &&
                            button.Text.StartsWith("Hide ", StringComparison.Ordinal) && tools.Any(name => button.Text == "Hide " + name)) == 1,
                            "Exclusive tool selection left a stale disclosure label: " + category);
                        foreach (var item in preservedActions)
                            Check(ReferenceEquals(Field<Control>(form, item.Key), item.Value) && item.Value.Handle == preservedHandles[item.Key],
                                "Optional navigation recreated an action: " + item.Key);
                        Check(form.CaptureLiveOptions() == savedOptions, "Optional navigation changed saved feature or model choices.");
                        AssertNoWork("Open optional tool " + tool);
                        CheckGeometry(form, page, size.Width + "px optional tool " + tool);
                        if (tool == "hands setup and uninstall")
                        {
                            var names = Descendants(bodies[0]).OfType<Label>().Where(label => label.Text is "Quest" or "Singularity").ToArray();
                            Check(names.Length == 2 && names.All(label => label.Width >= TextRenderer.MeasureText(
                                label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width),
                                "Hands prerequisites wrapped their short field names into fragments.");
                        }
                        if (tool == "gaze setup")
                            Check(!Field<Button>(form, "_setupGazeButton").Enabled && !Field<Button>(form, "_inspectGazeButton").Enabled,
                                "Optional category navigation bypassed the Magisk gate.");
                    }
                }
            }
            foreach (var (category, tool) in new[]
            {
                ("Optional features", "hands setup and uninstall"),
                ("Recovery", "PC runtime repair and recovery"),
            })
            {
                form.Size = new Size(1280, 900); Pump(form);
                NamedButton(form, "Optional tools category: " + category).PerformClick(); Pump(form);
                var toggle = Descendants(page).OfType<Button>().Single(button => button.Visible &&
                    (button.Text == "Show " + tool || button.Text == "Hide " + tool));
                if (toggle.Text.StartsWith("Show ", StringComparison.Ordinal)) { toggle.PerformClick(); Pump(form); }
                page.AutoScrollPosition = new Point(0, Math.Min(120, page.VerticalScroll.Maximum)); Pump(form);
                var position = page.AutoScrollPosition;
                var geometry = Descendants(page).Where(control => control.Visible)
                    .ToDictionary(control => control, control => control.Bounds);
                var wraps = Descendants(form).OfType<Label>().Where(label => Equals(label.Tag, "responsive-info"))
                    .ToDictionary(label => label, label => label.MaximumSize);
                form.WindowState = FormWindowState.Minimized; Pump(form);
                Check(wraps.All(item => item.Key.MaximumSize == item.Value),
                    "Minimizing an expanded optional tool changed wrapping: " + tool);
                form.WindowState = FormWindowState.Normal; Pump(form);
                Check(page.AutoScrollPosition == position && geometry.All(item => item.Key.Visible && item.Key.Bounds == item.Value),
                    "Restoring an expanded optional tool changed scroll or settled geometry: " + tool);
                Check(Descendants(page).Count(control => control.Visible && Equals(control.Tag, "optional-tool-body")) == 1,
                    "Restore changed exclusive optional-tool selection: " + tool);
                foreach (var item in preservedActions)
                    Check(ReferenceEquals(Field<Control>(form, item.Key), item.Value) && item.Value.Handle == preservedHandles[item.Key],
                        "Restore recreated an optional action: " + item.Key);
                AssertNoWork("Restore expanded optional tool " + tool);
                CheckGeometry(form, page, "Restored optional tool " + tool);
            }
            Disclosure(form, "Hide optional tools and recovery").PerformClick(); Pump(form);
            Check(!Descendants(page).Any(control => control.Visible && Equals(control.Tag, "optional-tool-body")),
                "Closing optional tools left a tool body visible.");
            AssertNoWork("Close optional tools");
        }
        Check(PreferenceFingerprint(root) == beforePreferences,
            "Preview navigation changed preferences or installed VRCFaceTracking module files.");
        Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Preview disposal wrote app state.");
        Console.WriteLine("PASS: " + _checks + " isolated setup navigation, state, status and layout checks.");
    }
}
