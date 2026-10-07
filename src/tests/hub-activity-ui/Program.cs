using System.Reflection;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var type = Assembly.Load("QproFaceTracking.Hub").GetType("QproFaceTracking.Hub.HubForm")!;
        // Preview construction suppresses hardware probes, timers, persistence
        // and update recovery. No real runtime or clipboard is used by this test.
        var root = Path.Combine(Path.GetTempPath(), "qpro-activity-ui-" + Guid.NewGuid().ToString("N"));
        using var form = (Form)Activator.CreateInstance(type, root, false, true)!;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var log = (RichTextBox)type.GetField("_log", flags)!.GetValue(form)!;
        var reset = type.GetMethod("ResetActivityLog", flags)!;
        var append = type.GetMethod("AppendLog", flags)!;
        Color Theme(string name) => (Color)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        void Add(string text) => append.Invoke(form, [text]);
        void Check(bool passed, string message)
        {
            if (!passed) throw new Exception(message);
        }
        void CheckColor(string text, string theme)
        {
            var index = log.Text.IndexOf(text, StringComparison.Ordinal);
            Check(index >= 0, "Missing diagnostic text: " + text);
            log.Select(index, text.Length);
            Check(log.SelectionColor.ToArgb() == Theme(theme).ToArgb(), "Wrong color: " + text);
        }

        form.Show();
        Application.DoEvents();
        reset.Invoke(form, null);
        Add("[Worker] Completed successfully. No errors.");
        Add("[Worker] WARNING: GPU fallback is active.");
        Add("[Worker] RuntimeError: sample library could not load");
        CheckColor("[NORMAL] [Worker] Completed", "Good");
        CheckColor("[WARNING] [Worker] WARNING", "Warning");
        CheckColor("[ERROR] [Worker] RuntimeError", "Bad");
        var diagnostics = (string)type.GetMethod("DiagnosticText", flags)!.Invoke(form, null)!;
        Check(diagnostics.Contains("RuntimeError: sample library could not load", StringComparison.Ordinal),
            "Copy/save diagnostics lost the original exception.");
        log.Select(log.Text.IndexOf("RuntimeError:", StringComparison.Ordinal), log.TextLength);
        var selectedTail = log.SelectedText;
        Add("[Other worker] A new update arrived.");
        Check(log.SelectedText == selectedTail, "New output cleared a selected log tail before it could be copied.");

        reset.Invoke(form, null);
        Add("First " + new string('a', 40_000));
        Add("Second " + new string('b', 40_000));
        Add("Third " + new string('c', 40_000));
        Add("selected survivor " + new string('d', 40_000));
        var selected = log.Text.IndexOf("selected survivor", StringComparison.Ordinal);
        log.Select(selected, "selected survivor".Length);
        Check(log.SelectedText == "selected survivor", "Selection fixture did not target the diagnostic.");
        Add("ERROR: newest diagnostic " + new string('e', 40_000));
        Check(log.TextLength <= 180_000, "Activity history exceeded its bound.");
        Check(log.SelectedText == "selected survivor", $"History trimming changed a surviving selection: '{log.SelectedText}', start={log.SelectionStart}, survivor={log.Text.IndexOf("selected survivor", StringComparison.Ordinal)}, length={log.TextLength}.");
        Check(!log.Text.Contains("First ", StringComparison.Ordinal), "History did not discard its oldest lines.");
        CheckColor("selected survivor", "Good");
        CheckColor("[ERROR] ERROR: newest diagnostic", "Bad");
        form.Close();
        Check(!Directory.Exists(root), "Preview wrote to its isolated source folder.");
        Console.WriteLine("PASS: RichTextBox severity colors, original diagnostics, bounded history and surviving selection.");
    }
}
