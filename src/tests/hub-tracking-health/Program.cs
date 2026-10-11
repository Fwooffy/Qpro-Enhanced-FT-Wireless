using System.ComponentModel;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using QproFaceTracking.Hub;

internal static class TrackingHealthTests
{
    private const string Healthy = "TRACKING_HEALTH camera_fps=23.8 inference_fps=23.6 frame_age_ms=4.1 result_age_ms=1.2 pipeline_ms=8.6 decode_ms=0.7 preprocess_ms=1.0 inference_ms=5.4 transfer_ms=0.5 dropped_frames=2 pupil_dropped_frames=1 pupil_age_ms=40 pupil_ms=7.2 tongue=hidden cheek=valid pupil=valid";
    private static void Check(bool okay, string message) { if (!okay) throw new Exception(message); }
    private static T Field<T>(HubForm form, string name) => (T)typeof(HubForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void SetField(HubForm form, string name, object value) => typeof(HubForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
    private static void Call(HubForm form, string name, params object[] args) => typeof(HubForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Pump(int milliseconds = 100)
    {
        var end = Environment.TickCount64 + milliseconds;
        do { Application.DoEvents(); Thread.Sleep(5); } while (Environment.TickCount64 < end);
    }

    private static void StateTests()
    {
        double now = 100;
        var health = new HubTrackingHealth(() => now);
        Check(health.CameraState == TrackingSignalState.Off, "Default camera state should be off.");
        health.Start(true, true, true);
        Check(health.CameraState == TrackingSignalState.Waiting && health.FeatureState("tongue") == TrackingSignalState.Waiting, "Startup assumed live frames.");
        Check(health.Observe("INFERENCE_BACKEND feature=tongue backend=amd-rocm device=cuda:0 name=AMD Radeon RX 7900 XTX"), "Backend identity was not parsed.");
        Check(health.LowerFaceBackend == "AMD ROCm · AMD Radeon RX 7900 XTX" && !health.CameraFresh, "GPU selection incorrectly proved camera readiness.");
        health.Observe("TRANSPORT_QUALITY cameras=4 fps=24.0 source_skips=4 extra_queue_p95_ms=2.1");
        Check(health.CameraFresh && health.FeatureState("tongue") == TrackingSignalState.Waiting, "Legacy transport should prove frames only.");
        now += 6.6;
        Check(!health.CameraFresh && health.CameraState == TrackingSignalState.Stale, "Legacy camera readiness latched after its report expired.");
        Check(health.Observe(Healthy) && health.CameraFresh, "Fresh health did not recover after a stall.");
        Check(health.FeatureState("tongue") == TrackingSignalState.Live && health.FeatureState("cheek") == TrackingSignalState.Live, "Hidden tongue is a valid neutral output, not a stale sample.");
        Check(health.LowerFaceState == TrackingSignalState.Live && health.LowerFaceStatusText == "Live", "Valid selected lower-face outputs did not report live.");
        health.Observe(Healthy.Replace("tongue=hidden", "tongue=warming"));
        Check(health.CameraFresh && health.SnapshotFresh && health.LowerFaceState == TrackingSignalState.Waiting
            && health.LowerFaceStatusText == "Waiting for output", "A current camera snapshot incorrectly reported a warming selected tongue as live.");
        health.Observe(Healthy.Replace("cheek=valid", "cheek=stale"));
        Check(health.LowerFaceState == TrackingSignalState.Stale && health.LowerFaceStatusText == "Stale output",
            "A live tongue masked its selected stale cheek output.");
        var tongueOnly = new HubTrackingHealth(() => now);
        tongueOnly.Start(true, false, false);
        tongueOnly.Observe(Healthy.Replace("cheek=valid", "cheek=stale"));
        Check(tongueOnly.LowerFaceState == TrackingSignalState.Live, "An unselected cheek output changed tongue-only status.");
        var cheeksOnly = new HubTrackingHealth(() => now);
        cheeksOnly.Start(false, true, false);
        cheeksOnly.Observe(Healthy.Replace("tongue=hidden", "tongue=warming"));
        Check(cheeksOnly.LowerFaceState == TrackingSignalState.Live, "An unselected tongue output changed cheek-only status.");
        health.Observe(Healthy);
        Check(health.PupilDroppedFrames == 1 && health.DroppedFrames == 2 && health.SourceSkips == 4 && health.PipelineMs == 8.6, "Telemetry metrics were lost.");
        now += 3.6;
        Check(!health.CameraFresh && health.FeatureState("pupil") == TrackingSignalState.Stale, "One-second health snapshot did not expire.");
        health.Observe("TRANSPORT_QUALITY fps=24");
        Check(!health.CameraFresh, "Old transport report resurrected an expired modern snapshot.");
        Check(!health.Observe(Healthy.Replace("23.8", "NaN")) && !health.Observe(Healthy.Replace("8.6", "-2"))
            && !health.Observe(Healthy.Replace("dropped_frames=2", "dropped_frames=9223372036854775808"))
            && !health.Observe("TRACKING_HEALTH camera_fps=24"), "Malformed telemetry was accepted.");
        Check(!health.CameraFresh, "Malformed telemetry refreshed camera readiness.");
        health.Observe(Healthy.Replace("pupil=valid", "pupil=invalid"));
        Check(health.FeatureState("pupil") == TrackingSignalState.Invalid && health.CameraFresh, "Invalid pupils should not invalidate lower-face frames.");
        health.Observe(Healthy.Replace("pupil_age_ms=40", "pupil_age_ms=3400"));
        now += .2;
        Check(health.FeatureState("pupil") == TrackingSignalState.Stale && health.FeatureState("tongue") == TrackingSignalState.Live, "Pupil age did not include elapsed PC time.");
        health.Observe(Healthy.Replace("frame_age_ms=4.1", "frame_age_ms=3400"));
        now += .2;
        Check(!health.CameraFresh, "Frame age did not include elapsed PC time.");
        health.Observe(Healthy.Replace("camera_fps=23.8", "camera_fps=0"));
        Check(!health.CameraFresh, "Zero camera FPS remained green.");
        health.Observe(Healthy);
        health.CameraExited(true);
        Check(health.CameraState == TrackingSignalState.Error && !health.Observe(Healthy), "Late data reactivated a failed worker.");
        health.Stop();
        Check(health.CameraState == TrackingSignalState.Stopped && health.CameraFps is null && health.DroppedFrames is null
            && !health.Observe(Healthy), "Stop retained current metrics or accepted late data.");
        health.Start(false, false, true);
        Check(!health.HasSnapshot && health.FeatureState("tongue") == TrackingSignalState.Off && health.PupilBackend == "Waiting for backend", "Restart retained previous session observations.");
        var pupilOnly = "TRACKING_HEALTH camera_fps=30.0 inference_fps=28.0 frame_age_ms=0.2 decode_ms=0.004 preprocess_ms=-1.000 inference_ms=-1.0 transfer_ms=-1.000 pipeline_ms=-1.0 result_age_ms=-1.0 dropped_frames=0 tongue=off cheek=off pupil=valid pupil_ms=11.0 pupil_age_ms=23.0 pupil_dropped_frames=1";
        var pupilWarming = "TRACKING_HEALTH camera_fps=30.0 inference_fps=0.0 frame_age_ms=0.2 decode_ms=0.004 preprocess_ms=-1.000 inference_ms=-1.0 transfer_ms=-1.000 pipeline_ms=-1.0 result_age_ms=-1.0 dropped_frames=0 tongue=off cheek=off pupil=warming pupil_ms=-1.0 pupil_age_ms=-1.0 pupil_dropped_frames=0";
        Check(health.Observe(pupilWarming) && health.CameraFresh && health.InferenceFps == 0
            && health.FeatureState("pupil") == TrackingSignalState.Waiting && health.PupilMs is null,
            "Receiver initial warmup did not show live frames while waiting for pupil output.");
        Check(health.Observe(pupilOnly) && health.CameraFresh && health.PipelineMs is null && health.InferenceMs is null
            && health.FeatureState("pupil") == TrackingSignalState.Live, "Receiver pupil-only unavailable timings invalidated live pupils.");
        now += 3.6;
        Check(!health.CameraFresh && health.FeatureState("pupil") == TrackingSignalState.Stale, "Pupil-only receiver snapshot failed to expire.");
        health.Observe("PUPIL_STATUS relative_mm left=3.40 right=3.50 processing_ms=7.4 age_ms=3501 dropped_frames=3");
        Check(health.FeatureState("pupil") == TrackingSignalState.Stale, "Old pupil estimate appeared live.");
        health.Stop(); health.Select(false, false, false);
        Check(health.CameraState == TrackingSignalState.Off, "Disabled camera feature showed stopped/error.");
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            health.Start(true, false, false);
            Check(health.Observe(Healthy) && health.CameraFps == 23.8, "Machine telemetry depended on Windows decimal separator.");
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        var delayed = new HubTrackingHealth(() => now);
        delayed.Start(true, true, true);
        Check(delayed.Observe(Healthy, now - 4) && !delayed.CameraFresh,
            "A delayed UI callback treated stale pipe output as a new observation.");
        Check(delayed.Observe(Healthy) && !delayed.Observe(Healthy.Replace("tongue=hidden", "tongue=stale"), now - 1)
            && delayed.FeatureState("tongue") == TrackingSignalState.Live, "Out-of-order pipe output replaced a newer health observation.");
        Console.WriteLine("PASS monotonic startup/live/stall/recovery/off/stop/error and strict culture-independent parser");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        StateTests();
        if (args.Length == 1 && args[0] == "--state-only") return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.Dark);
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "qpro-health-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        using var form = new HubForm(fixtureRoot, rememberLaunch: false, previewOnly: true);
        form.Show();
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        var all = Descendants(form).ToArray();
        all.OfType<Button>().Single(button => Equals(button.Tag, "workflow-tab") && button.Text == "Live tracking").PerformClick();
        form.ApplyTrackingHealthPreview();
        Pump(250);
        var card = Field<TableLayoutPanel>(form, "_trackingHealthCard");
        var featureLabel = Field<Label>(form, "_healthFeatures");
        Check(featureLabel.Text.Contains("Tongue: live") && featureLabel.Text.Contains("pupils: live"), "UI fixture did not display valid sample outputs.");
        var timers = Field<IContainer>(form, "_uiComponents").Components.OfType<System.Windows.Forms.Timer>().ToArray();
        Check(timers.Length == 4, "Health added a periodic timer or hardware refresh.");
        var page = all.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
        foreach (var width in new[] { 1140, 780 })
        {
            form.Size = new Size(width, width == 780 ? 620 : 850);
            form.ApplyTrackingHealthPreview();
            page.AutoScrollPosition = Point.Empty;
            Pump(250);
            if (width == 780)
            {
                var offset = page.PointToClient(card.PointToScreen(Point.Empty)).Y - page.AutoScrollPosition.Y;
                page.AutoScrollPosition = new Point(0, Math.Max(0, offset - 12));
            }
            Pump(200);
            Check(!page.HorizontalScroll.Visible, "Health panel introduced horizontal scrolling.");
            foreach (var label in Descendants(card).OfType<Label>().Where(label => label.Visible))
            {
                Check(label.Right <= label.Parent!.ClientSize.Width - label.Parent.Padding.Right + 2, "Health text overflow: " + label.Text);
                Check(label.Height >= TextRenderer.MeasureText(label.Text, label.Font,
                    new Size(Math.Max(1, label.Width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height - 3,
                    $"Health text clipped ({label.Size}; max={label.MaximumSize}; preferred={label.PreferredSize}; measured={TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.Width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix)}): " + label.Text);
            }
            if (args.Length == 1)
            {
                Directory.CreateDirectory(args[0]);
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(args[0], "live-health-" + width + ".png"), ImageFormat.Png);
            }
            Console.WriteLine("PASS native health panel wraps without clipping at " + width);
        }
        all.OfType<Button>().Single(button => button.Text == "Show tracking details").PerformClick();
        Pump(250);
        Check(Field<Label>(form, "_healthStages").Text.Contains("prediction total")
            && Field<Label>(form, "_healthStages").Text.Contains("GPU wait/readback"), "Expanded timings implied isolated or summable GPU stages.");
        Check(!page.HorizontalScroll.Visible, "Expanded health timings introduced horizontal scrolling.");
        if (args.Length == 1)
        {
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(args[0], "live-health-expanded-780.png"), ImageFormat.Png);
        }
        all.OfType<Button>().Single(button => button.Text == "Hide tracking details").PerformClick();
        Pump(150);
        var oldRates = Field<Label>(form, "_healthRates").Text;
        var oldFeatures = featureLabel.Text;
        SetField(form, "_interactiveResize", true);
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy.Replace("camera_fps=23.8", "camera_fps=0").Replace("tongue=hidden", "tongue=stale"));
        Check(!Field<bool>(form, "_cameraInputReady") && Field<Label>(form, "_runStatus").Text.Contains("fresh camera frames")
            && Field<HubTrackingHealth>(form, "_trackingHealth").CameraState == TrackingSignalState.Stale,
            "Interactive resizing deferred authoritative camera observations or readiness.");
        Check(Field<Label>(form, "_healthRates").Text == oldRates && featureLabel.Text == oldFeatures,
            "Optional health decorations changed during an interactive corner drag.");
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy.Replace("pupil=valid", "pupil=stale"));
        Check(Field<bool>(form, "_cameraInputReady") && Field<Label>(form, "_runStatus").Text.Contains("Camera input received")
            && featureLabel.Text == oldFeatures, "Camera recovery was deferred along with metric decoration during resize.");
        Call(form, "OnResizeEnd", EventArgs.Empty);
        Check(!Field<bool>(form, "_interactiveResize") && featureLabel.Text.Contains("pupils: stale"),
            "ResizeEnd failed to render the latest deferred health state.");
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy);
        Pump(150);
        Console.WriteLine("PASS interactive resize retains authoritative stale/recovery readiness and flushes decoration at ResizeEnd");
        var handles = all.Where(control => control.Visible && control.IsHandleCreated).ToDictionary(control => control, control => control.Handle);
        var position = page.AutoScrollPosition;
        var previous = featureLabel.Text;
        form.WindowState = FormWindowState.Minimized; Pump();
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy.Replace("pupil=valid", "pupil=stale"));
        Pump();
        Check(featureLabel.Text == previous, "Health labels painted while minimized.");
        form.WindowState = FormWindowState.Normal; Pump(250);
        Check(featureLabel.Text.Contains("pupils: stale"), "Restoring failed to refresh current health.");
        Check(page.AutoScrollPosition == position && handles.All(pair => pair.Key.Handle == pair.Value), "Health restore rebuilt controls or moved scroll.");
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy.Replace("camera_fps=23.8", "camera_fps=0"));
        Check(!Field<bool>(form, "_cameraInputReady") && Field<Label>(form, "_runStatus").Text.Contains("fresh camera frames"), "Displayed camera readiness latched after zero frames.");
        SetField(form, "_gazeFailureHandled", true);
        SetField(form, "_gazeRecoveryConfirmed", false);
        var status = Field<Label>(form, "_runStatus");
        status.Text = "● Gaze recovery unconfirmed — check Activity";
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy);
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy.Replace("camera_fps=23.8", "camera_fps=0"));
        Check(status.Text.Contains("Gaze recovery unconfirmed"), "Camera health erased unconfirmed gaze recovery.");
        SetField(form, "_gazeRecoveryConfirmed", true);
        SetField(form, "_gazeRecoveryRunning", true);
        status.Text = "● Gaze recovery in progress";
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy);
        Check(status.Text.Contains("Gaze recovery in progress"), "Camera health erased gaze recovery in progress.");
        SetField(form, "_gazeRecoveryRunning", false);
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy.Replace("camera_fps=23.8", "camera_fps=0"));
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy);
        Check(status.Text.Contains("Camera input received") && status.Text.Contains("Hub gaze off"), "Confirmed gaze recovery did not allow camera readiness to resume.");
        Call(form, "StopTrackingHealth");
        Call(form, "HandleInferenceStatus", "Camera tracking", Healthy);
        Check(!Field<bool>(form, "_cameraInputReady") && featureLabel.Text.Contains("Tongue: stopped"), "Late output reactivated stopped UI.");
        var layouts = 0;
        card.Layout += (_, _) => layouts++;
        Pump(250); layouts = 0;
        for (var line = 0; line < 80; line++) Call(form, "HandleInferenceStatus", "Camera tracking", "unrelated log line " + line);
        for (var pass = 0; pass < 10; pass++) Call(form, "RefreshTrackingHealth");
        Pump(150);
        Check(layouts == 0, "Unchanged or unrelated output churned health layout.");
        Console.WriteLine("PASS minimized rendering, restore/scroll/handle stability, stale footer, late-output rejection and idle layout");
        form.Close();
    }
}
