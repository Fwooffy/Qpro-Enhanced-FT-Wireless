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
            // a programmatic corner-drag simulation, not a physical mouse test.
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
            if (layouts != 0) throw new Exception("Corner-resize layout did not settle: " + layouts);
            if (page.HorizontalScroll.Visible) throw new Exception("Window resize introduced a horizontal page scrollbar.");
            Console.WriteLine("PASS corner-drag round trip settles without a horizontal scrollbar: " + tab.Text);
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
        Console.WriteLine("PASS expanded live settings settle during corner resizing");

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
        Console.WriteLine("PASS Activity inner log reading position survives appending during corner resizing");

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
}
