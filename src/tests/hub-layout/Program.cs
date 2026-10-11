using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Reflection;
using System.ComponentModel;
using QproFaceTracking.Hub;

internal static class LayoutTests
{
    [DllImport("user32.dll")]
    static extern nint SendMessage(nint window, int message, nint word, nint value);
    [STAThread]
    static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }

    static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    static void Pump(int milliseconds = 150)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }

    static void Run(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetColorMode(SystemColorMode.Dark);
        if (args.Length == 2 && args[0] == "--paint-presentation")
        {
            PresentationPaintTests.Run(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--paint-active")
        {
            ActivePaintTests.Run(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--paint-active-live")
        {
            ActivePaintTests.Run(args[1], liveOnly: true);
            return;
        }
        if (args.Length == 2 && args[0] == "--paint-check")
        {
            DisplayedPaintTests.Run(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--paint-fixture")
        {
            RunPaintFixture(args[1]);
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "qpro-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // A cancelled launch can dispose a form without ever closing a window.
        // Verify native timer ownership in that path, not just normal Close.
        using (var abandoned = new HubForm(root, rememberLaunch: false, previewOnly: true))
        {
            var components = (IContainer)typeof(HubForm).GetField("_uiComponents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(abandoned)!;
            var timers = components.Components.OfType<System.Windows.Forms.Timer>().ToArray();
            if (timers.Length != 4) throw new Exception("The Hub did not own all periodic/layout timers.");
            var disposedTimers = 0;
            foreach (var timer in timers) { timer.Disposed += (_, _) => disposedTimers++; timer.Start(); }
            var closed = false;
            abandoned.FormClosed += (_, _) => closed = true;
            abandoned.Dispose();
            if (closed || disposedTimers != timers.Length || timers.Any(timer => timer.Enabled))
                throw new Exception("Disposing a never-shown Hub retained a native timer.");
            abandoned.FlushPreviewLayout();
            Console.WriteLine("PASS direct disposal releases all native timers without FormClosed");
        }
        Console.WriteLine("Constructing offline layout fixture");
        using (var initiallyMinimized = new HubForm(root, rememberLaunch: false, previewOnly: true)
            { WindowState = FormWindowState.Minimized })
        {
            initiallyMinimized.Show(); Pump(100);
            var minimizedHandles = Descendants(initiallyMinimized).Where(control => control.IsHandleCreated)
                .ToDictionary(control => control, control => control.Handle);
            initiallyMinimized.WindowState = FormWindowState.Normal; Pump(150);
            if (initiallyMinimized.Controls.Cast<Control>().Any(control => control.Width <= 0)
                || minimizedHandles.Any(pair => pair.Key.Handle != pair.Value))
                throw new Exception("Initial minimize/restore did not restore usable root layout and native identity.");
            initiallyMinimized.WindowState = FormWindowState.Minimized; Pump(100);
            var minimizedComponents = (IContainer)typeof(HubForm).GetField("_uiComponents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(initiallyMinimized)!;
            var minimizedTimers = minimizedComponents.Components.OfType<System.Windows.Forms.Timer>().ToArray();
            initiallyMinimized.Dispose();
            if (minimizedTimers.Any(timer => timer.Enabled))
                throw new Exception("Minimized disposal retained an enabled native timer.");
            Console.WriteLine("PASS initial minimized show/restore preserves native identity; minimized disposal releases timers");
        }
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
        Console.WriteLine("Showing offline layout fixture");
        form.Show();
        Console.WriteLine("Applying sample data");
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        Pump(500);
        var all = Descendants(form).ToArray();
        var layouts = 0;
        foreach (var control in all) control.Layout += (_, _) => layouts++;
        var tabs = all.OfType<Button>().Where(button => Equals(button.Tag, "workflow-tab")).ToArray();
        foreach (var tab in tabs)
        {
            form.Size = new Size(780, 620);
            tab.PerformClick(); Pump(200);
            var page = all.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
            if (page.HorizontalScroll.Visible) throw new Exception("First show at a narrow width introduced horizontal scrolling.");
            form.Size = new Size(1140, 850); Pump(200);
            page.AutoScrollPosition = new Point(0, Math.Min(180, page.VerticalScroll.Maximum));
            Pump();
            var handles = all.Where(control => control.Visible && control.IsHandleCreated)
                .ToDictionary(control => control, control => control.Handle);
            var position = page.AutoScrollPosition;
            var geometry = all.Where(control => control.Visible).ToDictionary(control => control, control => control.Bounds);
            var wraps = all.OfType<Label>().Where(label => Equals(label.Tag, "responsive-info"))
                .ToDictionary(label => label, label => label.MaximumSize);
            layouts = 0;
            form.WindowState = FormWindowState.Minimized;
            Pump(300);
            if (wraps.Any(pair => pair.Key.MaximumSize != pair.Value))
                throw new Exception("Minimize changed a text wrapping width.");
            var minimizedLayouts = layouts;
            layouts = 0;
            form.WindowState = FormWindowState.Normal;
            Pump(500);
            Console.WriteLine($"RESTORE {tab.Text}: minLayouts={minimizedLayouts}; restoreLayouts={layouts}; scroll={position}->{page.AutoScrollPosition}; client={form.ClientSize}");
            if (page.AutoScrollPosition != position) throw new Exception("Restore changed " + tab.Text + " scroll position.");
            if (handles.Any(pair => !pair.Key.IsHandleCreated || pair.Key.Handle != pair.Value))
                throw new Exception("Restore rebuilt a native control.");
            if (geometry.Any(pair => pair.Key.Visible && pair.Key.Bounds != pair.Value))
                throw new Exception("Restore changed settled control bounds on " + tab.Text);
            layouts = 0; Pump(300);
            if (layouts != 0) throw new Exception("Idle layout work did not settle: " + layouts);
            Console.WriteLine("PASS restore settles without rebuilding controls: " + tab.Text);

            // Exercise the native enter/exit sizing notifications. This remains
            // a programmatic native size loop, not a physical mouse test.
            SendMessage(form.Handle, 0x0231, 0, 0); // WM_ENTERSIZEMOVE
            foreach (var width in new[] { 1000, 900, 820, 780, 900, 1040, 1140 })
            {
                form.Size = new Size(width, width < 900 ? 620 : 850);
                Pump(80);
                if (page.Width != page.Parent!.ClientSize.Width)
                    throw new Exception("Page stopped fitting the window during resizing.");
            }
            SendMessage(form.Handle, 0x0232, 0, 0); // WM_EXITSIZEMOVE
            Pump(300);
            layouts = 0; Pump(300);
            if (layouts != 0) throw new Exception("Simulated resize layout did not settle: " + layouts);
            if (page.HorizontalScroll.Visible) throw new Exception("Window resize introduced a horizontal page scrollbar.");
            Console.WriteLine("PASS native size loop round trip settles without a horizontal scrollbar: " + tab.Text);
            if (args.Length == 1)
            {
                Directory.CreateDirectory(args[0]);
                foreach (var width in new[] { 780, 1140 })
                {
                    form.Size = new Size(width, width == 780 ? 620 : 850);
                    page.AutoScrollPosition = Point.Empty;
                    Pump(350);
                    if (page.HorizontalScroll.Visible) throw new Exception("Narrow " + tab.Text + " page requires horizontal scrolling.");
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(args[0], tab.Text.Replace(' ', '-') + "-" + width + ".png"), ImageFormat.Png);
                }
            }
        }
        // Place one real page just on either side of its scrollbar threshold.
        // A fixed outer width must keep the same wrapping budget in both states,
        // and delayed fitting must settle instead of alternating label widths.
        tabs.Single(tab => tab.Text == "Model manager").PerformClick();
        // At the shortest supported window, this page's list is at its useful
        // minimum height rather than filling any extra viewport space.
        form.Size = new Size(1140, 580); Pump(200);
        var thresholdPage = all.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
        thresholdPage.AutoScrollPosition = Point.Empty;
        // Keep the existing list at that height for this threshold test; its
        // normal viewport-filling behavior would move the threshold with us.
        var thresholdContent = thresholdPage.Controls.Cast<Control>().Single(control => control.Visible);
        var originalMaximum = thresholdContent.MaximumSize;
        thresholdContent.MaximumSize = new Size(0, thresholdContent.Height);
        var contentHeight = thresholdContent.Bottom + thresholdContent.Margin.Bottom
            + thresholdPage.Padding.Bottom;
        var originalExtent = thresholdPage.AutoScrollMinSize;
        thresholdPage.AutoScrollMinSize = new Size(0, contentHeight);
        var thresholdLabels = all.OfType<Label>().Where(label => label.Visible && Equals(label.Tag, "responsive-info")).ToArray();
        if (thresholdLabels.Length == 0) throw new Exception("Scrollbar threshold fixture has no responsive text.");
        var thresholdWraps = new Dictionary<Label, Size>();
        var scrollbarStates = new HashSet<bool>();
        foreach (var allowance in new[] { 32, -32, 32, -32, 32 })
        {
            form.Height += contentHeight + allowance - thresholdPage.ClientSize.Height;
            Pump(200);
            scrollbarStates.Add(thresholdPage.VerticalScroll.Visible);
            Console.WriteLine($"THRESHOLD client={thresholdPage.ClientSize}; content={thresholdContent.Bounds}; bar={thresholdPage.VerticalScroll.Visible}");
            if (thresholdWraps.Count == 0)
                foreach (var label in thresholdLabels) thresholdWraps.Add(label, label.MaximumSize);
            else if (thresholdWraps.Any(pair => pair.Key.MaximumSize != pair.Value))
                throw new Exception("A vertical scrollbar changed the fixed-width text budget.");
            layouts = 0; Pump(200);
            if (layouts != 0) throw new Exception("Scrollbar threshold reflow did not settle.");
        }
        if (scrollbarStates.Count != 2) throw new Exception("Scrollbar threshold fixture did not cross both scrollbar states.");
        Console.WriteLine("PASS scrollbar threshold converges without alternating text widths");
        thresholdContent.MaximumSize = originalMaximum;
        thresholdPage.AutoScrollMinSize = originalExtent;
        form.Size = new Size(1140, 850);
        tabs.Single(tab => tab.Text == "Live tracking").PerformClick();
        Pump(150);
        foreach (var name in new[] { "connection and camera settings", "eye settings and guidance", "camera settings",
            "cheek adjustment details", "hand and controller details" })
        {
            all.OfType<Button>().Single(button => button.Text == "Show " + name).PerformClick();
            Pump(150);
        }
        var expandedPage = all.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
        SendMessage(form.Handle, 0x0231, 0, 0);
        foreach (var width in new[] { 900, 780, 1000, 1140 })
        {
            form.Size = new Size(width, 850); Pump(150);
        }
        SendMessage(form.Handle, 0x0232, 0, 0);
        Pump(300); layouts = 0; Pump(300);
        if (layouts != 0 || expandedPage.HorizontalScroll.Visible)
            throw new Exception("Expanded live settings failed to settle within the page.");
        Console.WriteLine("PASS expanded live settings settle during simulated resizing");

        tabs.Single(tab => tab.Text == "Activity").PerformClick();
        Pump(150);
        var log = all.OfType<RichTextBox>().Single();
        var append = typeof(HubForm).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string)], null)!;
        for (var line = 0; line < 180; line++) append.Invoke(form, ["Resize fixture activity line " + line]);
        Pump(150);
        log.Select(log.GetFirstCharIndexFromLine(10), 16);
        SendMessage(log.Handle, 0x00B6, 0, 30); // EM_LINESCROLL
        var firstLine = SendMessage(log.Handle, 0x00CE, 0, 0); // EM_GETFIRSTVISIBLELINE
        var selection = (log.SelectionStart, log.SelectionLength);
        form.WindowState = FormWindowState.Minimized;
        for (var line = 0; line < 20; line++) append.Invoke(form, ["While minimized line " + line]);
        Pump(150);
        form.WindowState = FormWindowState.Normal;
        Pump(300);
        if (SendMessage(log.Handle, 0x00CE, 0, 0) != firstLine
            || (log.SelectionStart, log.SelectionLength) != selection)
            throw new Exception("Restoring Activity changed the inner log reader position or selection.");
        Console.WriteLine("PASS Activity inner log reading position survives append during minimize/restore");
        SendMessage(form.Handle, 0x0231, 0, 0);
        foreach (var width in new[] { 780, 1140 })
        {
            form.Size = new Size(width, 850);
            for (var line = 0; line < 10; line++) append.Invoke(form, ["During resize line " + line]);
            Pump(150);
        }
        SendMessage(form.Handle, 0x0232, 0, 0); Pump(300);
        if (SendMessage(log.Handle, 0x00CE, 0, 0) != firstLine
            || (log.SelectionStart, log.SelectionLength) != selection)
            throw new Exception("Activity window resizing changed the log reader position or selection.");
        Console.WriteLine("PASS Activity inner log reading position survives appending during simulated resizing");

        var owned = (IContainer)typeof(HubForm).GetField("_uiComponents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        var pulseTimer = owned.Components.OfType<System.Windows.Forms.Timer>().Single(timer => timer.Interval == 550);
        var pulseTick = typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pulseFlag = typeof(HubForm).GetField("_setupPulseOn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var attention = new DarkButton { Visible = true, Enabled = true };
        typeof(HubForm).GetMethod("StyleSetupButton", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [attention, "Offline pending step", false, true]);
        pulseFlag.SetValue(form, false);
        form.WindowState = FormWindowState.Minimized; Pump(100);
        pulseTick.Invoke(pulseTimer, [EventArgs.Empty]);
        if ((bool)pulseFlag.GetValue(form)!) throw new Exception("Setup decoration animated while minimized.");
        form.WindowState = FormWindowState.Normal; Pump(150);
        pulseTick.Invoke(pulseTimer, [EventArgs.Empty]);
        if (!(bool)pulseFlag.GetValue(form)!) throw new Exception("Setup decoration did not resume after restoring.");
        Console.WriteLine("PASS setup attention skips minimized windows and resumes on restore");
        form.Close();
    }

    // This fixture stays on screen so manual titlebar and mouse resize
    // actions can exercise Windows' size/move loop. Its captures read the
    // displayed pixels; DrawToBitmap cannot detect a stale compositor frame.
    static void RunPaintFixture(string output)
    {
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "fixture-root");
        Directory.CreateDirectory(root);
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
        form.Text = "Qpro V3 paint fixture - sample data only";
        form.KeyPreview = true;
        var controls = Descendants(form).ToArray();
        var live = controls.OfType<Button>().Single(button => Equals(button.Tag, "workflow-tab") && button.Text == "Live tracking");
        var activity = controls.OfType<Button>().Single(button => Equals(button.Tag, "workflow-tab") && button.Text == "Activity");
        var append = typeof(HubForm).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string)], null)!;
        var frame = 0;
        using var traffic = new System.Windows.Forms.Timer { Interval = 120 };
        traffic.Tick += (_, _) => append.Invoke(form, ["Offline paint fixture output " + frame++]);
        void Capture(string name)
        {
            if (form.WindowState == FormWindowState.Minimized) return;
            var bounds = form.RectangleToScreen(form.ClientRectangle);
            using var image = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            image.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            File.AppendAllText(Path.Combine(output, "displayed-state.txt"),
                $"{name}: state={form.WindowState}; client={form.ClientSize}; dpi={form.DeviceDpi}; live={live.Parent?.Visible}; logs={frame}; handles={controls.Count(control => control.IsHandleCreated)}\n");
            Console.WriteLine("Displayed capture: " + name);
        }
        var captureNumber = 0;
        form.Shown += (_, _) =>
        {
            form.ApplyPreviewScenario("magisk", "VirtualDesktop");
            live.PerformClick();
            traffic.Start();
        };
        form.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.F6) Capture("displayed-" + ++captureNumber);
            else if (eventArgs.KeyCode == Keys.F7)
            {
                foreach (var name in new[] { "connection and camera settings", "eye settings and guidance", "camera settings", "cheek adjustment details", "hand and controller details" })
                    controls.OfType<Button>().SingleOrDefault(button => button.Text == "Show " + name)?.PerformClick();
            }
            else if (eventArgs.KeyCode == Keys.F8)
            {
                form.WindowState = FormWindowState.Normal;
                form.Size = new Size(780, 620);
            }
            else if (eventArgs.KeyCode == Keys.F9)
            {
                form.WindowState = FormWindowState.Normal;
                form.Size = new Size(1140, 850);
            }
            else if (eventArgs.KeyCode == Keys.F10) activity.PerformClick();
            else if (eventArgs.KeyCode == Keys.F11) live.PerformClick();
            else if (eventArgs.KeyCode == Keys.F12)
            {
                // Freeze sample output, not drawing, for an unchanged-state
                // comparison. Save the untouched displayed surface first.
                traffic.Stop();
                var name = "redraw-check-" + ++captureNumber;
                await Task.Delay(350);
                if (form.IsDisposed || form.WindowState == FormWindowState.Minimized) return;
                Capture(name + "-before");
                form.Invalidate(true);
                form.Update();
                await Task.Delay(200);
                if (form.IsDisposed) return;
                Capture(name + "-reference");
                using var before = new Bitmap(Path.Combine(output, name + "-before.png"));
                using var reference = new Bitmap(Path.Combine(output, name + "-reference.png"));
                var different = 0;
                for (var y = 0; y < before.Height; y++)
                    for (var x = 0; x < before.Width; x++)
                        if (before.GetPixel(x, y) != reference.GetPixel(x, y)) different++;
                File.AppendAllText(Path.Combine(output, "displayed-state.txt"),
                    $"{name}: changed pixels after reference redraw={different}/{before.Width * before.Height}\n");
                Console.WriteLine($"Redraw comparison: {different} changed pixels");
                traffic.Start();
            }
            else return;
            eventArgs.Handled = eventArgs.SuppressKeyPress = true;
        };
        Console.WriteLine("Paint fixture: F6 capture; F7 expand; F8 compact; F9 normal; F10 Activity; F11 Live; F12 retained-pixel comparison.");
        Application.Run(form);
    }
}
