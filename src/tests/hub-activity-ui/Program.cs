using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args.Contains("--offscreen")); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Environment.ExitCode = 1;
        }
    }

    private static void Run(bool offscreen)
    {
        ApplicationConfiguration.Initialize();
        var type = Assembly.Load("QproFaceTracking.Hub").GetType("QproFaceTracking.Hub.HubForm")!;
        // Preview construction suppresses hardware probes, timers, persistence
        // and update recovery. No real runtime or clipboard is used by this test.
        var root = Path.Combine(Path.GetTempPath(), "qpro-activity-ui-" + Guid.NewGuid().ToString("N"));
        using var form = (Form)Activator.CreateInstance(type, root, false, true)!;
        if (offscreen)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
        }
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
        Add("[Worker] CONTROLLER_BINDINGS_WARNING {\"message\":\"Could not prepare menu scrolling; optional setup failed.\"}");
        Add("[Worker] CONTROLLER_ERROR {\"message\":\"Sensor reader disconnected.\"}");
        CheckColor("[NORMAL] [Worker] Completed", "Good");
        CheckColor("[WARNING] [Worker] WARNING", "Warning");
        CheckColor("[ERROR] [Worker] RuntimeError", "Bad");
        CheckColor("[WARNING] [Worker] CONTROLLER_BINDINGS_WARNING", "Warning");
        CheckColor("[ERROR] [Worker] CONTROLLER_ERROR", "Bad");
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

        // Exercise the actual background pipe dispatch, rather than only the
        // synchronous UI helper used by the scroll fixtures above.
        reset.Invoke(form, null);
        Add(string.Join('\n', Enumerable.Range(0, 150).Select(index => $"Queued reader line {index:D3}")));
        log.Select(log.Text.IndexOf("Queued reader line 010", StringComparison.Ordinal), 12);
        log.ScrollToCaret();
        ScrollLines(log, 15);
        var queueSelection = log.SelectedText;
        var queueFirstLine = FirstVisibleLine(log);
        var queueScrollPixels = ReadScroll(log).Position;
        var postOutput = type.GetMethod("PostProcessOutput", flags)!;
        var setFeedback = type.GetMethod("SetActionFeedback", flags)!;
        var feedbackTitle = (Label)type.GetField("_actionTitle", flags)!.GetValue(form)!;
        var feedbackLayouts = 0;
        void OnFeedbackLayout(object? sender, LayoutEventArgs e) => feedbackLayouts++;
        feedbackTitle.Parent!.Layout += OnFeedbackLayout;
        var updateOutput = type.GetField("_componentUpdateOutput", flags)!;
        var arrivalTimes = new List<(string Before, string After)>();
        var applied = new List<int>();
        var textChanges = 0;
        void OnTextChanged(object? sender, EventArgs e) => textChanges++;
        log.TextChanged += OnTextChanged;
        updateOutput.SetValue(form, (Action<string>)(text =>
        {
            if (text.Contains("Queued output 043", StringComparison.Ordinal)) throw new InvalidOperationException("expected helper failure");
        }));
        try
        {
            Task.Run(() =>
            {
                for (var index = 0; index < 97; index++)
                {
                    var entry = index;
                    var before = DateTime.Now.ToString("HH:mm:ss");
                    postOutput.Invoke(form, ["Batch", $"[Batch] Queued output {entry:D3}", (Action)(() =>
                    {
                        applied.Add(entry);
                        setFeedback.Invoke(form, [$"Queued progress {entry:D3}", "The queued process is running.", "Wait for completion.", false, null]);
                        if (entry == 42) throw new InvalidOperationException("expected UI failure");
                    })]);
                    arrivalTimes.Add((before, DateTime.Now.ToString("HH:mm:ss")));
                }
            }).GetAwaiter().GetResult();
            Check(applied.Count == 0 && !log.Text.Contains("Queued output 000", StringComparison.Ordinal),
                "A background process update ran before the UI pump.");
            // Delay display into a later clock second. The timestamps must
            // describe arrival, while the UI completion remains last in order.
            while (DateTime.Now.ToString("HH:mm:ss") == arrivalTimes[^1].After) Thread.Sleep(20);
            Add("[Batch] Completed after all queued output.");
            var appliedAtInputTick = -1;
            using var inputTimer = new System.Windows.Forms.Timer { Interval = 1 };
            inputTimer.Tick += (_, _) => { appliedAtInputTick = applied.Count; inputTimer.Stop(); };
            inputTimer.Start();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!log.Text.Contains("Completed after all queued output", StringComparison.Ordinal) && timeout.ElapsedMilliseconds < 5000)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            Check(applied.SequenceEqual(Enumerable.Range(0, 97)), "Batched UI actions were lost, duplicated or reordered.");
            Check(appliedAtInputTick >= 0 && appliedAtInputTick < 97,
                "The bounded output drain starved a native UI timer until all queued output completed.");
            var lines = log.Lines.Where(line => line.Contains("[Batch] Queued output ", StringComparison.Ordinal)).ToArray();
            Check(lines.Length == 97, "Batched Activity dropped process output.");
            for (var index = 0; index < lines.Length; index++)
            {
                Check(lines[index].EndsWith($"Queued output {index:D3}", StringComparison.Ordinal), "Batched Activity changed output order or wording.");
                Check(lines[index].StartsWith(arrivalTimes[index].Before, StringComparison.Ordinal) ||
                    lines[index].StartsWith(arrivalTimes[index].After, StringComparison.Ordinal), "A queued log timestamp changed from arrival to display time.");
            }
            Check(log.Text.IndexOf("Completed after all queued output", StringComparison.Ordinal) >
                log.Text.IndexOf("Queued output 096", StringComparison.Ordinal), "A completion log overtook queued pipe output.");
            Check(log.Text.Contains("expected UI failure", StringComparison.Ordinal) &&
                log.Text.Contains("Component update progress could not refresh", StringComparison.Ordinal), "A failed UI helper hid its diagnostic or stopped the remaining batch.");
            Check(log.SelectedText == queueSelection && FirstVisibleLine(log) == queueFirstLine &&
                ReadScroll(log).Position == queueScrollPixels, "Batched background output moved the reader's selection or viewport.");
            Check(textChanges < 49, $"Output still edited RichEdit per message ({textChanges} edits for 97 messages).");
            Check(feedbackTitle.Text == "Queued progress 096" && feedbackLayouts < 97,
                $"Batched progress lost its final status or still reflowed per message ({feedbackLayouts} layouts).");
            Console.WriteLine($"PASS: background batching preserves 97 ordered messages, arrival times, failures and reader state ({textChanges} RichEdit edits; {feedbackLayouts} progress layouts).");
        }
        finally
        {
            updateOutput.SetValue(form, null);
            log.TextChanged -= OnTextChanged;
            feedbackTitle.Parent!.Layout -= OnFeedbackLayout;
        }
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
