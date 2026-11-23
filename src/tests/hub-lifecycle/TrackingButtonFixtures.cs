using System.Reflection;
using QproFaceTracking.Hub;

internal static class TrackingButtonFixtures
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string parent)
    {
        var directory = Directory.CreateDirectory(Path.Combine(parent, "tracking-buttons-" + Guid.NewGuid().ToString("N")));
        try { RunClicks(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    private static void RunClicks(string root)
    {
        var idle = new Queries();
        var foreground = new Queries();
        using var receiver = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int idleOpens = 0, foregroundOpens = 0;
        NativeIdleFeed OpenIdle(bool steam, Action<VrchatFaceStatus> report)
        {
            idleOpens++;
            return new NativeIdleFeed(idle.Open(report), _ => { });
        }
        VrchatFaceSession OpenForeground(bool steam, VrchatFaceSelection selection, Action<VrchatFaceStatus> report)
        {
            Check(!steam && selection is { Brows: true, Tongue: false, Pupils: false, Gaze: false },
                "Start must select the requested optional face inputs.");
            foregroundOpens++;
            return foreground.Open(report);
        }
        var guides = new List<(string Title, string Instructions)>();
        using var form = new HubForm(root, false, true, OpenIdle, OpenForeground, (title, instructions) => guides.Add((title, instructions)))
        {
            ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000)
        };
        T Field<T>(string name) => (T)typeof(HubForm).GetField(name, Private)!.GetValue(form)!;
        form.Show();
        void Click(string text) => ClickControl(Descendants(form).OfType<Button>().Single(b => b.Visible && b.Text == text));
        void ClickField(string name) => ClickControl(Field<Button>(name));
        void CheckToggle(string name, bool enabled)
        {
            var box = Field<CheckBox>(name);
            if (box.Checked != enabled)
            {
                Check(box.Visible && box.Enabled, "Expected an editable tracking option: " + name);
                box.AccessibilityObject.DoDefaultAction();
                Check(box.Checked == enabled, "The checkbox did not accept its default click action: " + name);
            }
        }
        void Disclosure(string name)
        {
            Click("Show " + name);
            Click("Hide " + name);
        }
        Click("Facial tracking");
        Check(!Field<Button>("_useBackupOutputButton").Visible, "Initial discovery offered the backup before an actionable failure.");
        foreach (var name in new[] { "_gaze", "_tongue", "_pupil", "_individualCheekPuff", "_individualCheekSuck", "_eyebrowBoost" })
        {
            CheckToggle(name, true);
            CheckToggle(name, false);
        }
        Check(!Field<Button>("_start").Enabled && !Field<Button>("_stop").Enabled, "Idle footer offered an empty optional session.");
        Disclosure("eye settings and guidance");
        Disclosure("tongue settings");
        Disclosure("cheek adjustment details");
        Click("Show connection and camera settings");
        foreach (var name in new[] { "_cameraPreview", "_trackingStopSound" })
        {
            CheckToggle(name, true);
            CheckToggle(name, false);
        }
        Directory.CreateDirectory(Path.Combine(root, "calibration"));
        File.WriteAllText(Path.Combine(root, "calibration", "qpro-independent-visual-axis-v99.json"), "{}");
        Click("Refresh connection status");
        Check(Field<ComboBox>("_eyeProfiles").Items.Count == 1, "Refresh did not reload the newly created fixture profile.");
        Click("Open setup");
        Click("VRChat output");
        Wait(() => idle.Calls == 1, "Automatic discovery failed to start.");
        var setup = Field<Button>("_checkVrchatSetupButton");
        var live = Field<Button>("_checkVrchatLiveButton");
        var first = idle.Hold();
        ClickControl(setup);
        Wait(() => idle.Calls == 2, "The setup button reused old status instead of checking the current receiver.");
        Check(!setup.Enabled && !live.Enabled, "A second VRChat check was offered during discovery.");
        first.TrySetResult(null);
        Wait(() => setup.Enabled && Field<Label>("_directVrchatStatus").Text.Contains("VRChat not found."), "Setup lost the refreshed result.");
        Check(Descendants(form).Single(c => c.AccessibleName == "VRChat OSC setup status").Text ==
            Field<Label>("_directVrchatStatus").Text, "Setup and Facial tracking reported different results.");
        Click("Facial tracking");
        Click("Hide connection and camera settings");
        Check(Field<Button>("_useBackupOutputButton").Visible && Field<HubOutputRouteSwitch>("_outputRouteSetup").SelectedIndex == 0,
            "An unsuccessful connection check should offer backup while keeping direct selected.");
        var next = idle.Hold();
        ClickControl(live);
        Wait(() => idle.Calls == 3, "Facial tracking did not ask the active session for a new result.");
        next.TrySetResult(new((System.Net.IPEndPoint)receiver.Client.LocalEndPoint!,
            new Dictionary<string, string> { ["/avatar/parameters/JawOpen"] = "f" }));
        Wait(() => live.Enabled && Field<Label>("_directVrchatStatus").Text.Contains("Waiting for matching face data"),
            "Facial connection check did not display the current avatar's writable inputs.");
        Check(!Field<Button>("_useBackupOutputButton").Visible,
            "The Hub still offered a fallback after finding the avatar's writable inputs.");
        Check(idleOpens == 1 && foregroundOpens == 0, "Checking the connection started a second receiver.");

        CheckToggle("_eyebrowBoost", true);
        var heldDuringHandoff = idle.Hold();
        var release = idle.DelayCancellation();
        try
        {
            ClickControl(live);
            Wait(() => idle.Calls == 4, "The handoff fixture never entered discovery.");
            ClickField("_start");
            Check(!live.Enabled && !Field<Button>("_start").Enabled && Field<Button>("_stop").Enabled,
                "A pending foreground start should offer only Stop and lock both connection checks.");
            ClickField("_stop");
        }
        finally { release.TrySetResult(); heldDuringHandoff.TrySetResult(null); }
        Wait(() => idleOpens == 2 && Field<Button>("_start").Enabled && !Field<Button>("_stop").Enabled,
            "Cancelling startup through the footer did not restore idle controls.");
        Check(foregroundOpens == 0, "A cancelled start opened a foreground writer.");

        ClickField("_start");
        Wait(() => foregroundOpens == 1 && !Field<Button>("_start").Enabled && Field<Button>("_stop").Enabled &&
            foreground.Calls == 1, "Footer Start did not open the requested face session.");
        Check(!Field<CheckBox>("_tongue").Enabled && Field<CheckBox>("_eyebrowBoost").Enabled,
            "Foreground session exposed incorrect live controls.");
        var active = foreground.Hold();
        ClickControl(live);
        Wait(() => foreground.Calls == 2, "Foreground manual check did not reach its own writer.");
        active.TrySetResult(null);
        Wait(() => live.Enabled, "Foreground check did not finish.");
        ClickField("_stop");
        Wait(() => idleOpens == 3 && Field<Button>("_start").Enabled && !Field<Button>("_stop").Enabled,
            "Footer Stop did not release the optional session and resume native input.");
        CheckToggle("_eyebrowBoost", false);
        Click("Controllers");
        CheckToggle("_hybridHands", true);
        Check(Field<Button>("_start").Enabled, "Optical fingers did not enable footer Start.");
        CheckToggle("_hybridHands", false);
        CheckToggle("_controllerTouchpad", true);
        Check(Field<Button>("_start").Enabled && Field<ComboBox>("_touchpadSide").Enabled,
            "The controller option did not enable its settings and footer Start.");
        Field<ComboBox>("_touchpadMode").SelectedIndex = 2;
        Check(Field<DarkSlider>("_swipeSensitivity").Enabled && Field<DarkSlider>("_swipeSensitivity").Visible,
            "Swipe mode did not expose its sensitivity.");
        CheckToggle("_controllerTouchpad", false);
        Check(!Field<Button>("_start").Enabled && !Field<ComboBox>("_touchpadMode").Enabled,
            "Turning off the last optional input left footer Start enabled.");
        Click("Show hand and controller details");
        ClickField("_controllerBindingGuide");
        Check(guides.Count == 1 && guides[0].Title == "Assign the extra thumb-rest inputs" &&
            guides[0].Instructions.Contains("Manage Controller Bindings") && guides[0].Instructions.Contains("Swipe"),
            "Guide click did not open the controller binding instructions.");
        Click("Hide hand and controller details");
        TestControllerCheck(form, root);
        Click("Open Facial tracking");
        Check(live.Visible && !Field<Button>("_checkHandsButton").Visible, "Open Facial tracking landed on the wrong page.");
        Field<ComboBox>("_trackingSourceLive").SelectedIndex = 1;
        Click("Controllers");
        Check(!Field<CheckBox>("_hybridHands").Enabled && !Field<Button>("_checkHandsButton").Enabled &&
            Field<Label>("_handsStatus").Text.Contains("Steam Link"), "Steam Link offered unsupported controller actions.");
        form.Close();
        Console.WriteLine("PASS actual Facial tracking, Controllers, Check VRChat and footer Start/Stop button routes");
    }

    private static void TestControllerCheck(HubForm form, string root)
    {
        var check = (Button)typeof(HubForm).GetField("_checkHandsButton", Private)!.GetValue(form)!;
        var status = (Label)typeof(HubForm).GetField("_handsStatus", Private)!.GetValue(form)!;
        // This is the entire helper in this disposable app directory. It cannot contact a headset.
        File.WriteAllText(Path.Combine(root, "controller-input.ps1"), """
            param([string]$Action)
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'action.txt'), $Action)
            'HANDS_CHECK ' + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'report.json'))
            """);
        foreach (bool ready in new[] { false, true })
        {
            var result = new
            {
                compatible = ready, androidVersion = "fixture", pcDriverSha256 = new string('a', 64), fridaVersion = "fixture",
                settings = new { hand_tracking_enabled = "1", multimodal_hands_and_controllers_enabled = "1", simultaneous_hands_and_controllers_mode = "1" },
                settingSource = "current-user", experimental = true, runtimeValidated = false,
                problems = ready ? Array.Empty<string>() : ["Fixture headset unavailable"], warnings = Array.Empty<string>(),
                componentsReady = ready, componentProblems = Array.Empty<string>()
            };
            File.WriteAllText(Path.Combine(root, "report.json"), System.Text.Json.JsonSerializer.Serialize(result));
            string receipt = Path.Combine(root, "action.txt");
            File.Delete(receipt);
            typeof(HubForm).GetMethod("UpdateControllerInputAvailabilityFromSnapshot", Private)!.Invoke(form, [true]);
            ClickControl(check);
            string expected = ready ? "Controller requirements ready" : "components need attention";
            Wait(() => File.Exists(receipt) && status.Text.Contains(expected) &&
                !(bool)typeof(HubForm).GetField("_utilityActionRunning", Private)!.GetValue(form)!,
                "Controller check failed to present its fixture result: " + expected);
            Check(File.ReadAllText(receipt) == "check", "Compatibility requested a different helper action.");
        }
    }

    private sealed class Queries
    {
        internal int Calls;
        private TaskCompletionSource<VrchatOscTarget?>? _next;
        private TaskCompletionSource? _cancelGate;
        internal TaskCompletionSource DelayCancellation() =>
            _cancelGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<VrchatOscTarget?> Hold()
        {
            var pending = new TaskCompletionSource<VrchatOscTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _next, pending);
            return pending;
        }
        internal VrchatFaceSession Open(Action<VrchatFaceStatus> report) => new(() => new Dictionary<string, float>(),
            new MemoryStream(), report, CheckAsync, (_, token) => CheckAsync(token), TimeSpan.FromHours(1));

        private async Task<VrchatOscTarget?> CheckAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var request = Interlocked.Exchange(ref _next, null);
            if (request is null) return null;
            try { return await request.Task.WaitAsync(token); }
            finally
            {
                if (token.IsCancellationRequested && Interlocked.Exchange(ref _cancelGate, null) is { } gate)
                    await gate.Task;
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control c) =>
        c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static void ClickControl(Button button)
    {
        Check(button.Visible && button.Enabled, "Cannot click " + button.Text);
        Console.WriteLine("Click " + button.Text);
        button.PerformClick();
        Application.DoEvents();
    }
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    private static void Wait(Func<bool> predicate, string message)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
        Check(predicate(), message);
    }
}
