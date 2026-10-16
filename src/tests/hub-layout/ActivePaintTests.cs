using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class ActivePaintTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRectangle rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint word, nint value);

    private sealed record Frame(double Milliseconds, int Phase, bool OperationBefore, bool OperationAfter,
        Rectangle Bounds, Bitmap Image, double? OperationEndMilliseconds = null);
    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void Pump(int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); }
    }
    private static Rectangle? ClientBounds(nint window)
    {
        if (IsIconic(window) || !GetClientRect(window, out var native)) return null;
        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin) || Math.Abs(origin.X) > 10000 || Math.Abs(origin.Y) > 10000) return null;
        var bounds = new Rectangle(origin.X, origin.Y, native.Right - native.Left, native.Bottom - native.Top);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    internal static void Run(string output, bool liveOnly = false)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "fixture-root");
        Directory.CreateDirectory(root);
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true) { TopMost = true };
        form.Text = "Qpro V3 active paint sampler - sample data only";
        form.Show();
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        Pump(300);
        var controls = Descendants(form).ToArray();
        var tabs = controls.OfType<Button>().Where(button => Equals(button.Tag, "workflow-tab")).ToArray();
        var log = controls.OfType<RichTextBox>().Single();
        var append = typeof(HubForm).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string)], null)!;
        var sizing = typeof(HubForm).GetField("_interactiveResize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handles = controls.Where(control => control.IsHandleCreated).ToDictionary(control => control, control => control.Handle);
        var sequence = 0;
        var layouts = controls.ToDictionary(control => control, _ => 0);
        var paints = controls.ToDictionary(control => control, _ => 0);
        var invalidations = controls.ToDictionary(control => control, _ => 0);
        foreach (var control in controls)
        {
            control.Layout += (_, _) => layouts[control]++;
            control.Paint += (_, _) => paints[control]++;
            control.Invalidated += (_, _) => invalidations[control]++;
        }
        using var traffic = new System.Windows.Forms.Timer { Interval = 35 };
        traffic.Tick += (_, _) => append.Invoke(form, ["Offline active-paint output " + sequence++]);
        traffic.Start();
        var report = Path.Combine(output, "active-state.txt");
        File.WriteAllText(report, "Full-client framebuffer samples during simulated native size/move and early OS restore. No content regions excluded; dynamic frames require visual inspection. PNG encoding/pixel scanning occurs after the interval. Sampling targets 20Hz and can itself affect CPU/latency. Physical mouse drag remains unverified.\n");

        var phase = 0;
        var operationInProgress = 0;
        Action? captureCompleted = null;
        void Interval(string name, Action<List<double>> actions, int frameLimit = 52)
        {
            Volatile.Write(ref phase, 0);
            foreach (var control in controls) { layouts[control] = paints[control] = invalidations[control] = 0; }
            var directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            var frames = new List<Frame>();
            var completed = new List<Frame>();
            var operationEnds = new List<object>();
            var sampledFrames = 0;
            var geometry = new List<object>();
            var timings = new List<double>();
            var start = Stopwatch.StartNew();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            using var cancellation = new CancellationTokenSource();
            var window = form.Handle;
            captureCompleted = () =>
            {
                var ended = start.Elapsed.TotalMilliseconds;
                var currentPhase = Volatile.Read(ref phase);
                var bounds = ClientBounds(window);
                operationEnds.Add(new { phase = currentPhase, endedMs = ended, state = form.WindowState.ToString(), bounds });
                if (bounds is not { } rectangle) return;
                // Read the first completed framebuffer immediately, without a
                // pump, invalidation, synchronous flush or deliberate delay.
                var image = new Bitmap(rectangle.Width, rectangle.Height);
                using (var graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
                completed.Add(new Frame(start.Elapsed.TotalMilliseconds, currentPhase, false, false, rectangle, image, ended));
            };
            var sampler = Task.Run(() =>
            {
                var phaseFrames = new Dictionary<int, int>();
                var lastFrameIndexes = new Dictionary<int, int>();
                while (!cancellation.IsCancellationRequested)
                {
                    var currentPhase = Volatile.Read(ref phase);
                    var operationBefore = Volatile.Read(ref operationInProgress) != 0;
                    var count = phaseFrames.GetValueOrDefault(currentPhase);
                    var bounds = ClientBounds(window);
                    if ((frames.Count < frameLimit || count > 0) && bounds is { } rectangle)
                    {
                        var image = new Bitmap(rectangle.Width, rectangle.Height);
                        using (var graphics = Graphics.FromImage(image))
                            graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
                        var operationAfter = Volatile.Read(ref operationInProgress) != 0;
                        if (ClientBounds(window) == bounds && Volatile.Read(ref phase) == currentPhase)
                        {
                            sampledFrames++;
                            var frame = new Frame(start.Elapsed.TotalMilliseconds, currentPhase, operationBefore, operationAfter, rectangle, image);
                            if (count < 2)
                            {
                                lastFrameIndexes[currentPhase] = frames.Count;
                                frames.Add(frame);
                                phaseFrames[currentPhase] = count + 1;
                            }
                            else
                            {
                                // Keep the early and latest frame of every step,
                                // rather than ending coverage after its first 100ms.
                                var index = lastFrameIndexes[currentPhase];
                                frames[index].Image.Dispose();
                                frames[index] = frame;
                            }
                        }
                        else image.Dispose();
                    }
                    cancellation.Token.WaitHandle.WaitOne(50);
                }
            });
            void CaptureGeometry(object? sender, EventArgs arguments)
            {
                geometry.Add(new { timeMs = start.Elapsed.TotalMilliseconds, client = form.ClientSize,
                    state = form.WindowState.ToString(), controls = controls.Where(control => control.Visible && control is ComboBox)
                        .Select(control => new { type = control.GetType().Name, text = control.Text,
                            bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle)) }).ToArray() });
            }
            form.Resize += CaptureGeometry;
            actions(timings);
            captureCompleted = null;
            Pump(160);
            cancellation.Cancel();
            sampler.GetAwaiter().GetResult();
            form.Resize -= CaptureGeometry;
            var intervalMs = start.Elapsed.TotalMilliseconds;
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            if (frames.Count == 0) throw new Exception("No displayed frames were captured: " + name);
            frames = frames.Concat(completed).OrderBy(frame => frame.Milliseconds).ToList();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                frame.Image.Save(Path.Combine(directory, $"frame-{index:D2}.png"), ImageFormat.Png);
                // Record broad black bands for diagnosis, not as an assertion
                // against legitimate native controls or DWM transition effects.
                var maximumBlackRun = 0;
                var data = frame.Image.LockBits(new Rectangle(Point.Empty, frame.Image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var pixels = new int[data.Width * data.Height];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    for (var y = 4; y < data.Height - 4; y++)
                    {
                        var run = 0;
                        for (var x = 4; x < data.Width - 4; x++)
                        {
                            run = (pixels[y * data.Width + x] & 0x00ffffff) == 0 ? run + 1 : 0;
                            maximumBlackRun = Math.Max(maximumBlackRun, run);
                        }
                    }
                }
                finally { frame.Image.UnlockBits(data); }
                File.AppendAllText(Path.Combine(directory, "frames.txt"),
                    $"frame-{index:D2}: phase={frame.Phase}; capture={(frame.OperationEndMilliseconds.HasValue ? "immediate-completed" : "sample")}; operationEndMs={frame.OperationEndMilliseconds?.ToString("F1") ?? "-"}; operationInProgressBefore={frame.OperationBefore}; operationInProgressAfter={frame.OperationAfter}; t={frame.Milliseconds:F1}ms; screenClient={frame.Bounds}; blackRun={maximumBlackRun}; targetCorner={frame.Image.GetPixel(5, 5)}\n");
                frame.Image.Dispose();
            }
            timings.Sort();
            var median = timings.Count == 0 ? 0 : timings[timings.Count / 2];
            var p95 = timings.Count == 0 ? 0 : timings[Math.Min(timings.Count - 1, (int)Math.Ceiling(timings.Count * 0.95) - 1)];
            var gaps = frames.Skip(1).Select((frame, index) => frame.Milliseconds - frames[index].Milliseconds).ToArray();
            File.WriteAllText(Path.Combine(directory, "geometry.json"), JsonSerializer.Serialize(geometry, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(directory, "operation-ends.json"), JsonSerializer.Serialize(operationEnds, new JsonSerializerOptions { WriteIndented = true }));
            var summary = $"{name}: retainedFrames={frames.Count}; sampledFrames={sampledFrames}; immediateCompletedFrames={completed.Count}; completedOperationFrames={frames.Count(frame => !frame.OperationBefore && !frame.OperationAfter)}; phases={string.Join(',', frames.Select(frame => frame.Phase).Distinct())}; actionInterval={intervalMs:F1}ms; firstFrame={frames[0].Milliseconds:F1}ms; lastFrame={frames[^1].Milliseconds:F1}ms; meanRetainedFrameGap={(gaps.Length == 0 ? 0 : gaps.Average()):F1}ms; processCPU={cpuMs:F1}ms; sizeSetterMedian={median:F1}ms; sizeSetterP95={p95:F1}ms; logLines={sequence}\n";
            File.AppendAllText(report, summary);
            File.WriteAllText(Path.Combine(directory, "control-events.txt"),
                $"Layout={layouts.Values.Sum()}; Paint={paints.Values.Sum()}; Invalidated={invalidations.Values.Sum()}\n" +
                string.Join("\n", controls.OrderByDescending(control => paints[control] + layouts[control]).Take(15)
                    .Select(control => $"{control.GetType().Name} parent={control.Parent?.GetType().Name} text={control.Text}: layout={layouts[control]}; paint={paints[control]}; invalidated={invalidations[control]}")));
            Console.Write(summary);
            if (handles.Any(pair => !pair.Key.IsHandleCreated || pair.Key.Handle != pair.Value))
                throw new Exception("Active transitions recreated a native handle.");
        }

        void SizeLoop(List<double> timings)
        {
            SendMessage(form.Handle, 0x0231, 0, 0);
            if (!(bool)sizing.GetValue(form)!) throw new Exception("Native resize begin did not defer optional reflow.");
            for (var step = 0; step < 24; step++)
            {
                var width = step < 12 ? 1140 - step * 30 : 810 + (step - 12) * 30;
                Volatile.Write(ref operationInProgress, 1);
                Interlocked.Increment(ref phase);
                var elapsed = Stopwatch.StartNew();
                form.Size = new Size(width, width < 900 ? 620 : 850);
                timings.Add(elapsed.Elapsed.TotalMilliseconds);
                Volatile.Write(ref operationInProgress, 0);
                captureCompleted?.Invoke();
                Pump(25);
            }
            SendMessage(form.Handle, 0x0232, 0, 0);
            if ((bool)sizing.GetValue(form)!) throw new Exception("Native resize end did not resume optional reflow.");
        }
        tabs.Single(tab => tab.Text == "Facial tracking").PerformClick();
        foreach (var name in new[] { "connection and camera settings", "eye settings and guidance", "camera settings", "cheek adjustment details", "hand and controller details" })
            controls.OfType<Button>().Single(button => button.Text == "Show " + name).PerformClick();
        Pump(250);
        var page = controls.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
        page.AutoScrollPosition = new Point(0, 230);
        Pump(120);
        Interval("live-active-resize", SizeLoop);
        if (liveOnly) { traffic.Stop(); form.Close(); return; }
        var viewport = page.RectangleToScreen(page.ClientRectangle);
        var combo = controls.OfType<ComboBox>().First(box => box.Visible && box.Enabled && box.Items.Count > 1
            && viewport.Contains(box.RectangleToScreen(box.ClientRectangle)));
        var originalChoice = combo.SelectedIndex;
        combo.DroppedDown = true;
        Pump(120);
        if (!combo.DroppedDown) throw new Exception("Native drop-down did not open.");
        var popupBounds = form.RectangleToScreen(form.ClientRectangle);
        using (var popup = new Bitmap(popupBounds.Width, popupBounds.Height))
        {
            using (var graphics = Graphics.FromImage(popup)) graphics.CopyFromScreen(popupBounds.Location, Point.Empty, popupBounds.Size);
            popup.Save(Path.Combine(output, "native-drop-down-open.png"), ImageFormat.Png);
        }
        combo.DroppedDown = false;
        combo.SelectedIndex = (originalChoice + 1) % combo.Items.Count;
        Pump(100);
        if (combo.SelectedIndex == originalChoice || combo.DroppedDown) throw new Exception("Native drop-down selection/close failed.");
        combo.SelectedIndex = originalChoice;
        File.AppendAllText(report, "Native ComboBox popup open/close plus sample choice assignment passed; original sample choice restored. Physical menu selection is unverified.\n");
        Interval("early-minimize-maximize-restore", timings =>
        {
            Volatile.Write(ref operationInProgress, 1);
            Interlocked.Increment(ref phase);
            form.WindowState = FormWindowState.Minimized;
            Volatile.Write(ref operationInProgress, 0);
            captureCompleted?.Invoke();
            Pump(80);
            Volatile.Write(ref operationInProgress, 1);
            Interlocked.Increment(ref phase);
            form.WindowState = FormWindowState.Maximized;
            Volatile.Write(ref operationInProgress, 0);
            captureCompleted?.Invoke();
            Pump(350);
            Volatile.Write(ref operationInProgress, 1);
            Interlocked.Increment(ref phase);
            form.WindowState = FormWindowState.Normal;
            Volatile.Write(ref operationInProgress, 0);
            captureCompleted?.Invoke();
            Pump(350);
        }, frameLimit: 16);
        tabs.Single(tab => tab.Text == "Activity").PerformClick();
        for (var index = 0; index < 160; index++) append.Invoke(form, ["Offline native interaction fixture " + index]);
        log.Select(log.GetFirstCharIndexFromLine(10), 16);
        tabs.Single(tab => tab.Text == "Activity").Focus();
        SendMessage(log.Handle, 0xB6, 0, 30);
        var reader = (log.SelectionStart, log.SelectionLength);
        var firstLine = SendMessage(log.Handle, 0xCE, 0, 0);
        Interval("activity-active-resize", SizeLoop);
        form.Size = new Size(1140, 850);
        Pump(200);
        if (reader != (log.SelectionStart, log.SelectionLength) || firstLine != SendMessage(log.Handle, 0xCE, 0, 0))
            throw new Exception("Active resize/log traffic changed the native reader position.");
        traffic.Stop();
        form.Close();
    }
}
