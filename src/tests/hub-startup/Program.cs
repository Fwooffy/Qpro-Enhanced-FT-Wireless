using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class StartupTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.Dark);
            var initializedMs = elapsed.Elapsed.TotalMilliseconds;
            var type = Assembly.Load("QproFaceTracking.Hub").GetType("QproFaceTracking.Hub.HubForm")!;
            var root = Path.Combine(Path.GetTempPath(), "qpro-startup-" + Guid.NewGuid().ToString("N"));
            using var form = (Form)Activator.CreateInstance(type, root, false, true)!;
            var constructedMs = elapsed.Elapsed.TotalMilliseconds;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            type.GetMethod("FlushPreviewLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
            form.Update();
            Application.DoEvents();
            var usableMs = elapsed.Elapsed.TotalMilliseconds;
            var controls = Descendants(form).ToArray();
            var tabs = controls.OfType<Button>().Where(button => Equals(button.Tag, "workflow-tab")).ToArray();
            Check(tabs.Length == 6, "Startup lost a navigation page.");
            var setup = tabs.Single(button => button.AccessibleName == "First-time setup");
            Check(setup.Enabled && setup.Visible && form.Location.X < 0, "First-time setup was not usable offscreen.");
            var pages = controls.OfType<Panel>().Where(panel => Equals(panel.Tag, "hub-page")).ToArray();
            Check(pages.Count(page => page.Visible) == 1, "Startup did not select exactly one page.");
            var startupPage = pages.Single(page => page.Visible);
            Check(controls.OfType<Label>().Any(label => label.Visible && label.Text == "First-time setup")
                && !startupPage.HorizontalScroll.Visible, "Startup did not settle on usable Setup.");
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var runStatus = (Label)type.GetField("_runStatus", flags)!.GetValue(form)!;
            Check(runStatus.Visible && runStatus.Height > 0, "Startup footer was unavailable.");
            if (args.Contains("--navigation"))
            {
                var captureOptions = type.GetMethod("CaptureLiveOptions", flags)!;
                var beforeOptions = captureOptions.Invoke(form, null);
                foreach (var tab in tabs)
                {
                    tab.PerformClick();
                    Application.DoEvents();
                    type.GetMethod("FlushPreviewLayout", flags)!.Invoke(form, null);
                    Check(pages.Count(page => page.Visible) == 1 && pages.Any(page => page.Visible && page.ClientSize.Width > 0),
                        "A navigation page failed after startup: " + tab.Text);
                }
                setup.PerformClick();
                Application.DoEvents();
                Check(Equals(beforeOptions, captureOptions.Invoke(form, null)), "Page navigation changed saved live choices.");
                CheckConstructionScope(type.Assembly);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { initializedMs, constructedMs, usableMs, dpi = form.DeviceDpi,
                runtime = Environment.Version.ToString(), mode = "fresh-process offscreen preview; no runtime probes", navigation = args.Contains("--navigation") }));
            form.Close();
            Check(!Directory.Exists(root), "Preview startup wrote to its isolated source directory.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckConstructionScope(Assembly assembly)
    {
        var scopeType = assembly.GetType("QproFaceTracking.Hub.HubLayoutConstructionScope")!;
        var tableType = assembly.GetType("QproFaceTracking.Hub.BufferedTableLayoutPanel")!;
        TableLayoutPanel? deferred = null;
        var layouts = 0;
        try
        {
            using ((IDisposable)Activator.CreateInstance(scopeType, nonPublic: true)!)
            {
                deferred = (TableLayoutPanel)Activator.CreateInstance(tableType, nonPublic: true)!;
                deferred.Layout += (_, _) => layouts++;
                deferred.Controls.Add(new Label { Text = "Synthetic construction row", AutoSize = true });
                deferred.Padding = new Padding(12);
                Check(layouts == 0, "Construction performed intermediate table layouts.");
            }
            Check(layouts > 0, "Construction did not release pending table layout.");
            var released = layouts;
            deferred.Padding = new Padding(18);
            Check(layouts > released, "Completed construction left table layout suspended.");
            using var ordinary = (TableLayoutPanel)Activator.CreateInstance(tableType, nonPublic: true)!;
            var ordinaryLayouts = 0;
            ordinary.Layout += (_, _) => ordinaryLayouts++;
            ordinary.Padding = new Padding(12);
            Check(ordinaryLayouts > 0, "Construction scope leaked into later UI controls.");
        }
        finally { deferred?.Dispose(); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
