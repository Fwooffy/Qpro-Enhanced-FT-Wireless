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
            button.AccessibleName?.StartsWith("Setup section: ", StringComparison.Ordinal) == true).ToArray();
        Check(steps.Length == 3 && steps.All(button => button.Width > 60 && button.Height >= 32),
            context + ": compact step navigation is missing or compressed.");
    }

    private static void CheckOptionalButtons(HubForm form, Panel page, Control body, string context)
    {
        var actions = Descendants(body).OfType<Button>().Where(button => button.Visible).ToArray();
        Check(actions.Length > 0, context + ": optional tool has no visible actions.");
        foreach (var action in actions)
        {
            var caption = TextRenderer.MeasureText(action.Text, action.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var disclosure = action is DarkButton { VisualStyle: HubButtonStyle.Disclosure };
            var minimumHeight = (int)Math.Ceiling((disclosure ? 24 : 32) * form.DeviceDpi / 96F);
            Check(action.Height >= minimumHeight,
                context + ": action collapsed vertically (" + action.Height + "px): " + action.Text);
            var captionInset = disclosure ? (int)Math.Ceiling(30 * form.DeviceDpi / 96F) : action.Padding.Horizontal;
            Check(action.ClientSize.Width >= caption.Width + captionInset &&
                  action.ClientSize.Height >= caption.Height + action.Padding.Vertical,
                context + ": action does not fit its caption: " + action.Text);
            var bounds = action.RectangleToScreen(action.ClientRectangle);
            // The outer page may scroll. Each intermediate container must still
            // reserve the whole action, including controls below that viewport.
            for (var ancestor = action.Parent; ancestor is not null && ancestor != page; ancestor = ancestor.Parent)
                Check(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(bounds),
                    context + ": " + ancestor.GetType().Name + " clips action: " + action.Text);
        }
    }

    private static void CheckSteamVrStartupClicks()
    {
        var root = Path.Combine(Path.GetTempPath(), "qpro-startup-clicks-" + Guid.NewGuid().ToString("N"));
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, MinimumSize = Size.Empty, Size = new Size(780, 580),
        };
        var toggle = Field<CheckBox>(form, "_launchWithSteamVr");
        var status = Field<Label>(form, "_steamVrStartupStatus");
        var log = Field<RichTextBox>(form, "_log");
        var writes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        using var allowFirstWrite = new ManualResetEventSlim();
        const string failure = "SteamVR registration unavailable in this fixture.";
        int saved = 0;
        SteamVrLaunchStatus Read()
        {
            return Volatile.Read(ref saved) == 1 ? new(true, true, "On in fixture", true) : new(false, true, "Off in fixture", true);
        }
        form.BindSteamVrStartup(Read, enabled =>
        {
            writes.Enqueue(enabled);
            if (writes.Count == 1 && !allowFirstWrite.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("The fixture did not release the first write.");
            if (writes.Count == 3) throw new IOException(failure);
            Volatile.Write(ref saved, enabled ? 1 : 0);
            return Read();
        });

        void WaitFor(Func<bool> complete, string context)
        {
            var until = Environment.TickCount64 + 8000;
            while (!complete() && Environment.TickCount64 < until) Pump(form);
            Check(complete(), $"{context} Status={status.Text}, checked={toggle.Checked}, enabled={toggle.Enabled}.");
        }
        form.Show();
        WaitFor(() => toggle.Enabled && status.Text == "Off in fixture", "SteamVR startup read never settled.");
        Check(!toggle.Checked && writes.IsEmpty, "Reading the initial SteamVR preference changed it.");
        PageOf(toggle).ScrollControlIntoView(toggle);
        toggle.AccessibilityObject.DoDefaultAction();
        try
        {
            WaitFor(() => !writes.IsEmpty, "The startup checkbox did not dispatch the enable action.");
            Check(!toggle.Enabled && toggle.Checked, "A pending SteamVR registration allowed another click.");
        }
        finally { allowFirstWrite.Set(); }
        WaitFor(() => toggle.Enabled && status.Text == "On in fixture", "Enabling SteamVR startup did not settle.");
        Check(toggle.Checked, "The SteamVR startup toggle lost its applied on state.");
        toggle.AccessibilityObject.DoDefaultAction();
        WaitFor(() => toggle.Enabled && status.Text == "Off in fixture", "Disabling SteamVR startup did not settle.");
        Check(!toggle.Checked, "The SteamVR startup toggle lost its applied off state.");
        toggle.AccessibilityObject.DoDefaultAction();
        WaitFor(() => toggle.Enabled && status.Text == failure, "The startup registration error did not reach setup.");
        Check(!toggle.Checked && status.Visible && status.ForeColor == HubForm.Warning && log.Text.Contains(failure),
            "The failed change did not restore the prior startup choice and report why.");
        Check(writes.SequenceEqual(new[] { true, false, true }),
            "Refreshing or rolling back the SteamVR checkbox triggered an extra write.");
        Console.WriteLine("PASS SteamVR startup checkbox: initial read, enable, disable, busy gating and failure rollback.");
    }

    private static void CheckSetupActionRouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "qpro-setup-actions-" + Guid.NewGuid().ToString("N"));
        var config = Directory.CreateDirectory(Path.Combine(root, "config"));
        var mode = Path.Combine(config.FullName, "connection-mode.txt");
        File.WriteAllText(mode, "wireless");
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, MinimumSize = Size.Empty, Size = new Size(780, 580),
        };
        form.Show(); Pump(form);
        var runtime = Field<Button>(form, "_setupRuntimeButton");
        var page = PageOf(runtime);
        var progress = Field<Control>(form, "_setupProgressContainer");
        var progressStatus = Field<Label>(form, "_setupProgressStatus");
        var connectionMode = Field<ComboBox>(form, "_connectionMode");
        var nextCalls = new List<(string Label, string Script, string[] Arguments)>();

        form.SelectSetupStepForPreview(1);
        var address = Field<TextBox>(form, "_wirelessAddress");
        var endpoint = Field<TextBox>(form, "_pairingEndpoint");
        var code = Field<TextBox>(form, "_pairingCode");
        Disclosure(form, "Show wireless setup options").PerformClick(); Pump(form);
        Disclosure(form, "Show pairing options").PerformClick(); Pump(form);
        CheckOptionalButtons(form, page, Field<Control>(form, "_wirelessSetup"), "Compact Wi-Fi actions");
        var wirelessActions = new[] { "_enableWirelessButton", "_connectWirelessButton", "_pairWirelessButton", "_disableWirelessButton" }
            .Select(name => Field<Button>(form, name)).ToArray();
        var untouched = OperationState(form);
        foreach (var action in wirelessActions)
        {
            Check(action.Visible && action.Enabled, "The Wi-Fi action is unreachable in the compact setup fixture: " + action.Text);
            action.PerformClick(); Pump(form);
            Check(OperationState(form) == untouched, "A plain preview dispatched a Wi-Fi command: " + action.Text);
        }
        form.SelectSetupStepForPreview(2); Pump(form);
        runtime.PerformClick(); Pump(form);
        Check(OperationState(form) == untouched, "A plain preview dispatched a runtime installation.");

        var install = new TaskCompletionSource<bool>();
        form.ConfigurePreviewSetupCommands((label, script, arguments) =>
        {
            nextCalls.Add((label, script, arguments));
            return script == "setup-runtime.ps1" ? install.Task : Task.FromResult(script != "Pair-QproWireless.ps1");
        });
        runtime.PerformClick(); Pump(form);
        Check(nextCalls.Count == 1 && nextCalls[0] is { Label: "PC runtime setup", Script: "setup-runtime.ps1", Arguments.Length: 0 },
            "Install runtime did not dispatch the bundled installation route.");
        Check(progress.Visible && Field<bool>(form, "_setupActionRunning") && !runtime.Enabled &&
            !connectionMode.Enabled && !Field<ComboBox>(form, "_trackingSourceSetup").Enabled,
            "A pending setup command did not block conflicting setup controls.");
        var viewActivity = Descendants(page).OfType<Button>().Single(button => button.Text == "View setup activity");
        viewActivity.PerformClick(); Pump(form);
        Check(!page.Visible && PageOf(Field<RichTextBox>(form, "_log")).Visible,
            "View setup activity did not open the Activity page during a running action.");
        install.SetResult(true); Pump(form);
        NamedButton(form, "First-time setup").PerformClick(); Pump(form);
        Check(!Field<bool>(form, "_setupActionRunning") && runtime.Enabled && connectionMode.Enabled &&
            Field<ComboBox>(form, "_trackingSourceSetup").Enabled && progressStatus.Text.Contains("completed successfully"),
            $"Setup completion left controls disabled until a later status probe. Busy={Field<bool>(form, "_setupActionRunning")}; " +
            $"Runtime={runtime.Enabled}; transport={connectionMode.Enabled}; source={Field<ComboBox>(form, "_trackingSourceSetup").Enabled}; " +
            $"progress={progressStatus.Text}; form={form.Enabled}; page={page.Enabled}/{page.Visible}; " +
            $"closing={Field<bool>(form, "_closingInProgress")}; recovery={Field<bool>(form, "_gazeRecoveryRunning")}; {OperationState(form)}");

        form.SelectSetupStepForPreview(1); Pump(form);
        address.Text = " 192.0.2.15 ";
        endpoint.Text = "192.0.2.15:37123";
        code.Text = "012345";
        foreach (var action in wirelessActions) { action.PerformClick(); Pump(form); }
        Check(nextCalls.Count == 5 && nextCalls[1].Script == "Enable-QproWireless.ps1" && nextCalls[1].Arguments.Length == 0,
            "Enable from USB did not dispatch its Wi-Fi setup helper.");
        Check(nextCalls[2].Script == "Connect-QproWireless.ps1" && nextCalls[2].Arguments.SequenceEqual(new[] { "-AdbTarget", "192.0.2.15:5555" }),
            "Connect to Quest did not normalize and pass the target.");
        Check(nextCalls[3].Script == "Pair-QproWireless.ps1" && nextCalls[3].Arguments.SequenceEqual(new[] {
            "-PairingEndpoint", "192.0.2.15:37123", "-AdbTarget", "192.0.2.15:5555", "-PairingCode", "012345" }),
            "Pair and connect did not route the separate pairing endpoint and target.");
        Check(nextCalls[4].Script == "Disable-QproWireless.ps1" && nextCalls[4].Arguments.Length == 0 &&
            code.Text.Length == 0 && address.Text.Length == 0 && connectionMode.SelectedIndex == 0,
            "Disable Wi-Fi ADB did not route its helper or clear input; pairing did not clear the synthetic code on failure.");
        Check(File.ReadAllText(mode) == "wireless" && Directory.EnumerateFiles(config.FullName).Count() == 1,
            "Setup command fixtures modified the isolated connection preferences.");
        Console.WriteLine("PASS setup button dispatch: PC runtime, Activity navigation, and four Wi-Fi actions with isolated commands.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.Dark);
        using var loop = new ApplicationContext();
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        int result = 0;
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (args is ["--startup-clicks"]) CheckSteamVrStartupClicks();
                else Run(args);
            }
            catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
            finally { loop.ExitThread(); }
        });
        Application.Run(loop);
        return result;
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
        CheckSteamVrStartupClicks();
        CheckSetupActionRouting();
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
            var stepButtons = new[] { "Setup section: VRChat output", "Setup section: Headset", "Setup section: PC runtime" }
                .Select(name => NamedButton(form, name)).ToArray();
            var features = NamedButton(form, "Choose tracking features");
            var checkVrchat = NamedButton(form, "Check VRChat connection in setup");
            var vrchatSetupStatus = Descendants(form).OfType<Label>().Single(label => label.AccessibleName == "VRChat OSC setup status");
            var fieldNames = new[] { "_connectionMode", "_wirelessAddress", "_pairingEndpoint", "_pairingCode",
                "_reconnectUsbButton", "_forgetUsbButton", "_connectWirelessButton", "_enableWirelessButton",
                "_setupRuntimeButton", "_trackingSourceSetup", "_trackingSourceLive", "_setupRuntimeStatus",
                "_directVrchatStatus", "_tongueModels", "_launchWithSteamVr", "_steamVrStartupStatus",
                "_setupGazeButton", "_inspectGazeButton", "_start", "_stop" };
            var original = fieldNames.ToDictionary(name => name, name => Field<Control>(form, name));
            var handles = original.ToDictionary(item => item.Key, item => item.Value.Handle);
            var noWork = OperationState(form);
            var startClicks = 0;
            Field<Button>(form, "_start").Click += (_, _) => startClicks++;
            var checkUsb = Field<Button>(form, "_reconnectUsbButton");
            var forgetUsb = Field<Button>(form, "_forgetUsbButton");
            var route = Field<HubOutputRouteSwitch>(form, "_outputRouteSetup");
            var installBackup = Field<Button>(form, "_installBackupModuleButton");
            var direct = route.Controls.OfType<RadioButton>().Single(radio => radio.AccessibleName == "Direct to VRChat");
            var backup = route.Controls.OfType<RadioButton>().Single(radio => radio.AccessibleName == "VRCFaceTracking backup");
            Check(route.Visible && direct.Checked && !backup.Checked && route.SelectedIndex == 0 && checkVrchat.Visible &&
                !installBackup.Visible && !checkUsb.Visible && direct.TabStop && !backup.TabStop &&
                direct.AccessibilityObject.Role == AccessibleRole.RadioButton && backup.AccessibilityObject.Role == AccessibleRole.RadioButton,
                "Fresh setup did not expose Direct as the selected accessible output choice.");
            var startup = Field<CheckBox>(form, "_launchWithSteamVr");
            var startupStatus = Field<Label>(form, "_steamVrStartupStatus");
            Check(startup.Visible && startupStatus.Visible && PageOf(startup) == page &&
                  startupStatus.Parent == startup.Parent && !string.IsNullOrWhiteSpace(startupStatus.Text),
                "First-time setup did not expose the SteamVR startup preference and its status.");
            stepButtons[1].PerformClick(); Pump(form);
            Check(checkUsb.Visible && checkUsb.Enabled && forgetUsb.Visible && !forgetUsb.Enabled,
                "An empty USB setup must allow checking and disable Forget until it has a remembered headset.");
            foreach (var action in new[] { checkUsb, forgetUsb })
            {
                action.PerformClick(); Pump(form);
                Check(OperationState(form) == noWork, "A USB action broke preview isolation: " + action.Text);
            }

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
                Check(Field<ComboBox>(form, "_connectionMode").Visible == (step == 1) &&
                    Field<Button>(form, "_reconnectUsbButton").Visible == (step == 1 && !wireless) &&
                    Field<Button>(form, "_connectWirelessButton").Visible == (step == 1 && wireless),
                    context + ": connection controls leaked into another step or wrong transport.");
                Check(Field<Button>(form, "_setupRuntimeButton").Visible == (step == 2) &&
                    Field<Label>(form, "_setupRuntimeStatus").Visible == (step == 2),
                    context + ": runtime controls are missing or leaked into another step.");
                Check(Field<ComboBox>(form, "_trackingSourceSetup").Visible == (step == 0) &&
                    checkVrchat.Visible == (step == 0) && vrchatSetupStatus.Visible == (step == 0),
                    context + ": VRChat setup action/status is missing or another action leaked.");
                Check(features.Visible && features.Enabled, context + ": tracking features are unreachable from setup.");
                Check(stepButtons.Count(button => button.AccessibleDescription?.StartsWith("Current setup section.", StringComparison.Ordinal) == true) == 1,
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
            stepButtons[0].PerformClick(); Pump(form);
            AssertStep(0, false, "Return to output");
            Check(Field<Label>(form, "_setupRuntimeStatus").Text.Contains("Optional for native face and eyes"),
                "Native face setup implied an unavailable PC runtime was required.");
            features.PerformClick(); Pump(form);
            Check(livePage.Visible && !page.Visible, "Final navigation did not open Facial tracking.");
            Check(Descendants(livePage).OfType<Label>().Any(label => label.Visible &&
                label.Text.Contains("Native face and eyes connect automatically")) &&
                !Descendants(livePage).OfType<CheckBox>().Any(toggle => toggle.Text.Contains("Stream native face and eyes")),
                "Facial tracking did not explain automatic native output or kept the old switch.");
            Capture(form, "facial-tracking-automatic-native");
            AssertNoWork("Open Facial tracking");
            var connectionDetails = Disclosure(form, "Show connection and camera settings");
            connectionDetails.PerformClick(); Pump(form);
            Descendants(livePage).OfType<Button>().Single(button => button.Text == "Open setup").PerformClick(); Pump(form);
            AssertStep(0, false, "Open setup from Facial tracking");

            var sources = new[] { "_directVrchatStatus", "_usbStatus" }.Select(name => Field<Label>(form, name)).ToArray();
            var headsetSetupStatus = Descendants(page).OfType<Label>().Single(label => label.AccessibleName == "Headset setup status");
            var mirrors = new[] { vrchatSetupStatus, headsetSetupStatus };
            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                var mirror = mirrors[index];
                Check(PageOf(source) == livePage && !ReferenceEquals(source, mirror), "Setup moved an original live status label.");
                source.Text = index == 0 ? "Waiting for VRChat · sample" : "Checking · isolated fixture";
                source.ForeColor = HubForm.Warning; Pump(form);
                Check(mirror.Text == source.Text && mirror.ForeColor == source.ForeColor && mirror.AccessibleDescription == source.Text,
                    "Setup card did not mirror current status and accessible description.");
                source.ForeColor = HubForm.Bad; Pump(form);
                Check(mirror.ForeColor == HubForm.Bad && mirror.Text == source.Text, "A color-only observed-state update was lost.");
                source.Text += " · changed"; Pump(form);
                Check(mirror.Text == source.Text && mirror.ForeColor == HubForm.Bad, "A text-only observed-state update was lost.");
            }
            Capture(form, "setup-vrchat-pending");

            form.Size = new Size(780, 580);
            sources[0].Text = "VRChat discovery timed out. Open the game, enable OSC in its Action Menu and check your connection again.";
            Pump(form);
            CheckGeometry(form, page, "Compact VRChat setup with long status");
            var facialTab = NamedButton(form, "Facial tracking");
            facialTab.PerformClick(); Pump(form);
            var liveViewport = livePage.RectangleToScreen(livePage.ClientRectangle);
            var liveStatusBounds = sources[0].RectangleToScreen(sources[0].ClientRectangle);
            var checkVrchatLive = Descendants(livePage).OfType<Button>().Single(button => button.Text == "Check VRChat connection");
            Check(liveViewport.Contains(liveStatusBounds) && liveStatusBounds.Height > sources[0].Font.Height &&
                checkVrchatLive.Visible && !livePage.HorizontalScroll.Visible,
                "The compact live page clipped the long VRChat status or hid its action.");
            setupTab.PerformClick();
            form.Size = new Size(1280, 900);
            Pump(form);

            foreach (var source in new[] { "VirtualDesktop", "SteamLink" })
            foreach (var scenario in new[] { "setup-connect", "setup-runtime", "setup-vrchat", "setup-complete" })
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
                label.Text.Contains("Magisk gaze", StringComparison.OrdinalIgnoreCase)),
                "Magisk skip guidance is missing from setup.");
            AssertNoWork("Open optional Magisk gaze");
            CheckGeometry(form, page, "Expanded optional Magisk gaze");
            Capture(form, "setup-magisk-optional-open");

            var optionalGroups = new[]
            {
                ("Optional features", new[] { "gaze setup", "hands setup and uninstall", "AMD setup" }),
                ("Checks & Updates", new[] { "headset compatibility", "Windows Streamer updates" }),
                ("Recovery", new[] { "PC runtime repair and recovery", "gaze recovery tools" }),
            };
            var actionFields = new[] { "_setupGazeButton", "_inspectGazeButton", "_installHandsButton", "_removeHandsButton",
                "_setupAmdButton", "_recoverGazeButton", "_resetLegacyGazeButton",
                "_checkStreamerUpdater", "_pauseStreamerUpdater", "_restoreStreamerUpdater", "_checkCompatibility",
                "_repairRuntimeButton", "_checkRuntimeRecoveryButton", "_restoreRuntimeButton" };
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
                        CheckOptionalButtons(form, page, optional.Parent!, size.Width + "px optional tool " + tool);
                        var expectedButtons = tool switch
                        {
                            "Windows Streamer updates" or "PC runtime repair and recovery" => 3,
                            _ => 2,
                        };
                        Check(Descendants(bodies[0]).OfType<Button>().Count(button => button.Visible) == expectedButtons,
                            tool + ": expected optional actions/disclosures are missing.");
                        var nestedName = tool == "AMD setup" ? "AMD compatibility details" :
                            tool == "headset compatibility" ? "firmware details" : null;
                        if (nestedName is not null)
                        {
                            Disclosure(form, "Show " + nestedName).PerformClick(); Pump(form);
                            CheckGeometry(form, page, size.Width + "px expanded " + nestedName);
                            CheckOptionalButtons(form, page, bodies[0], size.Width + "px expanded " + nestedName);
                            AssertNoWork("Expand " + nestedName);
                            Disclosure(form, "Hide " + nestedName).PerformClick(); Pump(form);
                        }
                        var visualActions = Descendants(bodies[0]).OfType<Button>().ToDictionary(button => button, button => button.Enabled);
                        foreach (var enabled in new[] { false, true })
                        {
                            foreach (var action in visualActions.Keys) action.Enabled = enabled;
                            Pump(form);
                            CheckOptionalButtons(form, page, bodies[0], size.Width + "px " + tool + " enabled=" + enabled);
                        }
                        foreach (var action in visualActions) action.Key.Enabled = action.Value;
                        if (size.Width == 780)
                        {
                            foreach (var action in visualActions.Keys.Where(button => button.Visible &&
                                button is not DarkButton { VisualStyle: HubButtonStyle.Disclosure }))
                            {
                                // Exercise the click guard even where a sample prerequisite disables it.
                                action.Enabled = true;
                                action.PerformClick(); Pump(form);
                                AssertNoWork("Preview click: " + action.Text);
                                action.Enabled = visualActions[action];
                            }
                        }
                        if (tool == "AMD setup")
                        {
                            var amdAction = Field<Button>(form, "_setupAmdButton");
                            var caption = amdAction.Text;
                            amdAction.Text = $"Verify / repair ROCm {HubRocmRuntime.InstallVersion}"; Pump(form);
                            CheckOptionalButtons(form, page, bodies[0], size.Width + "px long AMD caption");
                            amdAction.Text = caption; Pump(form);
                        }
                        if (tool == "PC runtime repair and recovery")
                        {
                            var copies = Field<ComboBox>(form, "_runtimeRecoveryCopies");
                            var restore = Field<Button>(form, "_restoreRuntimeButton");
                            var expandedHeight = bodies[0].Height;
                            var hiddenHeight = copies.Height + copies.Margin.Vertical + restore.Height + restore.Margin.Vertical;
                            copies.Visible = restore.Visible = false; Pump(form);
                            Check(bodies[0].Height <= expandedHeight - hiddenHeight,
                                size.Width + "px no saved runtime: hidden recovery controls left reserved gaps.");
                            CheckGeometry(form, page, size.Width + "px no saved runtime");
                            CheckOptionalButtons(form, page, bodies[0], size.Width + "px no saved runtime");
                            copies.Visible = restore.Visible = true; Pump(form);
                            CheckGeometry(form, page, size.Width + "px saved runtime controls restored");
                            CheckOptionalButtons(form, page, bodies[0], size.Width + "px saved runtime controls restored");
                        }
                        if (tool == "hands setup and uninstall")
                        {
                            var names = Descendants(bodies[0]).OfType<Label>().Where(label => label.Text is "Quest" or "Singularity").ToArray();
                            Check(names.Length == 2 && names.All(label => label.Width >= TextRenderer.MeasureText(
                                label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width),
                                "Hands prerequisites wrapped their short field names into fragments.");
                        }
                        if (tool == "gaze setup")
                        {
                            Check(!Field<Button>(form, "_setupGazeButton").Enabled && !Field<Button>(form, "_inspectGazeButton").Enabled,
                                "Optional category navigation bypassed the Magisk gate.");
                        }
                    }
                }
            }
            foreach (var (category, tool) in optionalGroups.SelectMany(group => group.Item2.Select(tool => (group.Item1, tool))))
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
                CheckOptionalButtons(form, page, optional.Parent!, "Restored optional tool " + tool);
            }
            Disclosure(form, "Hide optional tools and recovery").PerformClick(); Pump(form);
            Check(!Descendants(page).Any(control => control.Visible && Equals(control.Tag, "optional-tool-body")),
                "Closing optional tools left a tool body visible.");
            AssertNoWork("Close optional tools");

            form.ApplyPreviewScenario("steamvr-startup", "VirtualDesktop"); Pump(form);
            Check(startup.Checked && startupStatus.Text.Contains("On") && startupStatus.Text.Contains("sample"),
                "The startup preview does not show the opted-in SteamVR state.");
            form.Size = new Size(780, 580);
            startup.Enabled = false;
            startupStatus.Text = "Open Companion from its extracted app folder to set up SteamVR launch.";
            Pump(form);
            var startupTop = page.PointToClient(startup.Parent!.PointToScreen(Point.Empty)).Y - page.AutoScrollPosition.Y;
            page.AutoScrollPosition = new Point(0, Math.Max(0, startupTop - 12));
            Pump(form);
            var compactViewport = page.RectangleToScreen(page.ClientRectangle);
            Check(compactViewport.Contains(startup.RectangleToScreen(startup.ClientRectangle)) &&
                compactViewport.Contains(startupStatus.RectangleToScreen(startupStatus.ClientRectangle)) &&
                !page.HorizontalScroll.Visible, $"Compact setup clipped the disabled startup control or its explanation. View={compactViewport}, toggle={startup.RectangleToScreen(startup.ClientRectangle)}, status={startupStatus.RectangleToScreen(startupStatus.ClientRectangle)}.");
            startup.Enabled = true;
            stepButtons[2].PerformClick();
            facialTab.PerformClick();
            form.ApplyPreviewScenario("direct-unavailable", "VirtualDesktop"); Pump(form);
            var offerBackup = Field<Button>(form, "_useBackupOutputButton");
            Check(offerBackup.Visible && offerBackup.Enabled && route.SelectedIndex == 0,
                "A direct connection failure did not offer an explicit switch to backup.");
            offerBackup.PerformClick(); Pump(form);
            Check(page.Visible && route.Visible && route.SelectedIndex == 1 && installBackup.Visible,
                "The backup action did not open the selected output route.");
            direct.PerformClick(); Pump(form);
            form.ApplyPreviewScenario("direct-unavailable", "VirtualDesktop"); Pump(form);
            Check(direct.Checked && !backup.Checked && backup.Visible && backup.Enabled && route.SelectedIndex == 0,
                "A failed direct check hid the available backup choice or switched automatically.");
            backup.PerformClick(); Pump(form);
            Check(!direct.Checked && backup.Checked && route.SelectedIndex == 1 && installBackup.Visible,
                "Clicking the backup segment did not select the backup route.");
            void PressArrow(RadioButton current, Keys key, RadioButton expected)
            {
                current.Focus();
                var message = Message.Create(current.Handle, 0x0100, (IntPtr)key, IntPtr.Zero);
                bool handled = current.PreProcessMessage(ref message);
                Pump(form);
                Check(handled && expected.Focused && expected.Checked && expected.TabStop && !current.Checked && !current.TabStop,
                    $"The {key} arrow did not move the keyboard focus and active route together.");
            }
            PressArrow(backup, Keys.Left, direct);
            PressArrow(direct, Keys.Right, backup);
            foreach (var size in new[] { new Size(1280, 900), new Size(780, 580) })
            {
                form.Size = size;
                form.ApplyPreviewScenario("setup-backup", "VirtualDesktop"); Pump(form);
                page.AutoScrollPosition = Point.Empty; Pump(form);
                Check(route.SelectedIndex == 1 && installBackup.Visible && backup.Checked && !direct.Checked &&
                    checkVrchat.Visible && checkVrchat.Text == "Check backup connection" &&
                    vrchatSetupStatus.Text.Contains("Backup module"), "Backup setup exposed the wrong route actions.");
                var viewport = page.RectangleToScreen(page.ClientRectangle);
                Check(viewport.Contains(installBackup.RectangleToScreen(installBackup.ClientRectangle)),
                    $"{size.Width}px setup hides the backup installer. View={viewport}, button={installBackup.RectangleToScreen(installBackup.ClientRectangle)}, scroll={page.AutoScrollPosition}.");
                var installBounds = installBackup.RectangleToScreen(installBackup.ClientRectangle);
                var checkBounds = checkVrchat.RectangleToScreen(checkVrchat.ClientRectangle);
                Check(viewport.Contains(checkBounds) && installBounds.Y == checkBounds.Y && installBounds.Right < checkBounds.Left,
                    size.Width + "px setup separated the backup install and connection check.");
                CheckGeometry(form, page, size.Width + "px backup output");
                Capture(form, "backup-" + size.Width);
                direct.PerformClick(); Pump(form);
                Check(route.SelectedIndex == 0 && direct.Checked && !backup.Checked && !installBackup.Visible &&
                    checkVrchat.Visible && checkVrchat.Text == "Check VRChat connection",
                    "Returning to direct output failed to restore direct-only actions.");
                CheckGeometry(form, page, size.Width + "px direct output");
            }
        }
        Check(PreferenceFingerprint(root) == beforePreferences,
            "Preview navigation changed preferences or installed VRCFaceTracking module files.");
        Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Preview disposal wrote app state.");
        Console.WriteLine("PASS: " + _checks + " isolated setup navigation, state, status and layout checks.");
    }
}
