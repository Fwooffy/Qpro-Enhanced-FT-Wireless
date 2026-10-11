using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class ConnectionStatusTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static string _fixtures = "";
    private static readonly List<HubForm> Forms = [];
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static Task Refresh(HubForm form) =>
        (Task)typeof(HubForm).GetMethod("RefreshStatusAsync", Private)!.Invoke(form, null)!;
    private static void Wait(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(3);
        }
        Check(condition(), "The owned native status fixture did not complete within five seconds.");
    }
    private static void Complete(Task task)
    {
        Wait(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    private static string NativeText(Control control)
    {
        var text = new StringBuilder(4096);
        GetWindowText(control.Handle, text, text.Capacity);
        return text.ToString();
    }
    private static void CheckActivityColor(HubForm form, string message, Color expected)
    {
        var log = Field<RichTextBox>(form, "_log");
        var index = log.Text.IndexOf(message, StringComparison.Ordinal);
        Check(index >= 0, "The Activity message to inspect was missing.");
        log.Select(index, message.Length);
        Check(log.SelectionColor.ToArgb() == expected.ToArgb(), "The Activity severity color did not match its connection state.");
        log.Select(log.TextLength, 0);
    }
    private static HubForm.HubStatusSnapshot Snapshot() => new(true, false,
        new(HubModuleState.Missing, "Fixture module", "Fixture module not installed", "fixture-module"),
        false, false, false, null, false, false);

    private sealed class AdbFixture
    {
        internal const string Serial = "QUESTSTATUSFIXTURE";
        internal string State = "device";
        internal bool Empty;
        internal readonly List<string[]> Commands = [];
        internal Func<string[], Task<(bool, int, string)>?>? Override;
        internal Task<(bool, int, string)> Run(string _, IEnumerable<string> arguments, int timeout, CancellationToken cancellation)
        {
            var values = arguments.ToArray();
            Commands.Add(values);
            if (values.Contains("reconnect") || values.Contains("kill-server"))
                throw new InvalidOperationException("Status checks must never reset a fixture transport.");
            var overridden = Override?.Invoke(values);
            if (overridden is not null) return overridden;
            if (values.SequenceEqual(new[] { "start-server" })) return Task.FromResult((true, 0, ""));
            if (values.SequenceEqual(new[] { "devices", "-l" })) return Task.FromResult((true, 0,
                "List of devices attached\n" + (Empty ? "" : Serial + "\t" + State + " product:seacliff model:Quest_Pro device:seacliff\n")));
            if (values.SequenceEqual(new[] { "-d", "get-serialno" })) return Task.FromResult((true, 0, Serial));
            if (values.LastOrDefault() == "ro.product.model") return Task.FromResult((true, 0, "Quest Pro"));
            if (values.LastOrDefault() == "ro.product.device") return Task.FromResult((true, 0, "seacliff"));
            if (values.LastOrDefault() == "get-state") return Task.FromResult((true, 0, "device"));
            throw new InvalidOperationException("Unexpected fixture command: " + string.Join(" ", values));
        }
    }

    private static (HubForm Form, HubEnvironment Environment, AdbFixture Adb, string Root) Create(string name, bool wireless = false)
    {
        var root = Path.Combine(_fixtures, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "platform-tools"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        // The actual FindAdb checks existence. All commands run through a real
        // HubAdbSession with an owned fake runner; this file is never executed.
        File.WriteAllBytes(Path.Combine(root, "platform-tools", "adb.exe"), []);
        File.WriteAllText(Path.Combine(root, "config", "connection-mode.txt"), wireless ? "wireless" : "usb");
        var form = new HubForm(root, rememberLaunch: false, previewOnly: true)
        {
            ShowInTaskbar = false,
            Opacity = 0,
        };
        Forms.Add(form);
        var environment = Field<HubEnvironment>(form, "_environment");
        var adb = new AdbFixture();
        Set(environment, "_adbSession", new HubAdbSession(adb.Run));
        Field<Label>(form, "_usbStatus").Text = "previous connection status";
        Field<Label>(form, "_steamStatus").Text = "previous Steam status";
        form.ConfigurePreviewStatusRefresh(() => Task.FromResult(Snapshot()), (_, _) => Task.CompletedTask);
        return (form, environment, adb, root);
    }

    private static void FreshConnection(HubForm form)
    {
        var label = Field<Label>(form, "_usbStatus");
        var note = Field<Label>(form, "_connectionModeNote");
        Check(label.Text.Contains("USB ADB connected"), "A successful fresh USB probe was held behind unrelated status work.");
        Check(NativeText(label).Contains("USB ADB connected"), "The native USB label did not receive fresh readiness.");
        Check(label.AccessibleDescription == "USB Quest Pro connected and remembered.", "Fresh USB accessibility reason was not published.");
        Check(note.Text == label.AccessibleDescription, "Fresh USB setup guidance did not match its visible status.");
        Check(Field<RichTextBox>(form, "_log").Text.Contains("[USB connection check] USB Quest Pro connected and remembered."),
            "Fresh USB readiness was absent from Activity.");
        CheckActivityColor(form, "[USB connection check] USB Quest Pro connected and remembered.", HubForm.Good);
    }

    private static void AsyncBoundaryTests()
    {
        foreach (var heartbeat in new[] { false, true })
        foreach (var fault in new[] { false, true })
        {
            var fixture = Create("pending-" + heartbeat + "-" + fault);
            var pendingSnapshot = Pending<HubForm.HubStatusSnapshot>();
            var pendingHeartbeat = Pending<bool>();
            var heartbeatStarted = false;
            fixture.Form.ConfigurePreviewStatusRefresh(
                () => heartbeat ? Task.FromResult(Snapshot()) : pendingSnapshot.Task,
                (_, _) => { heartbeatStarted = true; return heartbeat ? pendingHeartbeat.Task : Task.CompletedTask; });
            var refresh = Refresh(fixture.Form);
            Wait(() => Field<Label>(fixture.Form, "_usbStatus").Text.Contains("USB ADB connected"));
            if (heartbeat) Wait(() => heartbeatStarted);
            FreshConnection(fixture.Form);
            Check(!refresh.IsCompleted, "Fixture dependency did not remain pending for the ordering check.");
            Check(Field<Label>(fixture.Form, "_steamStatus").Text == "previous Steam status", "Unrelated status was published before its dependencies completed.");
            if (heartbeat)
            {
                if (fault) pendingHeartbeat.SetException(new IOException("fixture heartbeat failed"));
                else pendingHeartbeat.SetResult(true);
            }
            else
            {
                if (fault) pendingSnapshot.SetException(new IOException("fixture module/runtime snapshot failed"));
                else pendingSnapshot.SetResult(Snapshot());
            }
            Complete(refresh);
            FreshConnection(fixture.Form);
            Check(!Field<bool>(fixture.Form, "_statusRefreshBusy"), "A delayed/faulted unrelated dependency latched the refresh gate.");
            if (fault)
                Check(Field<RichTextBox>(fixture.Form, "_log").Text.Contains("Status refresh could not complete: fixture"),
                    "Unrelated failure lost its diagnostic while preserving successful USB readiness.");
        }
    }

    private static void ContextAndDisposalTests()
    {
        foreach (var change in new[] { "disposed", "mode", "source", "endpoint" })
        {
            var fixture = Create("quest-context-" + change, wireless: change == "endpoint");
            if (change == "endpoint")
                File.WriteAllText(Path.Combine(fixture.Root, "config", "wireless-headset.json"), "{\"adbTarget\":\"192.0.2.77:5555\"}");
            var started = false;
            var pendingProbe = Pending<(bool, int, string)>();
            fixture.Adb.Override = arguments => arguments.SequenceEqual(new[] { "devices", "-l" }) || arguments.LastOrDefault() == "get-state"
                ? DelayProbe() : null;
            Task<(bool, int, string)> DelayProbe() { started = true; return pendingProbe.Task; }
            var snapshotCalls = 0;
            fixture.Form.ConfigurePreviewStatusRefresh(() => { snapshotCalls++; return Task.FromResult(Snapshot()); }, (_, _) => Task.CompletedTask);
            var refresh = Refresh(fixture.Form);
            Wait(() => started);
            if (change == "disposed") fixture.Form.Dispose();
            else if (change == "mode") fixture.Environment.SelectConnection(true);
            else if (change == "source") fixture.Environment.SetPreviewTrackingSource(!fixture.Environment.SteamLinkSelected);
            else File.WriteAllText(Path.Combine(fixture.Root, "config", "wireless-headset.json"), "{\"adbTarget\":\"192.0.2.88:5555\"}");
            pendingProbe.SetResult((true, 0, change == "endpoint" ? "device" : "List of devices attached\n" + AdbFixture.Serial + "\tdevice\n"));
            Complete(refresh);
            Check(snapshotCalls == 0, "A disposed or changed context ran unrelated snapshot work for an obsolete probe.");
            if (change != "disposed") Check(Field<Label>(fixture.Form, "_usbStatus").Text == "previous connection status",
                "A late transport result was published after its context changed.");
        }

        foreach (var change in new[] { "disposed", "mode", "source" })
        {
            var fixture = Create("snapshot-context-" + change);
            var pending = Pending<HubForm.HubStatusSnapshot>();
            var heartbeatCalls = 0;
            fixture.Form.ConfigurePreviewStatusRefresh(() => pending.Task, (_, _) => { heartbeatCalls++; return Task.CompletedTask; });
            var refresh = Refresh(fixture.Form);
            Wait(() => Field<Label>(fixture.Form, "_usbStatus").Text.Contains("USB ADB connected"));
            FreshConnection(fixture.Form);
            if (change == "disposed") fixture.Form.Dispose();
            else if (change == "mode") fixture.Environment.SelectConnection(true);
            else fixture.Environment.SetPreviewTrackingSource(!fixture.Environment.SteamLinkSelected);
            pending.SetResult(Snapshot());
            Complete(refresh);
            Check(heartbeatCalls == 0, "Obsolete snapshot work ran heartbeat after disposal/context change.");
            if (change != "disposed") Check(Field<Label>(fixture.Form, "_steamStatus").Text == "previous Steam status",
                "A late snapshot published obsolete unrelated status.");
        }
    }

    private static void StateReasonAndComboTests()
    {
        foreach (var state in new[] { "unauthorized", "offline", "empty" })
        {
            var fixture = Create("state-" + state);
            fixture.Adb.State = state;
            fixture.Adb.Empty = state == "empty";
            Complete(Refresh(fixture.Form));
            var reason = fixture.Environment.UsbConnectionMessage;
            var label = Field<Label>(fixture.Form, "_usbStatus");
            Check(label.Text.Contains("USB not connected") && label.AccessibleDescription == reason, "Command-success USB failure lost its state reason.");
            var first = Field<RichTextBox>(fixture.Form, "_log").Text;
            var message = "[USB connection check] WARNING: " + reason;
            Check(first.Contains(message), "Unusable USB state produced no Activity guidance.");
            CheckActivityColor(fixture.Form, message, HubForm.Warning);
            Complete(Refresh(fixture.Form));
            var second = Field<RichTextBox>(fixture.Form, "_log").Text;
            Check(second.Split(message).Length == 2, "Unchanged unusable USB state was logged repeatedly.");
            Check(fixture.Adb.Commands.All(command => !command.Contains("reconnect")), "Read-only status changed an unusable transport.");
        }
        var wireless = Create("missing-wireless", wireless: true);
        Complete(Refresh(wireless.Form));
        var wirelessLabel = Field<Label>(wireless.Form, "_usbStatus");
        Check(wirelessLabel.Text.Contains("Wi-Fi address needed"), "Missing wireless setup was reported as an unexplained transport failure.");
        var wirelessReason = wirelessLabel.AccessibleDescription ?? "";
        Check(wirelessReason.Contains("Connect to Quest") && wirelessReason.Contains("USB cable"),
            "Missing wireless address did not explain both appropriate next steps.");
        CheckActivityColor(wireless.Form, "[Wi-Fi connection check] WARNING: " + wirelessReason, HubForm.Warning);
        Check(wireless.Adb.Commands.Count == 0, "Wireless selection silently probed/fell back to physical USB.");

        var combo = Create("combo-events");
        var selector = Field<ComboBox>(combo.Form, "_connectionMode");
        Check(selector.Items.Count == 2 && selector.Items[0]!.ToString() == "USB cable" && selector.Items[1]!.ToString() == "Wireless ADB (Wi-Fi)",
            "Connection selector display order does not match its event mapping.");
        Check(selector.SelectedIndex == 0 && !combo.Environment.WirelessSelected, "Saved USB initialized a wireless selector.");
        File.WriteAllText(Path.Combine(combo.Root, "config", "wireless-headset.json"), "{\"adbTarget\":\"192.0.2.77:5555\"}");
        // Preview construction avoids timers, installers and preference writes.
        // Only this fixture activates the real selection event, with all ADB
        // commands still intercepted by its instance-owned HubAdbSession runner.
        Set(combo.Form, "_previewOnly", false);
        selector.SelectedIndex = 1;
        Wait(() => !Field<bool>(combo.Form, "_statusRefreshBusy"));
        Check(combo.Environment.WirelessSelected && File.ReadAllText(Path.Combine(combo.Root, "config", "connection-mode.txt")) == "wireless",
            "Choosing wireless did not update matching instance/config state.");
        selector.SelectedIndex = 0;
        Wait(() => !Field<bool>(combo.Form, "_statusRefreshBusy"));
        Check(!combo.Environment.WirelessSelected && File.ReadAllText(Path.Combine(combo.Root, "config", "connection-mode.txt")) == "usb",
            "Choosing USB left the production environment/config in wireless mode.");
        FreshConnection(combo.Form);
        Set(combo.Form, "_setupActionRunning", true);
        selector.SelectedIndex = 1;
        Check(selector.SelectedIndex == 0 && !combo.Environment.WirelessSelected, "Busy selection rejection left UI and actual transport mismatched.");
        Check(File.ReadAllText(Path.Combine(combo.Root, "config", "connection-mode.txt")) == "usb", "Busy selector changed persisted transport.");
        Set(combo.Form, "_setupActionRunning", false);
        Set(combo.Form, "_previewOnly", true);
    }

    [STAThread]
    private static int Main()
    {
        var externalTarget = Environment.GetEnvironmentVariable("QPRO_ADB_TARGET");
        var externalSerial = Environment.GetEnvironmentVariable("ANDROID_SERIAL");
        try
        {
            Environment.SetEnvironmentVariable("QPRO_ADB_TARGET", null);
            Environment.SetEnvironmentVariable("ANDROID_SERIAL", null);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            _fixtures = Path.Combine(AppContext.BaseDirectory, "connection-status-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_fixtures);
            AsyncBoundaryTests();
            ContextAndDisposalTests();
            StateReasonAndComboTests();
            Console.WriteLine($"Connection status checks passed: {_checks}; actual RefreshStatusAsync, native label/log, selected-transport mapping, pending/faulted unrelated work, context changes and disposal. No real ADB or displayed Hub.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Connection status checks failed: " + error); return 1; }
        finally
        {
            foreach (var form in Forms) form.Dispose();
            Environment.SetEnvironmentVariable("QPRO_ADB_TARGET", externalTarget);
            Environment.SetEnvironmentVariable("ANDROID_SERIAL", externalSerial);
        }
    }
}
