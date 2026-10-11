using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using QproFaceTracking.Hub;

// Diagnostic only: DwmFlush is deliberately absent from production and the
// acceptance sampler. Keep each pre-flush framebuffer as independent evidence.
internal static class PresentationPaintTests
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint word, nint value);

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var item in Descendants(child)) yield return item;
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); }
    }

    private static Bitmap Capture(Form form)
    {
        var rectangle = form.RectangleToScreen(form.ClientRectangle);
        var image = new Bitmap(rectangle.Width, rectangle.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
        return image;
    }

    internal static void Run(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        using var form = new HubForm(Path.Combine(output, "fixture-root"), rememberLaunch: false, previewOnly: true) { TopMost = true };
        form.Text = "Qpro presentation diagnostic - sample data only";
        form.Show();
        form.ApplyPreviewScenario("magisk", "VirtualDesktop");
        Pump(250);
        var controls = Descendants(form).ToArray();
        var tabs = controls.OfType<Button>().Where(button => Equals(button.Tag, "workflow-tab")).ToArray();
        var log = controls.OfType<RichTextBox>().Single();
        var append = typeof(HubForm).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string)], null)!;
        var snapshots = new List<object>();
        void Geometry(string stage)
        {
            snapshots.Add(new { stage, state = form.WindowState.ToString(), managed = form.Bounds, managedClient = form.ClientRectangle,
                controls = controls.Where(control => control.Parent == form || control is ComboBox || control == log)
                    .Select(control => new { type = control.GetType().Name, text = control.Text, managed = control.Bounds, managedClient = control.ClientRectangle }).ToArray() });
        }
        Geometry("normal-before-minimize");
        form.WindowState = FormWindowState.Minimized;
        Geometry("immediate-minimized");
        Pump(50);
        Geometry("settled-minimized");
        form.WindowState = FormWindowState.Normal;
        Geometry("immediate-restored");
        Pump(150);
        tabs.Single(tab => tab.Text == "Activity").PerformClick();
        for (var index = 0; index < 160; index++) append.Invoke(form, ["Offline native presentation fixture " + index]);
        log.Select(log.GetFirstCharIndexFromLine(10), 16);
        tabs.Single(tab => tab.Text == "Activity").Focus();
        SendMessage(log.Handle, 0xB6, 0, 30);
        var reader = (log.SelectionStart, log.SelectionLength, firstLine: SendMessage(log.Handle, 0xCE, 0, 0));
        var sequence = 0;
        using var traffic = new System.Windows.Forms.Timer { Interval = 35 };
        traffic.Tick += (_, _) => append.Invoke(form, ["Offline presentation traffic " + sequence++]);
        traffic.Start();
        var frames = new List<(string Name, Bitmap Image)>();
        var rows = new List<object>();
        SendMessage(form.Handle, 0x0231, 0, 0);
        for (var step = 0; step < 24; step++)
        {
            var width = step < 12 ? 1140 - step * 30 : 810 + (step - 12) * 30;
            var elapsed = Stopwatch.StartNew();
            form.Size = new Size(width, width < 900 ? 620 : 850);
            var setterMs = elapsed.Elapsed.TotalMilliseconds;
            var before = Capture(form); // First completed pixels; no pumping or repair.
            var beforeMs = elapsed.Elapsed.TotalMilliseconds;
            var flush = Stopwatch.StartNew();
            var result = DwmFlush();
            var flushMs = flush.Elapsed.TotalMilliseconds;
            var after = Capture(form); // Same state, no UI events processed between.
            frames.Add(($"step-{step:D2}-before", before));
            frames.Add(($"step-{step:D2}-after-flush", after));
            rows.Add(new { step, width, setterMs, beforeMs, flushMs, result, managedForm = form.Bounds, nativeClient = form.ClientRectangle,
                logManaged = log.Bounds, logClient = log.ClientRectangle, parentManaged = log.Parent!.Bounds, parentClient = log.Parent.ClientRectangle });
            Pump(25);
        }
        SendMessage(form.Handle, 0x0232, 0, 0);
        traffic.Stop();
        if (reader != (log.SelectionStart, log.SelectionLength, SendMessage(log.Handle, 0xCE, 0, 0)))
            throw new Exception("Presentation diagnostic changed the reader position.");
        foreach (var frame in frames) { frame.Image.Save(Path.Combine(output, frame.Name + ".png"), ImageFormat.Png); frame.Image.Dispose(); }
        File.WriteAllText(Path.Combine(output, "presentation.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "minimize-geometry.json"), JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "README.txt"), "Diagnostic only: 24 Activity size operations, first screenshot immediately after Size returns, then a separate DwmFlush and screenshot without pumping or redraw between. Full client retained. DwmFlush is not in production or the acceptance sampler. Native reader position preserved. Simulated size loop; physical dragging unverified.\n");
        form.Close();
        Console.WriteLine("PASS bounded presentation diagnostic: 24 independent pre-flush/after-flush pairs; reader preserved");
    }
}
