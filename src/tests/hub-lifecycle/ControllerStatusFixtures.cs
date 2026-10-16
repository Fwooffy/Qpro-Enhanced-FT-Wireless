using System.Reflection;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class ControllerStatusFixtures
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string root)
    {
        string Report(bool ready, bool leftFresh, bool rightFresh, bool leftRouted, bool rightRouted, long queries = 1) =>
            "HANDS_STATUS " + JsonSerializer.Serialize(new
            {
                ready,
                adapters = new object[]
                {
                    new { queries }, new { freshSides = new[] { leftFresh, rightFresh } },
                    new { activeSides = new[] { leftRouted, rightRouted } },
                },
            });

        var left = Report(true, true, false, true, false);
        var right = Report(true, false, true, false, true);
        var waiting = Report(false, false, false, true, true);
        Check(HubHandLiveStatus.TryParse(left, out var leftStatus) && leftStatus is { Ready: true, Left: true, Right: false },
            "current finger status identifies the fresh routed left hand");
        Check(HubHandLiveStatus.TryParse(right, out var rightStatus) && rightStatus is { Ready: true, Left: false, Right: true },
            "current finger status identifies the fresh routed right hand");
        Check(HubHandLiveStatus.TryParse(waiting, out var waitingStatus) && waitingStatus is { Ready: false, Left: false, Right: false },
            "expired optical data is waiting even if earlier skeleton routing remains recorded");
        Check(HubHandLiveStatus.TryParse(Report(false, false, true, true, false), out var mismatch) && !mismatch!.Ready,
            "fresh data on one side and routing on the other cannot confirm finger output");
        Check(HubHandLiveStatus.TryParse(Report(false, true, true, true, true, 0), out var noQuery) && !noQuery!.Ready,
            "finger readiness requires controller-selection queries");
        foreach (var invalid in new[]
        {
            Report(true, false, false, true, true), Report(true, true, true, true, true, 0),
            Report(false, true, false, true, false), Report(false, false, false, false, false, -1),
            left.Replace("\"ready\":true", "\"ready\":true,\"ready\":false"),
            left.Replace("\"queries\":1", "\"queries\":1,\"queries\":2"),
            left.Replace("[true,false]", "[true]"), left.Replace("[true,false]", "[1,false]"),
            "HANDS_STATUS {invalid}", "HANDS_STATUS " + new string(' ', 65536),
        }) Check(!HubHandLiveStatus.TryParse(invalid, out _), "malformed or inconsistent finger readiness stays unconfirmed");

        // Construct without ADB, timers or preference writes, and create a handle
        // only for the real UI dispatcher. The test never shows a window.
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
        _ = form.Handle;
        T Get<T>(string field) => (T)typeof(HubForm).GetField(field, Private)!.GetValue(form)!;
        void Set(string field, bool value) => typeof(HubForm).GetField(field, Private)!.SetValue(form, value);
        object? Call(string method, params object[] args) => typeof(HubForm).GetMethod(method, Private)!.Invoke(form, args);
        void Observe(string line) => Call("ObserveControllerInput", "Hand/controller input", line);
        Check(Get<Button>("_controllerBindingGuide").Enabled,
            "SteamVR binding instructions remain available before enabling experimental input");
        var pendingColor = Get<Label>("_handsStatus").ForeColor;
        Check(Get<Label>("_handsStatus").Text.Contains("not checked") &&
            Get<Label>("_handsStatusNext").Text.Contains("check compatibility"),
            "unchecked compatibility has an explicit status and next step");
        Call("ShowControllerFeedback", new HubControllerFeedback(ControllerFeedbackState.Checked,
            "Hand/controller prechecks passed", "Static checks passed; live optical input has not been verified.",
            "Start tracking and confirm real finger movement.", false));
        var passedColor = Get<Label>("_handsStatus").ForeColor;
        Check(passedColor != pendingColor && Get<Label>("_handsStatusDetail").Text.Contains("has not been verified") &&
            Get<Label>("_handsStatusNext").Text.Contains("confirm real finger movement"),
            "passed static checks are distinct from a required live verification");
        Call("ShowControllerFeedback", new HubControllerFeedback(ControllerFeedbackState.Checked,
            "Hand/controller compatibility needs attention", "No validated profile is available.",
            "Keep Hands + controllers off and copy diagnostics.", true));
        var blockedColor = Get<Label>("_handsStatus").ForeColor;
        Check(blockedColor != passedColor && blockedColor != pendingColor &&
            Get<Label>("_handsStatusNext").Text.Contains("Keep Hands + controllers off"),
            "blocked checks use an error color and retain their recovery step");
        Call("ShowControllerFeedback", new HubControllerFeedback(ControllerFeedbackState.Waiting,
            "Waiting for controller restoration", "Final restoration is not confirmed.", "Keep the Hub open.", false));
        Check(Get<Label>("_handsStatus").ForeColor == pendingColor,
            "an unconfirmed waiting result uses the same warning color as an unchecked result");
        Call("ControllerInputStartupFailed", new InvalidOperationException("Sample startup failure."));
        Check(Get<Label>("_handsStatus").ForeColor == blockedColor &&
            Get<Label>("_handsStatusDetail").Text.Contains("Sample startup failure"),
            "a controller startup failure leaves a final red result instead of a waiting message");
        Set("_controllerInputStopped", false);
        Get<CheckBox>("_hybridHands").Checked = true;
        Observe(left);
        Check(Get<bool>("_handsReady") && Get<Label>("_handsStatus").Text.Contains("left active; right waiting"),
            "Hub displays the current per-hand result");
        Observe(waiting);
        Check(!Get<bool>("_handsReady") && Get<Label>("_handsStatus").Text.Contains("Waiting for fresh"),
            "occlusion clears earlier ready display without stopping the worker");
        Observe(right);
        Check(Get<bool>("_handsReady"), "fresh optical data restores the ready display");
        Observe("HANDS_STATUS {invalid}");
        Check(!Get<bool>("_handsReady"), "an unreadable current report cannot retain a stale ready display");
        foreach (var guard in new[] { "_stopping", "_controllerInputStopped", "_controllerCleanupFailed" })
        {
            Set(guard, true);
            Observe(left);
            Check(!Get<bool>("_handsReady"), guard + " rejects late optical readiness");
            Set(guard, false);
        }

        Get<CheckBox>("_controllerTouchpad").Checked = true;
        var mode = Get<ComboBox>("_touchpadMode");
        var side = Get<ComboBox>("_touchpadSide");
        Check(side.SelectedItem?.ToString() == "Both", "thumb-rest selection defaults to both controllers");
        foreach (var selected in new[] { ("Left (L)", "left"), ("Right (R)", "right"), ("Both", "both") })
        {
            side.SelectedItem = selected.Item1;
            mode.SelectedItem = "Swipe";
            var options = form.CaptureLiveOptions();
            var restored = JsonSerializer.Deserialize<HubLiveOptions>(JsonSerializer.Serialize(options))!;
            side.SelectedItem = "Both";
            form.ApplyLiveOptions(restored);
            Check(side.SelectedItem?.ToString() == selected.Item1, "saved thumb-rest side restores " + selected.Item1);
            var args = (string[])Call("ControllerInputArguments")!;
            Check(args[Array.IndexOf(args, "-Side") + 1] == selected.Item2 &&
                args[Array.IndexOf(args, "-Mode") + 1] == "swipe" && args.Contains("-Hands"),
                "launch arguments scope only the thumb-rest mode to " + selected.Item2);
        }
        form.ApplyLiveOptions(form.CaptureLiveOptions() with { TouchpadSide = null });
        Check(side.SelectedItem?.ToString() == "Both", "older saved options keep the both-controller default");
        form.ApplyLiveOptions(form.CaptureLiveOptions() with { TouchpadSide = "invalid" });
        Check(side.SelectedItem?.ToString() == "Both", "invalid saved side uses the both-controller default");
        Get<CheckBox>("_controllerTouchpad").Checked = false;
        Check(!side.Enabled, "side selection is disabled when extra thumb-rest input is off");
        foreach (var name in new[] { "Trackpad", "Relative joystick", "Swipe", "Desktop mouse" })
        {
            mode.SelectedItem = name;
            Check(Get<Label>("_controllerModeHint").Text.StartsWith(name + ":"), "mode guidance follows " + name);
        }
        Observe("TOUCHPAD_READY {\"mode\":\"mouse\"}");
        Check(Get<Label>("_handsStatusDetail").Text.Contains("Windows pointer") &&
            !Get<Label>("_handsStatusDetail").Text.Contains("SteamVR") &&
            Get<Label>("_handsStatusNext").Text.Contains("Stop tracking releases"),
            "Desktop mouse readiness explains pointer control rather than SteamVR binding");
        mode.SelectedItem = "Trackpad";
        Observe("TOUCHPAD_READY {\"mode\":\"trackpad\"}");
        Check(Get<Label>("_handsStatusDetail").Text.Contains("SteamVR") &&
            Get<Label>("_handsStatusNext").Text.Contains("binding"),
            "trackpad readiness retains the SteamVR add-on and binding guidance");
    }

    static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
