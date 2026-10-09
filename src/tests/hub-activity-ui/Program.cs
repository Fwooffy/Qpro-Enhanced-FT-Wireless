using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try { Run(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Environment.ExitCode = 1;
        }
    }

    private static void Run()
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
        Controls(form).OfType<Button>().Single(button => button.AccessibleName == "Activity").PerformClick();
        Application.DoEvents();
        Check(log.Visible && log.ClientSize.Height > 0, "Activity scrolling requires a visible log control.");
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
        Add(string.Join('\n', Enumerable.Range(0, 150).Select(index => $"Reading line {index:D3}")));
        log.Select(log.Text.IndexOf("Reading line 010", StringComparison.Ordinal), 0);
        log.ScrollToCaret();
        ScrollLines(log, 15);
        var oldCaret = log.SelectionStart;
        for (var update = 0; update < 8; update++)
        {
            ScrollLines(log, 2);
            var readingLine = FirstVisibleLine(log);
            Add($"Streaming update {update}");
            Check(FirstVisibleLine(log) == readingLine,
                $"Streaming output moved the reading position from line {readingLine} to {FirstVisibleLine(log)} while scrolling down.");
            Check(log.SelectionStart == oldCaret, "Streaming output changed the old text cursor.");
        }
        log.Select(log.TextLength, 0);
        log.ScrollToCaret();
        ScrollLines(log, -20);
        var scrolledFromTail = FirstVisibleLine(log);
        var scrolledTailPixels = ReadScroll(log).Position;
        Add("A tail cursor must not override a manually scrolled viewport.");
        Check(FirstVisibleLine(log) == scrolledFromTail, "The cursor at the end incorrectly enabled automatic following.");
        Check(ReadScroll(log).Position == scrolledTailPixels, "Appending changed the viewport's partial-line pixel offset.");
        log.Select(oldCaret, 0);
        NativeMessage(log.Handle, 0x115, 7, 0); // WM_VSCROLL, SB_BOTTOM: scroll without moving the cursor.
        var bottomScroll = ReadScroll(log);
        Check(AtBottom(log), $"Bottom-follow fixture did not reach the bottom: position={bottomScroll.Position}, max={bottomScroll.Maximum}, page={bottomScroll.Page}, first={FirstVisibleLine(log)}.");
        Add(string.Join('\n', Enumerable.Range(0, 20).Select(index => $"New bottom line {index}")));
        Check(AtBottom(log), "A viewport already at the bottom stopped following new output.");
        Check(log.SelectionStart == oldCaret, "Following new output moved an unrelated text cursor.");
        log.Focus();
        ScrollLines(log, -50);
        var beforeScroll = FirstVisibleLine(log);
        NativeMessage(log.Handle, 0x115, 1, 0); // WM_VSCROLL, SB_LINEDOWN
        var afterScroll = FirstVisibleLine(log);
        Check(afterScroll > beforeScroll, "The native scrollbar fixture did not scroll toward newer messages.");
        Add("Output after downward scrollbar input.");
        Check(FirstVisibleLine(log) == afterScroll, "An update undid downward scrollbar input.");

        // Many short lines exceed 65,535 vertical pixels without exceeding
        // the character bound. A trim must preserve the visible text anchor.
        reset.Invoke(form, null);
        Add(string.Join('\n', Enumerable.Range(0, 6500).Select(index => $"L{index:D4}")));
        log.Select(log.Text.IndexOf("L0200", StringComparison.Ordinal), 0);
        log.ScrollToCaret();
        ScrollLines(log, 5000 - FirstVisibleLine(log));
        var anchor = log.GetFirstCharIndexFromLine(FirstVisibleLine(log));
        var anchorText = log.Text[anchor..].Split('\n')[0];
        var anchorOffset = log.GetPositionFromCharIndex(anchor).Y;
        var scroll = ReadScroll(log);
        Check(scroll.Position > 65_535, "Large-history fixture did not exercise a 32-bit scroll position.");
        Add(string.Join('\n', Enumerable.Range(6500, 1200).Select(index => $"L{index:D4}")));
        var retainedAnchor = log.GetFirstCharIndexFromLine(FirstVisibleLine(log));
        Check(log.Text[retainedAnchor..].Split('\n')[0] == anchorText,
            "Trimming old messages moved the viewport away from its surviving reading line.");
        Check(log.GetPositionFromCharIndex(retainedAnchor).Y == anchorOffset,
            "Trimming changed the retained viewport's partial-line offset.");
        Check(log.TextLength <= 180_000 && !log.Text.Contains("L0000", StringComparison.Ordinal),
            "Large-history fixture did not trim old messages within the bound.");
        NativeMessage(log.Handle, 0x115, 6, 0); // SB_TOP: the next trim removes this reading anchor.
        Add(string.Join('\n', Enumerable.Range(7700, 3200).Select(index => $"L{index:D4}")));
        Check(FirstVisibleLine(log) == 0, "An expired reading anchor should settle at the earliest retained line.");
        Console.WriteLine("PASS: streaming scroll-down, manual tail cursor, viewport following and 32-bit history-trim anchors.");

        reset.Invoke(form, null);
        Add("First " + new string('a', 40_000));
        Add("Second " + new string('b', 40_000));
        Add("Third " + new string('c', 40_000));
        Add("selected survivor " + new string('d', 40_000));
        var selected = log.Text.IndexOf("selected survivor", StringComparison.Ordinal);
        log.Select(selected, "selected survivor".Length);
        Check(log.SelectedText == "selected survivor", "Selection fixture did not target the diagnostic.");
        log.ScrollToCaret();
        ScrollLines(log, 30);
        var wrappedAnchor = log.GetFirstCharIndexFromLine(FirstVisibleLine(log));
        Add("ERROR: newest diagnostic " + new string('e', 40_000));
        Check(log.TextLength <= 180_000, "Activity history exceeded its bound.");
        Check(log.SelectedText == "selected survivor", $"History trimming changed a surviving selection: '{log.SelectedText}', start={log.SelectionStart}, survivor={log.Text.IndexOf("selected survivor", StringComparison.Ordinal)}, length={log.TextLength}.");
        var removed = selected - log.Text.IndexOf("selected survivor", StringComparison.Ordinal);
        Check(log.GetFirstCharIndexFromLine(FirstVisibleLine(log)) == wrappedAnchor - removed,
            "Trimming moved a wrapped-line reading anchor away from its surviving text.");
        Check(log.ReadOnly, "History trimming left Activity editable.");
        Check(!log.Text.Contains("First ", StringComparison.Ordinal), "History did not discard its oldest lines.");
        CheckColor("selected survivor", "Good");
        CheckColor("[ERROR] ERROR: newest diagnostic", "Bad");
        form.Close();
        Check(!Directory.Exists(root), "Preview wrote to its isolated source folder.");
        Console.WriteLine("PASS: RichTextBox severity colors, original diagnostics, bounded history and surviving selection.");
    }

    private static IEnumerable<Control> Controls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in Controls(control)) yield return child;
        }
    }

    private static int FirstVisibleLine(RichTextBox log) => (int)NativeMessage(log.Handle, 0xCE, 0, 0);
    private static void ScrollLines(RichTextBox log, int lines) => NativeMessage(log.Handle, 0xB6, 0, lines);
    private static ScrollInfo ReadScroll(RichTextBox log)
    {
        var scroll = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 7 };
        GetScrollInfo(log.Handle, 1, ref scroll);
        return scroll;
    }
    private static bool AtBottom(RichTextBox log)
    {
        var scroll = ReadScroll(log);
        return scroll.Position >= scroll.Maximum - Math.Max((long)scroll.Page - 1, 0) - 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size, Mask;
        public int Minimum, Maximum;
        public uint Page;
        public int Position, TrackPosition;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint NativeMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollInfo(nint window, int bar, ref ScrollInfo scroll);
}
