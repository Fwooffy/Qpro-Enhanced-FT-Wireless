using System.Reflection;
using System.Text;
using QproFaceTracking.Hub;

internal static class MagiskGateTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);
    private static object? Call(HubForm form, string method, params object[] args) =>
        typeof(HubForm).GetMethod(method, Private)!.Invoke(form, args);

    [STAThread]
    private static void Main()
    {
        try
        {
            ScanParser();
            ContextState();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            UiGate();
            StartupScan();
            Console.WriteLine($"PASS: {_checks} Magisk gaze checks; bounded read-only scan, cache and production control/persistence gates.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Environment.ExitCode = 1;
        }
    }

    private static void ScanParser()
    {
        Check(HubMagiskGazeScan.TryParse(HubMagiskGazeScan.Complete + "\n", out var empty) && empty.Count == 0,
            "A complete negative scan was rejected.");
        Check(HubMagiskGazeScan.TryParse(HubMagiskGazeScan.ModulePrefix + "questpro_independent_gaze\r\n" +
            HubMagiskGazeScan.Complete, out var modules) && modules.SequenceEqual(["questpro_independent_gaze"]),
            "A complete positive scan was rejected.");
        foreach (var value in new[] { "", "error", HubMagiskGazeScan.ModulePrefix + "questpro_independent_gaze",
            HubMagiskGazeScan.Complete + "\nunknown", HubMagiskGazeScan.Complete + "\n" + HubMagiskGazeScan.Complete,
            HubMagiskGazeScan.ModulePrefix + "../../other\n" + HubMagiskGazeScan.Complete,
            HubMagiskGazeScan.ModulePrefix + "x\n" + HubMagiskGazeScan.ModulePrefix + "x\n" + HubMagiskGazeScan.Complete,
            new string('x', 32769), string.Concat(Enumerable.Repeat(HubMagiskGazeScan.ModulePrefix + "a\n", 129)) + HubMagiskGazeScan.Complete })
            Check(!HubMagiskGazeScan.TryParse(value, out _), "An incomplete, oversized or malformed scan became proof of a gaze method.");
        var request = HubMagiskGazeScan.RootShellRequest;
        var encoded = request["printf %s ".Length..request.IndexOf(" | base64", StringComparison.Ordinal)];
        Check(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) == HubMagiskGazeScan.Script.Replace("\r\n", "\n") + "\n",
            "The fixed root scan did not survive the existing base64 transport.");
        Check(request.EndsWith(" | base64 -d | su -c sh", StringComparison.Ordinal), "The read-only script transport changed unexpectedly.");
        Check(HubMagiskGazeScan.Script.Contains("-le 128") && HubMagiskGazeScan.Script.Contains("-le 65536") &&
            !HubMagiskGazeScan.Script.Contains("eval") && !HubMagiskGazeScan.Script.Contains("source "),
            "The module scan lost its bounded data-only inspection.");
    }

    private static void ContextState()
    {
        var state = new HubMagiskGazeState();
        Check(state.BeginScan(null, false) is null && !state.Active, "An unverified target could trigger a root scan.");
        var first = state.BeginScan("quest-a", false)!.Value;
        Check(state.AcceptScan(first, "quest-a", false, true) && state.Active, "A current positive scan was not accepted.");
        for (var tick = 0; tick < 20; tick++)
            Check(state.BeginScan("quest-a", false) is null && state.Active, "A timer tick repeated a completed module scan.");
        state.SelectContext(null, false);
        Check(state.Active && state.BeginScan(null, false) is null, "A transient USB gap cleared the positive module result.");
        state.RetryScan();
        var retry = state.BeginScan("quest-a", false)!.Value;
        Check(state.Active && state.BeginScan("quest-a", false) is null, "A retry or its failure cleared the last positive result.");
        Check(state.AcceptScan(retry, "quest-a", false, false) && !state.Active, "A complete negative retry could not clear the gate.");
        state.Observe("quest-a", false, true);
        state.SelectContext("quest-b", false);
        Check(!state.Active && !state.AcceptScan(first, "quest-a", false, true) && !state.Active,
            "A different headset inherited a result or a late old-target scan reset its context.");
        Check(state.BeginScan("quest-b", false) is not null, "A different verified headset was not scanned.");
        state.Observe("quest-b", false, true);
        state.SelectContext("quest-b:5555", true);
        Check(!state.Active && state.BeginScan("quest-b:5555", true) is not null, "A transport change reused an unverified module result.");
        state.Observe(null, false, true);
        state.SelectContext("quest-c", false);
        Check(!state.Active, "An unscoped observation transferred to a new verified headset.");
        var lostTargetScan = state.BeginScan("quest-c", false)!.Value;
        state.SelectContext(null, false);
        state.RetryAfterTargetLoss(lostTargetScan, "quest-c", false);
        var restoredTargetScan = state.BeginScan("quest-c", false);
        Check(restoredTargetScan is not null && !state.AcceptScan(lostTargetScan, "quest-c", false, true),
            "Discarding a scan during a transient target loss permanently consumed its attempt or accepted its late result.");
        state.RetryAfterTargetLoss(lostTargetScan, "quest-c", false);
        Check(state.BeginScan("quest-c", false) is null && state.AcceptScan(restoredTargetScan!.Value, "quest-c", false, true),
            "An old lost-target result cancelled the newer retry.");
        state.SelectContext("quest-d", false);
        state.RetryAfterTargetLoss(restoredTargetScan!.Value, "quest-c", false);
        Check(!state.Active && state.BeginScan("quest-d", false) is not null,
            "A target-loss retry from the previous headset altered the new target context.");
    }

    private static void UiGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "qpro-magisk-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var preference = Path.Combine(root, "config", "independent-gaze.txt");
        File.WriteAllText(preference, "on");
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
        // Construction stays in preview mode, so no timers, ADB, global writes,
        // apps or hardware probes run. Only the isolated preference write below
        // exercises the production event and gate after construction.
        Set(form, "_previewOnly", false);
        var gaze = Field<CheckBox>(form, "_gaze");
        Check(gaze.Checked, "The saved true preference fixture was not loaded.");
        var active = new HubGazeInspection("fixture", "false", false, ["questpro_independent_gaze"], []);
        Call(form, "RememberGazeInspection", active);
        Check(!gaze.Checked && !gaze.Enabled && File.ReadAllText(preference) == "off", "Confirmed Magisk did not clear and disable the saved Hub selection.");
        Check(gaze.Text.Contains("provided by Magisk") && gaze.AccessibleDescription?.Contains("pupil and eyebrow") == true,
            "The disabled eye option did not explain the available gaze method and independent controls.");
        Check(!Field<Control>(form, "_setupGazeButton").Enabled && !Field<Control>(form, "_inspectGazeButton").Enabled,
            "Confirmed Magisk still required or allowed Hub Check/Prepare actions.");
        var writtenAt = File.GetLastWriteTimeUtc(preference);
        var status = Field<Label>(form, "_setupGazeStatus");
        var statusChanges = 0;
        status.TextChanged += (_, _) => statusChanges++;
        for (var i = 0; i < 20; i++) Call(form, "UpdateGazeSetupStatus");
        Check(statusChanges == 0 && File.GetLastWriteTimeUtc(preference) == writtenAt,
            "Repeated refreshes flickered the status or rewrote the off preference.");
        Set(form, "_previewOnly", true);
        var snapshot = new HubForm.HubStatusSnapshot(true, true,
            new HubModuleInspection(HubModuleState.Current, "Fixture module", "Fixture current module", "fixture"),
            false, true, false, null, false, false);
        Call(form, "UpdateControlStateFromSnapshot", snapshot);
        int availabilityChanges = 0;
        foreach (var control in new[] { gaze, Field<Control>(form, "_inspectGazeButton"), Field<Control>(form, "_setupGazeButton") })
            control.EnabledChanged += (_, _) => availabilityChanges++;
        for (var tick = 0; tick < 20; tick++) Call(form, "UpdateControlStateFromSnapshot", snapshot);
        Check(availabilityChanges == 0 && !gaze.Enabled,
            "A full control refresh briefly enabled a Magisk-blocked control before disabling it again.");
        Set(form, "_previewOnly", false);
        gaze.Checked = true;
        Check(!gaze.Checked && File.ReadAllText(preference) == "off", "A direct checked change bypassed the Magisk selection gate.");
        var prepare = (Task)Call(form, "PrepareGazeAsync")!;
        Check(prepare.IsCompletedSuccessfully && Field<Label>(form, "_actionTitle").Text.Contains("provided by Magisk"),
            "Direct Prepare gaze did not return before accessing ADB under the Magisk gate.");
        Call(form, "CompleteGazeStatusRecovery", true);
        Check(!gaze.Enabled && status.Text.Contains("Magisk"), "Qpro-only recovery incorrectly cleared the Magisk gate.");

        // A running selection must remain a truthful indication until normal
        // Stop cleanup completes; discovery itself never restores headset state.
        Call(form, "RememberGazeInspection", new HubGazeInspection("fixture", "false", false, [], []));
        gaze.Checked = true;
        Set(form, "_starting", true);
        Call(form, "RememberGazeInspection", active);
        Check(gaze.Checked && !gaze.Enabled && Field<Label>(form, "_actionNext").Text.Contains("Stop tracking"),
            "A newly discovered conflict silently changed an in-flight gaze session or omitted its cleanup instruction.");
        Check(File.ReadAllText(preference) == "on", "Discovery changed the saved running session before Stop cleanup.");
        Set(form, "_starting", false);
        Call(form, "UpdateGazeSetupStatus");
        Check(!gaze.Checked && File.ReadAllText(preference) == "off", "The confirmed conflict was not cleared after the session became idle.");
        Call(form, "RememberGazeInspection", new HubGazeInspection("fixture", "false", false, [], []));
        Check(gaze.Enabled && !gaze.Checked && !gaze.Text.Contains("Magisk"), "A completed negative check did not restore optional Hub gaze without enabling it.");

        // Preview scenarios use exactly the same report presenter and never
        // write preferences; pupil/tongue options stay available independently.
        Set(form, "_previewOnly", true);
        foreach (var source in new[] { "VirtualDesktop", "SteamLink" })
        {
            form.ApplyPreviewScenario("magisk", source);
            Check(!gaze.Checked && !gaze.Enabled && gaze.Text.Contains("Magisk"), "A Magisk preview bypassed the production disabled state.");
            Check(Field<CheckBox>(form, "_pupil").Enabled && Field<CheckBox>(form, "_tongue").Enabled,
                "The gaze gate disabled separate pupil or tongue features.");
        }
    }

    private static void StartupScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "qpro-magisk-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "platform-tools"));
        File.WriteAllBytes(Path.Combine(root, "platform-tools", "adb.exe"), []);
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
        Set(form, "_previewOnly", false);
        var environment = Field<HubEnvironment>(form, "_environment");
        var usb = Field<HubUsbConnection>(environment, "_usbConnection");
        Set(usb, "_verifiedSerial", "MAGISK_SCAN_FIXTURE");
        int calls = 0;
        var reply = (true, 0, HubMagiskGazeScan.ModulePrefix + "questpro_independent_gaze\n" + HubMagiskGazeScan.Complete);
        TaskCompletionSource<(bool, int, string)>? pendingReply = null;
        Set(environment, "_adbSession", new HubAdbSession((_, args, timeout, _) =>
        {
            calls++;
            Check(timeout == 8 && args.SequenceEqual(["-s", "MAGISK_SCAN_FIXTURE", "shell", HubMagiskGazeScan.RootShellRequest]),
                "Startup issued anything other than the bounded fixed metadata read for its verified target.");
            return pendingReply?.Task ?? Task.FromResult(reply);
        }));
        void Complete(Task task)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (!task.IsCompleted && Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            Check(task.IsCompleted, "The owned fake scan did not complete.");
            task.GetAwaiter().GetResult();
        }
        void Scan(bool connected = true) => Complete((Task)Call(form, "DetectMagiskGazeOnceAsync", connected)!);
        var gaze = Field<CheckBox>(form, "_gaze");
        Scan();
        Check(calls == 1 && !gaze.Enabled && gaze.Text.Contains("provided by Magisk"),
            "Automatic discovery did not disable Hub gaze without a manual Check gaze setup.");
        for (var tick = 0; tick < 10; tick++) Scan();
        Check(calls == 1, "Repeated status refreshes ran another metadata scan.");
        Set(usb, "_verifiedSerial", null);
        Scan(false);
        Call(form, "UpdateGazeSetupStatus");
        Check(!gaze.Enabled && calls == 1, "A disconnected USB status lost or retried the confirmed module method.");
        Set(usb, "_verifiedSerial", "MAGISK_SCAN_FIXTURE");
        Field<HubMagiskGazeState>(form, "_magiskGazeState").RetryScan();
        reply = (false, -1, "fixture root check timed out");
        Scan();
        Check(calls == 2 && !gaze.Enabled, "An incomplete retry re-enabled conflicting Hub gaze.");
        Scan();
        Check(calls == 2, "A failed scan caused a retry loop on subsequent timer refreshes.");
        Field<HubMagiskGazeState>(form, "_magiskGazeState").RetryScan();
        reply = (true, 0, HubMagiskGazeScan.Complete);
        Scan();
        Check(calls == 3 && gaze.Enabled && !gaze.Checked, "A completed negative retry did not restore optional, unchecked Hub gaze.");
        var state = Field<HubMagiskGazeState>(form, "_magiskGazeState");
        state.Observe("MAGISK_SCAN_FIXTURE", false, true);
        Call(form, "UpdateGazeSetupStatus");
        state.RetryScan();
        pendingReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = (Task)Call(form, "DetectMagiskGazeOnceAsync", true)!;
        Check(calls == 4 && !delayed.IsCompleted, "The target-loss fixture did not keep a real discovery awaiting its reply.");
        Set(usb, "_verifiedSerial", null);
        pendingReply.SetResult((true, 0, HubMagiskGazeScan.Complete));
        Complete(delayed);
        pendingReply = null;
        Check(!gaze.Enabled && state.Active, "A result returned during target loss cleared confirmed Magisk detection.");
        Scan(false);
        Check(calls == 4, "Target loss itself launched a retry before USB was verified again.");
        Set(usb, "_verifiedSerial", "MAGISK_SCAN_FIXTURE");
        reply = (true, 0, HubMagiskGazeScan.ModulePrefix + "questpro_independent_gaze\n" + HubMagiskGazeScan.Complete);
        Scan();
        Check(calls == 5 && !gaze.Enabled && state.Active,
            "The same headset becoming verified again could not retry its discarded scan exactly once.");
        for (var tick = 0; tick < 5; tick++) Scan();
        Check(calls == 5, "The recovered target kept rescanning after its replacement result completed.");
    }
}
