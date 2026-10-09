using System.Reflection;
using Qpro.Shared;
using QproFaceTracking.Hub;

internal static class LifecycleTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    static void Main()
    {
        try { Run(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Environment.ExitCode = 1;
        }
    }

    static void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // Preview construction avoids ADB, runtime detection, timers and preference writes.
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "qpro-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var fixtureConfig = Path.Combine(fixtureRoot, "config");
        Directory.CreateDirectory(fixtureConfig);
        var gazePreference = Path.Combine(fixtureConfig, "independent-gaze.txt");
        foreach (var value in new[] { "", "off", "false", "true", "1", "unexpected", "on", " ON \r\n" })
        {
            File.WriteAllText(gazePreference, value);
            var environment = new HubEnvironment(fixtureRoot);
            var expected = value.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            if (environment.IndependentGazeEnabled != expected)
                throw new Exception("Saved gaze preference enabled the firmware feature without an explicit on value.");
            Console.WriteLine("PASS explicit gaze preference: " + (value.Trim().Length == 0 ? "empty" : value.Trim()));
        }
        File.Delete(gazePreference);
        if (new HubEnvironment(fixtureRoot).IndependentGazeEnabled)
            throw new Exception("A fresh Hub enabled independent gaze.");
        using var form = new HubForm(fixtureRoot, rememberLaunch: false, previewOnly: true);
        object? Call(string name, params object[] args) => typeof(HubForm).GetMethod(name, Private)!.Invoke(form, args);
        void Set(string name, bool value) => typeof(HubForm).GetField(name, Private)!.SetValue(form, value);
        bool Pending() => (bool)typeof(HubForm).GetProperty("TrackingShutdownPending", Private)!.GetValue(form)!;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        var gaze = (CheckBox)typeof(HubForm).GetField("_gaze", Private)!.GetValue(form)!;
        var compatibility = (Label)typeof(HubForm).GetField("_compatibilitySummary", Private)!.GetValue(form)!;
        var setupGaze = (Label)typeof(HubForm).GetField("_setupGazeStatus", Private)!.GetValue(form)!;
        Check(!gaze.Checked, "Magisk preview leaves Hub gaze off");
        Check(compatibility.Text.Contains("Magisk") && compatibility.Text.Contains("skipped"),
            "Magisk compatibility result explicitly skips Hub gaze setup");
        Check(setupGaze.Text.Contains("Skip Hub gaze setup"), "Magisk setup preview does not request a gaze check");
        // Exercise the actual status presenter and animation separately. A pulse
        // must not rewrite labels or perform the full setup readiness pass.
        Call("UpdateGazeSetupStatus");
        var confirmedGazeText = setupGaze.Text;
        var gazeTextChanges = 0;
        setupGaze.TextChanged += (_, _) => gazeTextChanges++;
        using var pulseButton = new DarkButton { Visible = true, Enabled = true };
        Call("StyleSetupButton", pulseButton, "Offline pending step", false, true);
        var pulseTextChanges = 0;
        pulseButton.TextChanged += (_, _) => pulseTextChanges++;
        for (var pulse = 0; pulse < 8; pulse++)
        {
            Call("PulseSetupAttention");
            Call("UpdateGazeSetupStatus");
        }
        Check(gazeTextChanges == 0 && setupGaze.Text == confirmedGazeText,
            "repeated gaze updates and pulses retain one Magisk status without Complete/Waiting transitions");
        Check(pulseTextChanges == 0, "setup animation changes borders without rewriting button captions");
        var gazeStatus = (Label)typeof(HubForm).GetField("_gazeStatus", Private)!.GetValue(form)!;
        Set("_compatibilityChecking", true);
        typeof(HubForm).GetField("_gazeInspectionResult", Private)!.SetValue(form, null);
        Call("UpdateGazeSetupStatus");
        Check(setupGaze.Text == confirmedGazeText && gazeTextChanges == 0,
            "a pending check retains the last completed Magisk observation");
        Set("_compatibilityChecking", false);
        gaze.Checked = true;
        Call("UpdateGazeSetupStatus");
        Check(setupGaze.Text.Contains("turn Hub gaze off"), "selecting Hub gaze still warns about the Magisk conflict");
        gaze.Checked = false;
        Call("UpdateGazeSetupStatus");
        Check(setupGaze.Text == confirmedGazeText, "unchecking Hub gaze restores the stable Magisk status");
        form.ApplyPreviewScenario("unsupported", "VirtualDesktop");
        Call("UpdateGazeSetupStatus");
        Check(setupGaze.Text.Contains("unavailable") && gazeStatus.Text.Contains("unavailable"),
            "an unsupported Hub engine remains unavailable during status updates");

        // Synthetic verified targets exercise display scoping only. No probe or
        // headset action runs, and no serial is written to a user config file.
        var hubEnvironment = (HubEnvironment)typeof(HubForm).GetField("_environment", Private)!.GetValue(form)!;
        var usb = (HubUsbConnection)typeof(HubEnvironment).GetField("_usbConnection", Private)!.GetValue(hubEnvironment)!;
        var verifiedSerial = typeof(HubUsbConnection).GetField("_verifiedSerial", Private)!;
        verifiedSerial.SetValue(usb, "synthetic-gaze-usb-a");
        Call("RememberGazeInspection", new HubGazeInspection("fixture-build", "false", false,
            ["fixture-independent-gaze"], []));
        var scopedMagiskText = setupGaze.Text;
        verifiedSerial.SetValue(usb, null);
        Call("UpdateGazeSetupStatus");
        Check(setupGaze.Text == scopedMagiskText && setupGaze.Text.Contains("Last check"),
            "a transient USB probe gap retains the labeled last check");
        verifiedSerial.SetValue(usb, "synthetic-gaze-usb-b");
        Call("UpdateGazeSetupStatus");
        Check(!setupGaze.Text.Contains("Last check"), "a different USB headset cannot inherit confirmed Magisk status");
        verifiedSerial.SetValue(usb, null);
        Call("RememberGazeInspection", new HubGazeInspection("fixture-build", "false", false,
            ["fixture-independent-gaze"], []));
        verifiedSerial.SetValue(usb, "synthetic-gaze-usb-c");
        Call("UpdateGazeSetupStatus");
        Check(!setupGaze.Text.Contains("Last check"), "an unscoped observation does not transfer to a newly verified headset");
        Call("RememberGazeInspection", new HubGazeInspection("fixture-build", "false", false,
            ["fixture-independent-gaze"], []));
        hubEnvironment.SelectConnection(true);
        Call("UpdateGazeSetupStatus");
        Check(!setupGaze.Text.Contains("Last check"), "changing USB to wireless clears the prior gaze observation");
        hubEnvironment.SelectConnection(false);
        Call("RememberGazeInspection", new HubGazeInspection("fixture-build", "true", true, [], []));
        Check(setupGaze.Text.Contains("needs attention"), "a recorded Qpro session retains its recovery warning");
        Call("CompleteGazeStatusRecovery", false);
        Check(setupGaze.Text.Contains("needs attention"), "an unconfirmed recovery cannot clear the last gaze warning");
        Call("CompleteGazeStatusRecovery", true);
        Check(!setupGaze.Text.Contains("needs attention") && gazeStatus.Text.Contains("Hub gaze off") &&
            !gazeStatus.Text.Contains("stock", StringComparison.OrdinalIgnoreCase),
            "confirmed recovery clears stale inspection without claiming a fresh stock-tracking check");
        pulseButton.Visible = false;
        var hiddenOutline = pulseButton.OutlineColor;
        Call("PulseSetupAttention");
        Check(pulseButton.OutlineColor == hiddenOutline, "hidden setup actions are not animated");
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        Check(!Pending(), "idle permits shutdown");
        foreach (var state in new[] { "_starting", "_stopping" })
        {
            Set(state, true);
            Check(Pending(), state + " prevents shutdown");
            Check((bool)Call("UtilityActionIsBusy", false)!, state + " rejects setup/capture entry");
            Check(!(bool)Call("TryBeginSetupProgress", "Offline regression")!, state + " cannot acquire setup");
            var utility = (Task<bool>)Call("RunUtilityAsync", "Offline regression", "nonexistent-must-not-launch.ps1", Array.Empty<string>())!;
            Check(utility.IsCompletedSuccessfully && !utility.Result, state + " rejects direct utility before launching script");
            Call("UpdateControlState");
            foreach (var name in new[] { "_setupRuntimeButton", "_setupGazeButton", "_connectWirelessButton", "_pairWirelessButton", "_reconnectUsbButton", "_forgetUsbButton" })
                Check(!((Control)typeof(HubForm).GetField(name, Private)!.GetValue(form)!).Enabled, state + " disables " + name);
            Set(state, false);
        }
        Set("_trackingCleanupPending", true);
        Check(Pending(), "exited children with undrained output still prevent shutdown");
        Call("UpdateControlState");
        Check(((Control)typeof(HubForm).GetField("_stop", Private)!.GetValue(form)!).Enabled, "pending output cleanup remains retryable");
        Set("_trackingCleanupPending", false);
        Check(!Pending(), "confirmed cleanup permits shutdown");
        foreach (var state in new[] { "_trackingCleanupPending", "_gazeRecoveryRunning", "_closingInProgress" })
        {
            Set(state, true);
            Call("UpdateControlState");
            foreach (var name in new[] { "_installHandsButton", "_removeHandsButton", "_checkHandsButton" })
                Check(!((Control)typeof(HubForm).GetField(name, Private)!.GetValue(form)!).Enabled, state + " disables " + name);
            Set(state, false);
        }

        string sessionName = @"Local\QproHubLifecycleRegression." + Guid.NewGuid().ToString("N");
        Check(!CheekTrackingSession.IsActive(sessionName), "isolated cheek session starts inactive");
        string configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "config");
        string[] preferenceNames = ["cheek-puff-mode.txt", "cheek-puff-last-style.txt", "cheek-suck-mode.txt", "cheek-suck-last-style.txt"];
        var beforePreferences = preferenceNames.ToDictionary(name => name, name => ReadPreference(Path.Combine(configRoot, name)));
        var puff = (CheckBox)typeof(HubForm).GetField("_individualCheekPuff", Private)!.GetValue(form)!;
        var suck = (CheckBox)typeof(HubForm).GetField("_individualCheekSuck", Private)!.GetValue(form)!;
        bool savedPuff = puff.Checked, savedSuck = suck.Checked;

        using var session = new CheekTrackingSession(sessionName);
        typeof(HubForm).GetField("_cheekTrackingSession", Private)!.SetValue(form, session);
        Set("_nativeCheekOnlySession", true);
        Check(Pending(), "cheek-only session counts as live tracking without child processes");
        Call("UpdateControlState");
        Check(((Control)typeof(HubForm).GetField("_stop", Private)!.GetValue(form)!).Enabled, "cheek-only session enables Stop");
        foreach (string field in new[] { "_start", "_connectionMode", "_trackingSourceSetup", "_trackingSourceLive", "_setupRuntimeButton", "_recordCameraCheeks", "_trainCameraCheeks", "_reconnectUsbButton", "_forgetUsbButton" })
            Check(!((Control)typeof(HubForm).GetField(field, Private)!.GetValue(form)!).Enabled, "cheek-only session disables " + field);

        var stopped = (Task)Call("StopTrackingAsync")!;
        Check(!CheekTrackingSession.IsActive(sessionName), "Stop releases cheek permission before asynchronous cleanup");
        Check(stopped.IsCompletedSuccessfully, "cheek-only Stop completes without a worker or headset action");
        Check(!Pending(), "cheek-only Stop clears live tracking state");
        Check(((Control)typeof(HubForm).GetField("_start", Private)!.GetValue(form)!).Enabled, "Start is enabled after cheek-only Stop");
        Check(!((Control)typeof(HubForm).GetField("_stop", Private)!.GetValue(form)!).Enabled, "Stop is disabled after cheek-only Stop");
        Check(puff.Checked == savedPuff && suck.Checked == savedSuck, "Stop retains selected cheek options");
        foreach (string name in preferenceNames)
            Check(ReadPreference(Path.Combine(configRoot, name)) == beforePreferences[name], "Stop does not rewrite " + name);

        string closingName = @"Local\QproHubLifecycleRegression." + Guid.NewGuid().ToString("N");
        using var closingSession = new CheekTrackingSession(closingName);
        typeof(HubForm).GetField("_cheekTrackingSession", Private)!.SetValue(form, closingSession);
        form.Dispose();
        Check(!CheekTrackingSession.IsActive(closingName), "disposing the Hub releases its cheek session");
        Console.WriteLine("PASS: Hub lifecycle and cheek-only session checks completed without live processes.");
    }

    static string? ReadPreference(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
}
