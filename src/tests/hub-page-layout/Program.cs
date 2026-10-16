using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class PageLayoutTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static T Field<T>(HubForm form, string name) => (T)typeof(HubForm).GetField(name, Private)!.GetValue(form)!;
    private static void Call(HubForm form, string name, params object[] values) => typeof(HubForm).GetMethod(name, Private)!.Invoke(form, values);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static Panel PageOf(Control control)
    {
        for (Control? parent = control; parent is not null; parent = parent.Parent)
            if (parent is Panel page && Equals(page.Tag, "hub-page")) return page;
        throw new Exception("Control has no workflow page.");
    }
    private static void Pump(HubForm form)
    {
        var until = Environment.TickCount64 + 120;
        do { Application.DoEvents(); Thread.Sleep(5); } while (Environment.TickCount64 < until);
        form.FlushPreviewLayout(); Application.DoEvents();
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.Dark);
        string root = Path.Combine(Path.GetTempPath(), "qpro-page-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true)
        { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false, MinimumSize = Size.Empty };
        form.Size = new Size(1280, 900);
        form.Show();
        form.ApplyPreviewScenario("hands-modes", "VirtualDesktop");
        Pump(form);
        var controls = Descendants(form).ToArray();
        var productName = controls.OfType<Label>().Single(label => label.AccessibleName == "Product name");
        Check(form.Text.StartsWith("Qpro Companion V3.0.0", StringComparison.Ordinal) &&
            productName.Text == "Qpro Companion", "Window or sidebar did not use the public product name.");
        Check(((string)typeof(HubForm).GetMethod("DiagnosticText", Private)!.Invoke(form, null)!)
            .StartsWith("Qpro Companion V3.0.0", StringComparison.Ordinal), "Copied diagnostics kept the old product name.");
        var navigation = controls.OfType<Button>().Where(button => Equals(button.Tag, "workflow-tab"))
            .OrderBy(button => ((TableLayoutPanel)button.Parent!).GetRow(button)).ToArray();
        Check(navigation.Select(button => button.Text).SequenceEqual(new[] { "First-time setup", "Facial tracking", "Controllers", "Personalize", "Model manager", "Activity" }),
            "Controllers navigation is not directly below Facial tracking.");
        Check(navigation.All(button => button.TabStop && button.AccessibleName == button.Text), "Workflow navigation is not accessible by keyboard/name.");
        var live = navigation.Single(button => button.Text == "Facial tracking");
        var controllers = navigation.Single(button => button.Text == "Controllers");
        var activity = navigation.Single(button => button.Text == "Activity");
        var tongue = Field<CheckBox>(form, "_tongue");
        var hands = Field<CheckBox>(form, "_hybridHands");
        var touchpad = Field<CheckBox>(form, "_controllerTouchpad");
        var health = Field<TableLayoutPanel>(form, "_trackingHealthCard");
        var log = Field<RichTextBox>(form, "_log");
        var mode = Field<ComboBox>(form, "_touchpadMode");
        var side = Field<ComboBox>(form, "_touchpadSide");
        var sensitivity = Field<DarkSlider>(form, "_swipeSensitivity");
        var sensitivityValue = Field<Label>(form, "_swipeSensitivityValue");
        var source = Field<ComboBox>(form, "_trackingSourceLive");
        Check(PageOf(hands) == PageOf(touchpad) && PageOf(hands) != PageOf(tongue), "Controller controls still share the face page.");
        Check(PageOf(health) == PageOf(log) && PageOf(health) != PageOf(tongue) && health.AccessibleName == "Tracking health", "Tracking health is not named or located correctly on Activity.");
        Check(((TableLayoutPanel)health.Parent!).GetRow(health) < ((TableLayoutPanel)log.Parent!.Parent!).GetRow(log.Parent!), "Tracking health does not precede the detailed log.");
        var start = Field<Button>(form, "_start"); var stop = Field<Button>(form, "_stop");
        Check(start.Parent == stop.Parent && !Equals(start.Parent!.Tag, "hub-page") && start.Visible && stop.Visible,
            "Start/Stop no longer remain global.");
        var handles = new[] { tongue, hands, touchpad, (Control)health, log, mode, side, sensitivity, source, start, stop }.Select(control => control.Handle).ToArray();
        controllers.PerformClick(); Pump(form);
        Check(PageOf(hands).Visible && !PageOf(tongue).Visible && !PageOf(health).Visible, "Controller navigation did not select only its page.");
        Check(mode.SelectedItem?.ToString() == "Trackpad" && side.SelectedItem?.ToString() == "Both", "Navigation changed mode/side defaults.");
        var controllerCard = (TableLayoutPanel)touchpad.Parent!;
        var modeHint = Field<Label>(form, "_controllerModeHint");
        var sensitivityPanel = controls.OfType<TableLayoutPanel>()
            .Single(panel => panel.AccessibleName == "Swipe sensitivity options");
        void CheckControllerSelectors()
        {
            Check(mode.Parent == controllerCard && side.Parent == controllerCard && modeHint.Parent == controllerCard,
                "Controller mode, side or explanation is still hidden inside details.");
            Check(controllerCard.GetRow(mode) == controllerCard.GetRow(touchpad) + 1 &&
                controllerCard.GetRow(side) == controllerCard.GetRow(mode) + 1 &&
                controllerCard.GetRow(sensitivityPanel) == controllerCard.GetRow(side) + 1 &&
                controllerCard.GetRow(modeHint) == controllerCard.GetRow(sensitivityPanel) + 1,
                "Controller mode and side do not immediately follow the two toggles.");
            Check(mode.Visible && side.Visible && modeHint.Visible && mode.Width > 100 && side.Width > 100,
                "Controller choices are hidden or compressed without expanding details.");
            Check(touchpad.Bottom <= mode.Top && mode.Bottom <= side.Top && side.Bottom <= modeHint.Top,
                "Controller toggles, choices or mode explanation overlap.");
            if (!sensitivityPanel.Visible)
                Check(modeHint.Top - side.Bottom <= (int)Math.Ceiling(24 * form.DeviceDpi / 96F),
                    "A hidden sensitivity row leaves an empty gap in non-Swipe modes.");
            var page = PageOf(mode);
            Check(page.RectangleToScreen(page.ClientRectangle).Contains(side.RectangleToScreen(side.ClientRectangle)),
                "Choosing the controller side requires scrolling at a supported test window size.");
            Check(mode.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(
                new[] { "Trackpad", "Relative joystick", "Swipe", "Desktop mouse" }) &&
                side.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(new[] { "Both", "Left (L)", "Right (R)" }),
                "Moving the choices changed the available modes or controller sides.");
        }
        CheckControllerSelectors();
        Check(!sensitivityPanel.Visible && sensitivity.Value == 100 && sensitivityValue.Text == "100%",
            "Non-Swipe modes show unnecessary sensitivity controls or changed the suggested default.");
        Check(sensitivity.Minimum == 25 && sensitivity.Maximum == 200 && sensitivity.SmallChange == 25 &&
            sensitivity.AccessibleName == "Swipe sensitivity" && sensitivity.AccessibilityObject.Role == AccessibleRole.Slider &&
            sensitivityValue.AccessibleName == "Swipe sensitivity percentage",
            "Swipe slider range, increments or accessible labels are incorrect.");
        Check(mode.Enabled && side.Enabled, "Selected Virtual Desktop thumb-rest input did not enable mode and side.");
        touchpad.Checked = false; Pump(form);
        Check(!mode.Enabled && !side.Enabled, "Disabled thumb-rest input still permits mode changes.");
        CheckControllerSelectors();
        touchpad.Checked = true; Pump(form);
        var modeHints = new HashSet<string>();
        for (var index = 0; index < mode.Items.Count; index++)
        {
            mode.SelectedIndex = index; Pump(form);
            modeHints.Add(modeHint.Text);
            Check(sensitivityPanel.Visible == (index == 2) && sensitivity.Enabled == (index == 2),
                "Swipe sensitivity visibility or availability does not follow the selected mode.");
            Check(modeHint.Text.StartsWith(mode.SelectedItem!.ToString()! + ":", StringComparison.Ordinal),
                "The visible mode explanation does not follow the selected mode.");
            if (index == 2)
            {
                Check(modeHint.Text.Contains("SteamVR dashboard and VRChat menu scrolling automatically where supported", StringComparison.Ordinal) &&
                    modeHint.Text.Contains("Custom bindings are kept", StringComparison.Ordinal) &&
                    modeHint.Text.Contains("Activity's manual steps", StringComparison.Ordinal) &&
                    modeHint.Text.Contains("Normal movement, buttons, gestures and other game bindings stay unchanged", StringComparison.Ordinal),
                    "Swipe guidance does not distinguish supported menu setup from preserved normal/custom/game controls.");
                Check(sensitivity.Width > 80 && sensitivity.Right <= sensitivityValue.Left &&
                    sensitivityValue.Width >= 56 && sensitivityValue.Text == "100%",
                    "Swipe slider overlaps or compresses its percentage readout.");
                if (args.Length == 1)
                {
                    Directory.CreateDirectory(args[0]);
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(args[0], "controllers-swipe.png"), ImageFormat.Png);
                    var originalSize = form.Size;
                    form.Size = new Size(880, 700); Pump(form);
                    CheckControllerSelectors();
                    Check(PageOf(sensitivity).RectangleToScreen(PageOf(sensitivity).ClientRectangle)
                        .Contains(sensitivity.RectangleToScreen(sensitivity.ClientRectangle)) &&
                        PageOf(sensitivityValue).RectangleToScreen(PageOf(sensitivityValue).ClientRectangle)
                        .Contains(sensitivityValue.RectangleToScreen(sensitivityValue.ClientRectangle)),
                        "Compact Swipe layout clips the sensitivity choice.");
                    using var compact = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(compact, new Rectangle(Point.Empty, form.Size));
                    compact.Save(Path.Combine(args[0], "controllers-swipe-small.png"), ImageFormat.Png);
                    form.Size = originalSize; Pump(form);
                }
            }
        }
        Check(modeHints.Count == 4, "Controller modes share a stale explanation.");
        var bindingInstructions = (string)typeof(HubForm).GetField("ControllerBindingInstructions", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        Check(bindingInstructions.Contains("Existing custom bindings are kept", StringComparison.Ordinal) &&
            bindingInstructions.Contains("select the prepared Qpro binding manually", StringComparison.Ordinal) &&
            bindingInstructions.Contains("If your binding layout is not recognized, leave it unchanged", StringComparison.Ordinal) &&
            bindingInstructions.Contains("SteamVR dashboard and VRChat menu scrolling", StringComparison.Ordinal) &&
            bindingInstructions.Contains("does not replace your game's bindings", StringComparison.Ordinal) &&
            bindingInstructions.Contains("active only in Swipe while tracking runs", StringComparison.Ordinal),
            "Binding instructions omit the dashboard fallback, preserved game choices or Swipe-only lifetime.");
        mode.SelectedItem = "Swipe";
        foreach (var percent in Enumerable.Range(1, 8).Select(index => index * 25))
        {
            sensitivity.Value = percent;
            var options = form.CaptureLiveOptions();
            var restored = JsonSerializer.Deserialize<HubLiveOptions>(JsonSerializer.Serialize(options))!;
            sensitivity.Value = 100;
            form.ApplyLiveOptions(restored);
            Check(form.CaptureLiveOptions() == options && options.SwipeSensitivityPercent == percent &&
                sensitivityValue.Text == percent + "%",
                "Sensitivity JSON round trip changed saved models/options or lost " + percent + "%.");
            var arguments = (string[])typeof(HubForm).GetMethod("ControllerInputArguments", Private)!.Invoke(form, null)!;
            var gainIndex = Array.IndexOf(arguments, "-SwipeGain");
            Check(gainIndex >= 0 && decimal.Parse(arguments[gainIndex + 1], CultureInfo.InvariantCulture) == .22m * percent / 100m,
                "Swipe launch gain does not match the selected percentage.");
        }
        foreach (int? invalid in new int?[] { null, 0, 24, 26, 201, 301, int.MaxValue })
        {
            form.ApplyLiveOptions(form.CaptureLiveOptions() with { SwipeSensitivityPercent = invalid });
            Check(sensitivity.Value == 100 && sensitivityValue.Text == "100%", "Missing or invalid saved sensitivity did not use 100%.");
        }
        sensitivity.AccessibilityObject.Value = "126";
        Check(sensitivity.Value == 125 && sensitivityValue.Text == "125%", "Accessible slider changes did not snap or update the percentage.");
        typeof(DarkSlider).GetMethod("OnKeyDown", Private)!.Invoke(sensitivity, new object[] { new KeyEventArgs(Keys.Right) });
        Check(sensitivity.Value == 150, "Swipe arrow input did not use 25% steps.");
        typeof(DarkSlider).GetMethod("OnKeyDown", Private)!.Invoke(sensitivity, new object[] { new KeyEventArgs(Keys.Left) });
        Check(sensitivity.Value == 125, "Swipe reverse arrow input did not use 25% steps.");
        sensitivity.Value = 38;
        Check(sensitivity.Value == 50, "Swipe pointer/value quantization did not snap to the nearest 25% step.");
        var sliderWidth = sensitivity.Width;
        sensitivity.Value = 200; Pump(form);
        Check(sensitivity.Width == sliderWidth && sensitivityValue.Text == "200%", "Changing the percentage resized the slider track.");
        var smoothing = Field<DarkSlider>(form, "_smoothing");
        var smoothingBefore = smoothing.Value;
        var nextSmoothing = smoothingBefore == smoothing.Maximum ? smoothingBefore - 1 : smoothingBefore + 1;
        smoothing.Value = nextSmoothing;
        Check(smoothing.SmallChange == 1 && smoothing.Value == nextSmoothing,
            "Adding Swipe increments changed the existing Motion smoothing slider precision.");
        smoothing.Value = smoothingBefore;
        sensitivity.Value = 100;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var arguments = (string[])typeof(HubForm).GetMethod("ControllerInputArguments", Private)!.Invoke(form, null)!;
            Check(arguments[Array.IndexOf(arguments, "-SwipeGain") + 1] == "0.22", "Swipe gain used the local decimal separator in its launch argument.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        foreach (var otherMode in new[] { "Trackpad", "Relative joystick", "Desktop mouse" })
        {
            mode.SelectedItem = otherMode;
            Check(!((string[])typeof(HubForm).GetMethod("ControllerInputArguments", Private)!.Invoke(form, null)!).Contains("-SwipeGain"),
                "Sensitivity leaked into the " + otherMode + " launch.");
        }
        mode.SelectedItem = "Swipe";
        touchpad.Checked = false;
        Check(!((string[])typeof(HubForm).GetMethod("ControllerInputArguments", Private)!.Invoke(form, null)!).Contains("-SwipeGain") && !sensitivity.Enabled,
            "An unchecked thumb-rest feature still applies its sensitivity.");
        touchpad.Checked = true;
        mode.SelectedIndex = 0;
        side.SelectedIndex = 2;
        controls.OfType<Button>().Single(button => button.Text == "Open Facial tracking").PerformClick(); Pump(form);
        Check(PageOf(tongue).Visible && !PageOf(hands).Visible, "Open Facial tracking did not return to the source settings.");
        Field<HubEnvironment>(form, "_environment").SetPreviewTrackingSource(true);
        source.SelectedIndex = 1; Call(form, "UpdateTrackingSourceNotes"); Pump(form);
        var summary = controls.OfType<Label>().Single(label => label.AccessibleName == "Controller streaming app");
        Check(summary.Text.Contains("Steam Link") && summary.Text.Contains("require Virtual Desktop"), "Controller source summary did not follow Steam Link.");
        Check(!hands.Enabled && !touchpad.Enabled, "Steam Link enabled unsupported experimental controller inputs.");
        controllers.PerformClick(); Pump(form);
        CheckControllerSelectors();
        Check(!mode.Enabled && !side.Enabled && side.SelectedIndex == 2,
            "Steam Link enabled unsupported mode controls or discarded the selected controller side.");
        Field<HubEnvironment>(form, "_environment").SetPreviewTrackingSource(false);
        source.SelectedIndex = 0; Call(form, "UpdateTrackingSourceNotes"); Pump(form);
        Check(summary.Text.Contains("Streaming app: Virtual Desktop"), "Controller source summary did not return to Virtual Desktop.");
        CheckControllerSelectors();
        Check(!mode.Enabled && !side.Enabled && side.SelectedIndex == 2,
            "Returning to Virtual Desktop enabled an unchecked thumb-rest input or discarded its controller side.");
        touchpad.Checked = true; side.SelectedIndex = 0; Pump(form);
        form.ApplyTrackingHealthPreview();
        controllers.PerformClick(); Pump(form);
        Call(form, "HandleInferenceStatus", "Camera tracking", "TRACKING_HEALTH camera_fps=0 inference_fps=0 frame_age_ms=1 result_age_ms=1 pipeline_ms=5 dropped_frames=0 tongue=stale pupil=invalid");
        Check(!Field<bool>(form, "_cameraInputReady"), "Hidden Activity health allowed stale camera readiness to latch.");
        Call(form, "HandleInferenceStatus", "Camera tracking", "TRACKING_HEALTH camera_fps=24 inference_fps=24 frame_age_ms=1 result_age_ms=1 pipeline_ms=5 dropped_frames=0 tongue=hidden pupil=valid");
        Check(Field<bool>(form, "_cameraInputReady"), "Hidden Activity health gated fresh worker observations.");
        activity.PerformClick(); Pump(form);
        Check(PageOf(health).Visible && Field<Label>(form, "_healthFeatures").Text.Contains("Tongue: live"), "Activity did not render the latest hidden-page observation.");
        if (args.Length == 1) Directory.CreateDirectory(args[0]);
        foreach (var size in new[] { new Size(1280, 900), new Size(880, 700), new Size(1200, 820) })
        {
            form.Size = size; Pump(form);
            Check(PageOf(health).Visible, "Resizing changed the selected page.");
            Check(!PageOf(health).HorizontalScroll.Visible, "Activity introduced horizontal scrolling.");
            Check(log.ClientSize.Height >= 150 && health.Height > 100,
                $"Activity compressed health or log: window={size}, health={health.Height}, log={log.ClientSize.Height}, card={log.Parent!.Height}.");
            Check(health.Bottom + health.Margin.Bottom <= log.Parent!.Top, "Tracking health overlaps the log card.");
            Descendants(health).OfType<Button>().Single(button => button.Text == "Show tracking details").PerformClick(); Pump(form);
            Check(log.ClientSize.Height >= 150 && health.Bottom + health.Margin.Bottom <= log.Parent!.Top,
                "Expanded health details compressed or overlapped the diagnostic log.");
            Check(!PageOf(health).HorizontalScroll.Visible, "Expanded health details introduced horizontal scrolling.");
            Descendants(health).OfType<Button>().Single(button => button.Text == "Hide tracking details").PerformClick(); Pump(form);
            if (args.Length == 1)
            {
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(args[0], "activity-resize-" + size.Width + ".png"), ImageFormat.Png);
            }
            controllers.PerformClick(); Pump(form);
            CheckControllerSelectors();
            Check(!PageOf(hands).HorizontalScroll.Visible, "Controllers introduced horizontal scrolling.");
            Check(navigation.All(button => button.Visible && button.Height >= 40), "A resized sidebar clipped or hid navigation.");
            Check(productName.Visible && productName.ClientSize.Width >= TextRenderer.MeasureText(productName.Text, productName.Font).Width,
                "The public product name is clipped after resizing.");
            activity.PerformClick(); Pump(form);
        }
        Check(handles.SequenceEqual(new[] { tongue, hands, touchpad, (Control)health, log, mode, side, sensitivity, source, start, stop }.Select(control => control.Handle)),
            "Navigation/resizing recreated tracking controls or native handles.");
        Check(form.CaptureLiveOptions().TouchpadMode == "Trackpad" && form.CaptureLiveOptions().TouchpadSide == "Both", "Layout changes modified saved controller options.");
        Console.WriteLine($"PASS: {_checks} page routing, source, hidden health, resize and control-preservation checks.");
        form.Close();
    }
}
