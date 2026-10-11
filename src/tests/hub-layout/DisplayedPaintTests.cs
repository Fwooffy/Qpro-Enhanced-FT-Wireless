using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using QproFaceTracking.Hub;

internal static class DisplayedPaintTests
{
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint word, nint value);

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void Pump(int milliseconds = 350)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }

    internal static void Run(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "fixture-root");
        Directory.CreateDirectory(root);
        using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
        form.Text = "Qpro V3 displayed paint check - sample data only";
        // A background test process cannot always acquire foreground focus.
        // Keep only this temporary sample window unobscured for CopyFromScreen.
        form.TopMost = true;
        form.Show();
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        form.Activate();
        Pump();
        var controls = Descendants(form).ToArray();
        var tabs = controls.OfType<Button>().Where(button => Equals(button.Tag, "workflow-tab")).ToArray();
        var append = typeof(HubForm).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string)], null)!;
        var log = controls.OfType<RichTextBox>().Single();
        var sequence = 0;
        using var traffic = new System.Windows.Forms.Timer { Interval = 120 };
        traffic.Tick += (_, _) => append.Invoke(form, ["Offline displayed paint fixture line " + sequence++]);
        traffic.Start();
        var evidence = Path.Combine(output, "displayed-state.txt");
        File.WriteAllText(evidence, "Actual framebuffer captures; programmatic OS window transitions and simulated size loop. Physical mouse drag is unverified. Pixel comparisons exclude only the bottom-right 16 logical pixel frame corner; the full RichEdit client is compared.\n");

        Bitmap Capture(string name)
        {
            var rectangle = form.RectangleToScreen(form.ClientRectangle);
            var bitmap = new Bitmap(rectangle.Width, rectangle.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
            bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            return bitmap;
        }

        void Check(string name)
        {
            traffic.Stop();
            tabs.Single(tab => tab.Text == "Activity").Focus();
            Pump();
            var page = controls.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
            var scroll = page.AutoScrollPosition;
            var selection = (log.SelectionStart, log.SelectionLength);
            var handles = controls.Where(control => control.IsHandleCreated).ToDictionary(control => control, control => control.Handle);
            // Capture first, with no Refresh/Invalidate/DrawToBitmap beforehand.
            using var displayed = Capture(name);
            if (displayed.GetPixel(5, 5).ToArgb() != HubForm.Panel.ToArgb())
                throw new Exception("Framebuffer capture is obscured or not the fixture: " + name);
            form.Invalidate(true);
            form.Update();
            Pump(180);
            using var reference = Capture(name + "-reference");
            var different = 0;
            var differenceBounds = Rectangle.Empty;
            // The rounded frame corner/resize grip can differ by a channel
            // value after DWM composition. Exclude only its 16px decoration.
            var gripSize = (int)Math.Ceiling(16 * form.DeviceDpi / 96F);
            var grip = new Rectangle(displayed.Width - gripSize, displayed.Height - gripSize, gripSize, gripSize);
            for (var y = 0; y < displayed.Height; y++)
                for (var x = 0; x < displayed.Width; x++)
                {
                    if (grip.Contains(x, y) || displayed.GetPixel(x, y) == reference.GetPixel(x, y)) continue;
                    different++;
                    var pixel = new Rectangle(x, y, 1, 1);
                    differenceBounds = differenceBounds.IsEmpty ? pixel : Rectangle.Union(differenceBounds, pixel);
                }
            var line = $"{name}: state={form.WindowState}; client={form.ClientSize}; dpi={form.DeviceDpi}; pageScroll={scroll}; changedPixels={different}; changedBounds={differenceBounds}; logLines={sequence}\n";
            File.AppendAllText(evidence, line);
            Console.Write(line);
            if (page.AutoScrollPosition != scroll || (log.SelectionStart, log.SelectionLength) != selection
                || handles.Any(pair => !pair.Key.IsHandleCreated || pair.Key.Handle != pair.Value))
                throw new Exception("A reference repaint mutated control/reader state: " + name);
            if (different != 0) throw new Exception("Displayed pixels retained stale content until a reference repaint: " + name);
            traffic.Start();
        }

        tabs.Single(tab => tab.Text == "Live tracking").PerformClick();
        Check("normal");
        foreach (var name in new[] { "connection and camera settings", "eye settings and guidance", "camera settings", "cheek adjustment details", "hand and controller details" })
            controls.OfType<Button>().Single(button => button.Text == "Show " + name).PerformClick();
        Pump();
        var live = controls.OfType<Panel>().Single(panel => Equals(panel.Tag, "hub-page") && panel.Visible);
        live.AutoScrollPosition = new Point(0, 230);
        Check("expanded-scrolled");
        var wheelPoint = live.PointToScreen(new Point(100, 100));
        SendMessage(live.Handle, 0x020A, -240 << 16, (wheelPoint.Y << 16) | (wheelPoint.X & 0xffff));
        Check("mouse-wheel-scrolled");
        SendMessage(live.Handle, 0x0115, 3, 0); // WM_VSCROLL, SB_PAGEDOWN
        Check("scrollbar-scrolled");
        form.WindowState = FormWindowState.Minimized;
        Pump(200);
        form.WindowState = FormWindowState.Maximized;
        Check("minimize-to-maximize");
        form.WindowState = FormWindowState.Normal;
        Check("restored");
        SendMessage(form.Handle, 0x0231, 0, 0);
        foreach (var width in new[] { 1000, 900, 820, 780, 900, 1040, 1140 })
        {
            form.Size = new Size(width, width < 900 ? 620 : 850);
            Pump(90);
        }
        SendMessage(form.Handle, 0x0232, 0, 0);
        Check("resize-roundtrip");
        form.Size = new Size(780, 620);
        Check("compact");
        form.Size = new Size(1140, 850);
        tabs.Single(tab => tab.Text == "Activity").PerformClick();
        for (var index = 0; index < 180; index++) append.Invoke(form, ["Offline activity display fixture line " + index]);
        Pump();
        log.Select(log.GetFirstCharIndexFromLine(10), 16);
        SendMessage(log.Handle, 0xB6, 0, 30);
        var firstLine = SendMessage(log.Handle, 0xCE, 0, 0);
        var reader = (log.SelectionStart, log.SelectionLength);
        form.WindowState = FormWindowState.Minimized;
        Pump(300);
        form.WindowState = FormWindowState.Maximized;
        Pump();
        form.WindowState = FormWindowState.Normal;
        Check("activity-restored-during-logs");
        if (SendMessage(log.Handle, 0xCE, 0, 0) != firstLine || (log.SelectionStart, log.SelectionLength) != reader)
            throw new Exception("Activity reader position changed during displayed transitions.");
        form.Size = new Size(780, 620);
        Pump();
        form.Size = new Size(1140, 850);
        Check("activity-resized-during-logs");
        if (SendMessage(log.Handle, 0xCE, 0, 0) != firstLine || (log.SelectionStart, log.SelectionLength) != reader)
            throw new Exception("Activity reader position changed during displayed resizing.");
        traffic.Stop();
        form.Close();
    }
}
