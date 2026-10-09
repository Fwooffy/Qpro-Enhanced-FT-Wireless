using System.Diagnostics;
using System.Reflection;
using QproFaceTracking.Hub;

// Real child processes reproduce the supervisor's failure stop marker and
// final pipe output. They never load a runtime, ADB, SteamVR or headset code.
internal static class ControllerLifecycleFixtures
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const string FinalCleanup = "CONTROLLER_CLEANUP {\"confirmed\":true,\"restoration\":\"confirmed\",\"reader\":\"stopped\",\"problems\":[]}";

    internal static bool TryRunWorker(string[] args)
    {
        if (args.Length != 4 || args[0] != "--controller-lifecycle-fixture") return false;
        string mode = args[1], marker = args[2], witness = args[3];
        if (mode.StartsWith("failure-", StringComparison.Ordinal))
        {
            // manage.py writes its own marker even on a worker failure.
            File.WriteAllText(marker, "controller stop requested\n");
            string? report = mode[8..] switch
            {
                "confirmed" => FinalCleanup,
                "interim" => "CONTROLLER_CLEANUP {\"restoration\":\"still-running\",\"problems\":[]}",
                "malformed" => "CONTROLLER_CLEANUP {invalid}",
                "failed" => "CONTROLLER_CLEANUP {\"confirmed\":false,\"restoration\":\"unconfirmed\",\"reader\":\"stopped\",\"problems\":[\"Adapter restoration was not confirmed.\"]}",
                _ => null,
            };
            if (report is not null) Console.WriteLine(report);
            // No newline: the actual Exited event can precede this last line.
            Console.Error.Write("CONTROLLER_ERROR {\"message\":\"The controller sensor reader disconnected.\"}");
            Environment.ExitCode = 1;
            return true;
        }
        Task<string>? parentClosed = mode == "controller-wait" ? Task.Run(Console.In.ReadToEnd) : null;
        Console.WriteLine(mode == "camera" ? "TRANSPORT_QUALITY fps=24" : "FIXTURE_READY " + mode);
        Console.Out.Flush();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(marker) && parentClosed?.IsCompleted != true && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        bool stopSeen = File.Exists(marker);
        if (stopSeen || parentClosed?.IsCompleted == true)
        {
            File.WriteAllText(witness, stopSeen ? "stop-marker" : "parent-eof");
            if (mode == "controller-wait") Console.WriteLine(FinalCleanup);
            Console.WriteLine("STOP_REQUESTED fixture=" + mode);
        }
        else Environment.ExitCode = 2;
        return true;
    }

    internal static void Run(string root)
    {
        TestCleanFailure(root);
        TestExplicitStop(root);
        foreach (var result in new[] { "missing", "interim", "malformed", "failed" })
            TestUnconfirmedFailure(root, result);
        TestIndependentStopSignals(root);
        var incomplete = new HubControllerUtilityResult();
        incomplete.Observe(FinalCleanup);
        Check(incomplete.CompleteRestoration(outputDrained: false).IsError,
            "an undrained controller pipe cannot prove restoration even after a final-looking report");
    }

    static void TestCleanFailure(string root)
    {
        using var fixture = new Session(root);
        var camera = fixture.Start("Camera tracking", "camera", fixture.SharedStop, "camera");
        var gaze = fixture.Start("Independent gaze", "gaze", fixture.SharedStop, "gaze");
        fixture.Wait(() => fixture.Get<bool>("_cameraInputReady"), "camera readiness");
        var controller = fixture.Start("Hand/controller input", "failure-confirmed", fixture.ControllerStop, "controller");
        fixture.WaitForController(controller);
        Check(File.Exists(fixture.ControllerStop) && !File.Exists(fixture.SharedStop) && !camera.HasExited && !gaze.HasExited,
            "a failed optional controller writes only its private marker and preserves live camera and gaze workers");
        Check(!fixture.Property<bool>("ControllerCleanupUnconfirmed") && !fixture.Property<bool>("ControllerRestartBlocked"),
            "confirmed final restoration permits future controller use despite operational exit 1");
        var status = fixture.Get<Label>("_runStatus").Text;
        Check(status.Contains("Camera input received") && status.Contains("controller input off") && !status.Contains("thumb-rest"),
            "optional controller exit keeps camera readiness without waiting forever for controller input");
        Application.DoEvents();
        Check(fixture.Get<RichTextBox>("_log").Text.Contains("controller sensor reader disconnected"),
            "final controller stderr without a newline survives the bounded exit drain");
        fixture.Stop();
        Check(camera.HasExited && gaze.HasExited && fixture.SawStop("camera") && fixture.SawStop("gaze"),
            "explicit Stop still stops both unrelated camera and gaze fixtures");
    }

    static void TestExplicitStop(string root)
    {
        using var fixture = new Session(root);
        var camera = fixture.Start("Camera tracking", "camera", fixture.SharedStop, "camera");
        var controller = fixture.Start("Hand/controller input", "controller-wait", fixture.ControllerStop, "controller");
        fixture.Wait(() => fixture.Get<bool>("_cameraInputReady"), "camera readiness");
        fixture.Stop();
        Check(camera.HasExited && controller.HasExited && fixture.SawStop("camera") && fixture.SawStop("controller"),
            "explicit Stop signals the camera marker and controller private marker or parent EOF");
        Check(!fixture.Property<bool>("ControllerCleanupUnconfirmed") && !fixture.Property<bool>("TrackingShutdownPending"),
            "requested Stop drains confirmed controller cleanup without an unexpected-failure latch");
        Check(!fixture.Get<Label>("_runStatus").Text.Contains("attention"),
            "confirmed requested Stop retains the ordinary idle status");
    }

    static void TestUnconfirmedFailure(string root, string result)
    {
        using var fixture = new Session(root);
        var camera = fixture.Start("Camera tracking", "camera", fixture.SharedStop, "camera");
        fixture.Wait(() => fixture.Get<bool>("_cameraInputReady"), "camera readiness");
        var controller = fixture.Start("Hand/controller input", "failure-" + result, fixture.ControllerStop, "controller");
        fixture.WaitForController(controller);
        Check(!camera.HasExited && !File.Exists(fixture.SharedStop) && fixture.Property<bool>("ControllerRestartBlocked"),
            result + " controller cleanup gates controller restart while the camera keeps running");
        Check(fixture.Get<Label>("_runStatus").Text.Contains("controller cleanup needs attention"),
            result + " controller uncertainty is visible alongside active camera status");
        fixture.Stop();
        Check(!fixture.Property<bool>("TrackingShutdownPending") && fixture.Property<bool>("ControllerCleanupUnconfirmed") &&
            File.Exists(fixture.ControllerStop) && !fixture.Get<Control>("_start").Enabled,
            result + " cleanup stays controller-only after Stop without a global shutdown deadlock");
        Application.DoEvents();
        string log = fixture.Get<RichTextBox>("_log").Text;
        Check(fixture.Get<Label>("_runStatus").Text.Contains("controller cleanup needs attention") &&
            log.Contains("Controller restoration remains unconfirmed") && !log.Contains("Qpro live overrides stopped;"),
            result + " Stop summary does not report generic success while controller restoration is unconfirmed");
        fixture.Call("ShowControllerFeedback", new HubControllerFeedback(ControllerFeedbackState.Checked,
            "Hand/controller prechecks passed", "A read-only check passed.", "Start optional input.", false));
        fixture.Call("ShowControllerFeedback", new HubControllerFeedback(ControllerFeedbackState.Restored,
            "PC components removed", "A PC-only setup action finished.", "Reopen SteamVR.", false));
        Check(fixture.Property<bool>("ControllerCleanupUnconfirmed") &&
            fixture.Get<Label>("_handsStatus").Text.Contains("Controller restoration needs attention"),
            result + " prechecks and PC-only setup results cannot clear or hide the headset restoration warning");
        fixture.Get<CheckBox>("_controllerTouchpad").Checked = false;
        fixture.Get<CheckBox>("_hybridHands").Checked = false;
        fixture.Call("UpdateControlState");
        Check(fixture.Get<Control>("_start").Enabled && fixture.Get<Control>("_installHandsButton").Enabled,
            result + " unchecking optional controllers allows camera-only Start and PC setup");
        fixture.Call("CompleteControllerInputExit", controller,
            HubControllerFeedback.ParseCleanupLine(FinalCleanup), true);
        Check(fixture.Property<bool>("ControllerCleanupUnconfirmed") &&
            fixture.Get<Label>("_handsStatus").Text.Contains("Controller restoration needs attention"),
            result + " a late successful report cannot erase previously failed restoration evidence");
        fixture.Call("ObserveControllerInput", "Hand/controller input", "HANDS_READY late-packet");
        fixture.Call("ObserveControllerInput", "Hand/controller input", "TOUCHPAD_READY late-packet");
        Check(!fixture.Get<bool>("_handsReady") && !fixture.Get<bool>("_touchpadReady") &&
            fixture.Get<Label>("_handsStatus").Text.Contains("Controller restoration needs attention"),
            result + " late controller readiness cannot reactivate input or hide the cleanup warning");
        fixture.Get<HubEnvironment>("_environment").SetPreviewTrackingSource(true);
        fixture.Call("UpdateControllerInputAvailability");
        Check(fixture.Get<Label>("_handsStatus").Text.Contains("Controller restoration needs attention"),
            result + " switching to Steam Link retains the headset restoration warning");
        if (result == "missing")
        {
            // A delayed UI callback may be suppressed once Stop removed the
            // exited worker. Its completed raw task remains authoritative.
            fixture.Set("_controllerCleanupFailed", false);
            fixture.Call("ShowControllerFeedback", new HubControllerFeedback(ControllerFeedbackState.Checked,
                "Hand/controller prechecks passed", "Read-only prechecks passed.", "Start optional input.", false));
            Check(fixture.Property<bool>("ControllerCleanupUnconfirmed") &&
                fixture.Get<Label>("_handsStatus").Text.Contains("Controller restoration needs attention"),
                "a completed failed restoration task keeps warning priority even without its UI latch callback");
        }
    }

    static void TestIndependentStopSignals(string root)
    {
        using var fixture = new Session(root);
        using (var locked = File.Open(fixture.SharedStop, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            bool failed = false;
            try { fixture.Call("RequestTrackingStop"); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { failed = true; }
            Check(failed && File.Exists(fixture.ControllerStop),
                "a failed camera stop write still independently signals controller cleanup");
        }
        File.Delete(fixture.SharedStop);
        File.Delete(fixture.ControllerStop);
        using (var locked = File.Open(fixture.ControllerStop, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            bool failed = false;
            try { fixture.Call("RequestTrackingStop"); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { failed = true; }
            Check(failed && File.Exists(fixture.SharedStop),
                "a failed controller stop write still independently signals camera cleanup");
        }
    }

    static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    sealed class Session : IDisposable
    {
        readonly HubForm form;
        readonly string root;
        readonly List<Process> owned = [];
        internal string SharedStop => Get<string>("_stopFile");
        internal string ControllerStop => Property<string>("ControllerStopFile");
        internal Session(string parent)
        {
            root = Path.Combine(parent, "controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            form = new HubForm(root, rememberLaunch: false, previewOnly: true);
            _ = form.Handle; // Enable actual BeginInvoke dispatch without showing a window.
            Get<CheckBox>("_gaze").Checked = false;
            Get<CheckBox>("_tongue").Checked = true;
            Get<CheckBox>("_pupil").Checked = false;
            Get<CheckBox>("_controllerTouchpad").Checked = true;
        }
        internal T Get<T>(string field) => (T)typeof(HubForm).GetField(field, Private)!.GetValue(form)!;
        internal void Set(string field, object value) => typeof(HubForm).GetField(field, Private)!.SetValue(form, value);
        internal T Property<T>(string property) => (T)typeof(HubForm).GetProperty(property, Private)!.GetValue(form)!;
        internal object? Call(string method, params object[] args) => typeof(HubForm).GetMethod(method, Private)!.Invoke(form, args);
        internal Process Start(string label, string mode, string marker, string witness)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var argument in new[] { "--controller-lifecycle-fixture", mode, marker, Path.Combine(root, witness + ".stopped") })
                start.ArgumentList.Add(argument);
            var arguments = new object[] { label, start, null! };
            var process = (Process)Call("StartManagedProcess", arguments)!;
            owned.Add(process);
            if (label == "Hand/controller input")
            {
                var actual = (string[])Call("ControllerInputArguments")!;
                int stopIndex = Array.IndexOf(actual, "-StopFile");
                Check(stopIndex >= 0 && actual[stopIndex + 1] == marker && marker != SharedStop,
                    "actual controller launch arguments use the isolated stop file");
            }
            return process;
        }
        internal void Wait(Func<bool> done, string operation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Application.DoEvents();
            if (!done()) throw new Exception("Timeout waiting for " + operation);
        }
        internal void WaitForController(Process process) => Wait(() => process.HasExited &&
            Get<Task<HubControllerFeedback>>("_controllerRestoration").IsCompleted && Get<bool>("_controllerInputStopped"),
            "drained controller exit result");
        internal void Stop()
        {
            var stopped = (Task)Call("StopTrackingAsync")!;
            Wait(() => stopped.IsCompleted, "explicit session Stop");
            stopped.GetAwaiter().GetResult();
        }
        internal bool SawStop(string witness) => File.Exists(Path.Combine(root, witness + ".stopped"));
        public void Dispose()
        {
            foreach (var process in owned)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            form.Dispose();
            foreach (var process in owned) process.Dispose();
        }
    }
}
