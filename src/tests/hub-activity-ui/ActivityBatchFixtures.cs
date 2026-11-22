using System.Diagnostics;

internal static class ActivityBatchFixtures
{
    internal static void Run(RichTextBox log, Action<string> append, Action reset, Func<string, Color> theme)
    {
        reset();
        append(string.Join('\n', Enumerable.Range(0, 150).Select(index => $"Reader line {index:D3}")));
        log.Select(log.Text.IndexOf("Reader line 050", StringComparison.Ordinal), 15);
        log.ScrollToCaret();
        var selected = log.SelectedText;
        var selection = log.SelectionStart;
        var font = log.Font;
        var lines = Enumerable.Range(0, 32).Select(index =>
            $"[Mixed] {(index % 3 == 0 ? "WARNING: " : index % 3 == 1 ? "RuntimeError: " : "Progress ")}{index:D2} 🥽 e\u0301 中文 {{path\\value}}").ToArray();
        using var mutations = new TextMutationObserver(log);
        append(string.Join('\n', lines));
        if (mutations.Count != 1) throw new Exception($"A 32-line mixed batch made {mutations.Count} native text insertions instead of one.");
        if (log.SelectionStart != selection || log.SelectedText != selected || !log.ReadOnly || !log.Font.Equals(font))
            throw new Exception("Mixed-severity insertion changed the reader selection, editability or font.");
        for (var index = 0; index < lines.Length; index++)
        {
            var position = log.Text.IndexOf(lines[index], StringComparison.Ordinal);
            if (position < 0) throw new Exception("Mixed-severity insertion changed Unicode or original diagnostic wording.");
            log.Select(position, lines[index].Length);
            var expected = theme(index % 3 == 0 ? "Warning" : index % 3 == 1 ? "Bad" : "Good");
            if (log.SelectionColor.ToArgb() != expected.ToArgb())
                throw new Exception($"Mixed-severity insertion colored the wrong UTF-16 span at line {index}.");
        }
        Console.WriteLine($"PASS: 32 alternating severity lines use {mutations.Count} native text insertion; Unicode, original wording, all severity spans, selection and font preserved.");
    }

    // Offline native RichEdit workload. Measure only the synchronous batch
    // append on the UI thread; pumping between batches lets painting settle.
    // This does not measure a receiver, headset or real streaming throughput.
    internal static void Benchmark(Form form, RichTextBox log, Action<string> append, Action reset)
    {
        const int batches = 40;
        var sample = string.Join('\n', Enumerable.Range(0, 32).Select(index =>
            $"[Mixed] {(index % 3 == 0 ? "WARNING: " : index % 3 == 1 ? "RuntimeError: " : "Progress ")}{index:D2} " + new string('x', 160)));
        var totals = new List<double>();
        var editCounts = new List<int>();
        var insertionCounts = new List<int>();
        var allocated = new List<long>();
        for (var run = 0; run < 6; run++)
        {
            reset();
            append(string.Join('\n', Enumerable.Range(0, 3000).Select(index => $"Retained line {index:D4}")));
            log.Select(log.Text.IndexOf("Retained line 2500", StringComparison.Ordinal), 13);
            log.ScrollToCaret();
            Application.DoEvents();
            var edits = 0;
            long ticks = 0, bytes = 0;
            using var mutations = new TextMutationObserver(log);
            void OnEdit(object? sender, EventArgs e) => edits++;
            log.TextChanged += OnEdit;
            try
            {
                for (var batch = 0; batch < batches; batch++)
                {
                    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                    var beforeTicks = Stopwatch.GetTimestamp();
                    append(sample);
                    ticks += Stopwatch.GetTimestamp() - beforeTicks;
                    bytes += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                    Application.DoEvents();
                }
            }
            finally { log.TextChanged -= OnEdit; }
            if (run == 0) continue; // Warm the same native control and JIT path.
            totals.Add(ticks * 1000.0 / Stopwatch.Frequency / batches);
            editCounts.Add(edits);
            insertionCounts.Add(mutations.Count);
            allocated.Add(bytes / batches);
        }
        totals.Sort(); editCounts.Sort(); insertionCounts.Sort(); allocated.Sort();
        Console.WriteLine($"BENCH: mixed severity, 32 lines/batch, {batches} batches/sample, 5 warm samples; median synchronous append {totals[2]:F3} ms/batch; observed native text-write messages {insertionCounts[2]}/{batches} batches; change notifications {editCounts[2]} (includes formatting/trims); median managed allocation {allocated[2]} bytes/batch. OS={Environment.OSVersion}; framework={Environment.Version}; DPI={form.DeviceDpi}; source preview only, offscreen, no device/process probes.");
    }

    // Formatting also raises TextChanged. Count actual native text writes so
    // the regression distinguishes insertion/reflow from severity recoloring.
    private sealed class TextMutationObserver : NativeWindow, IDisposable
    {
        internal int Count { get; private set; }
        internal TextMutationObserver(RichTextBox log) => AssignHandle(log.Handle);
        protected override void WndProc(ref Message message)
        {
            if (message.Msg is 0x000C or 0x00C2 or 0x0449 or 0x0461) Count++; // WM_SETTEXT, EM_REPLACESEL, EM_STREAMIN, EM_SETTEXTEX
            base.WndProc(ref message);
        }
        public void Dispose() => ReleaseHandle();
    }
}
