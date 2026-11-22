using System.Runtime.InteropServices;
using System.Text;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private const int ActivityCharacterLimit = 180_000;
    private const int ActivityCharactersToKeep = 120_000;
    private readonly HubActivityClassifier _activityClassifier = new();
    private sealed record ActivityEntry(string Text, string Timestamp, ActivitySeverity? Severity = null);
    private readonly Label _actionSeverity = new()
    {
        Text = "NORMAL", AutoSize = true, ForeColor = Good,
        Font = new Font(UiFontName, 8.5F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6),
    };

    private static Color ActivityColor(ActivitySeverity severity) => severity switch
    {
        ActivitySeverity.Error => Bad,
        ActivitySeverity.Warning => Warning,
        _ => Good,
    };

    private static string ActivityName(ActivitySeverity severity) => severity switch
    {
        ActivitySeverity.Error => "ERROR",
        ActivitySeverity.Warning => "WARNING",
        _ => "NORMAL",
    };

    // All calls arrive on the UI thread. Keep the original diagnostic wording
    // after a plain severity label, so Copy diagnostics and Save log retain it.
    private void AppendActivityText(string text, string timestamp, ActivitySeverity? severity = null)
        => AppendActivityEntries([new(text, timestamp, severity)]);

    private void AppendActivityEntries(IReadOnlyList<ActivityEntry> entries)
    {
        if (entries.Count == 0) return;
        var runs = FormatActivityEntries(entries);
        RememberActivityRuns(runs);
        if (_activityDisplayPaused) return;
        RenderActivityRuns(runs);
    }

    private List<ActivityRenderRun> FormatActivityEntries(IReadOnlyList<ActivityEntry> entries)
    {
        var runs = new List<ActivityRenderRun>();
        var text = new StringBuilder();
        ActivitySeverity? runLevel = null;
        void FinishRun()
        {
            if (text.Length == 0) return;
            runs.Add(new(text.ToString(), runLevel!.Value));
            text.Clear();
        }
        foreach (var entry in entries)
        {
            foreach (var line in entry.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var level = entry.Severity ?? _activityClassifier.Classify(line);
                if (runLevel != level) FinishRun();
                runLevel = level;
                if (!string.IsNullOrWhiteSpace(line))
                    text.Append(entry.Timestamp).Append("  [").Append(ActivityName(level)).Append("] ").Append(line);
                // RichEdit exposes LF text; use the same offsets in retained
                // history so paused selections survive a later history trim.
                text.Append('\n');
            }
        }
        FinishRun();
        return runs;
    }

    private void RenderActivityRuns(IReadOnlyList<ActivityRenderRun> runs, bool rebuild = false)
    {
        var selectionStart = _log.SelectionStart;
        var selectionLength = _log.SelectionLength;
        var caretAtTail = selectionStart >= _log.TextLength;
        var firstVisible = (int)ActivityMessage(_log.Handle, 0xCE, 0, 0); // EM_GETFIRSTVISIBLELINE
        var firstCharacter = Math.Max(0, _log.GetFirstCharIndexFromLine(firstVisible));
        var scroll = new ActivityScrollInfo { Size = (uint)Marshal.SizeOf<ActivityScrollInfo>(), Mask = 7 };
        // A scrollbar thumb's reported position can lag while the mouse is
        // held. Preserve the live viewport throughout that interaction too.
        var followTail = selectionLength == 0 && Control.MouseButtons == MouseButtons.None &&
            (!GetActivityScrollInfo(_log.Handle, 1, ref scroll) ||
            scroll.Position >= scroll.Maximum - Math.Max((long)scroll.Page - 1, 0) - 1);
        // Batch formatting and painting; do not expose the intermediate end
        // selection to the reader while output is arriving.
        var suspendRedraw = _log.Visible;
        if (suspendRedraw) ActivityMessage(_log.Handle, 0xB, 0, 0); // WM_SETREDRAW
        try
        {
            if (rebuild) _log.Clear();
            AppendActivityRenderRuns(runs);

            // Remove whole old lines without replacing Text, which would discard
            // the remaining colors. A very long single line still stays bounded.
            var removed = (int)Math.Min(int.MaxValue, _activityRemovedCharacters - _activityDisplayRemovedCharacters);
            if (!rebuild && removed > 0)
            {
                _log.Select(0, Math.Min(removed, _log.TextLength));
                var readOnly = _log.ReadOnly;
                try
                {
                    // RichEdit can reject a selection deletion while read-only.
                    // This synchronous edit does not pump input or expose typing.
                    _log.ReadOnly = false;
                    _log.SelectedText = string.Empty;
                }
                finally { _log.ReadOnly = readOnly; }
            }
            _activityDisplayRemovedCharacters = _activityRemovedCharacters;
            var start = followTail && caretAtTail ? _log.TextLength
                : Math.Clamp(selectionStart - removed, 0, _log.TextLength);
            var end = Math.Clamp(selectionStart + selectionLength - removed, start, _log.TextLength);
            _log.Select(start, end - start);
            if (followTail)
                ActivityMessage(_log.Handle, 0x115, 7, 0); // WM_VSCROLL, SB_BOTTOM
            else
            {
                // Restore the viewport after selection. Display-line anchors
                // survive wrapping and trimming without 16-bit pixel limits.
                var anchor = Math.Clamp(firstCharacter - removed, 0, _log.TextLength);
                var line = _log.GetLineFromCharIndex(anchor);
                var current = (int)ActivityMessage(_log.Handle, 0xCE, 0, 0);
                ActivityMessage(_log.Handle, 0xB6, 0, line - current); // EM_LINESCROLL
            }
        }
        finally
        {
            if (suspendRedraw)
            {
                ActivityMessage(_log.Handle, 0xB, 1, 0);
                // RichEdit's scrollbar is non-client content. Re-enable both
                // its text and frame painting after the batched edit, without
                // forcing a synchronous redraw for every incoming log line.
                RedrawWindow(_log.Handle, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0400);
                // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME
            }
        }
    }

    private void AppendActivityRenderRuns(IReadOnlyList<ActivityRenderRun> runs)
    {
        if (runs.Count == 0) return;
        var start = _log.TextLength;
        var firstSeverity = runs[0].Severity;
        _log.Select(start, 0);
        _log.SelectionColor = ActivityColor(firstSeverity);
        // RichEdit reflows text on each insertion even with redraw suspended.
        // Insert a mixed-color batch once, then apply its severity spans. Keep
        // plain text insertion so Unicode and existing font settings survive.
        if (runs.Count == 1) _log.AppendText(runs[0].Text);
        else
        {
            var text = new StringBuilder();
            foreach (var run in runs) text.Append(run.Text);
            _log.AppendText(text.ToString());
        }

        var offset = start;
        var length = 0;
        var severity = firstSeverity;
        void FinishSpan()
        {
            // The insertion already assigned the first severity everywhere.
            if (severity != firstSeverity && length > 0)
            {
                _log.Select(offset, length);
                _log.SelectionColor = ActivityColor(severity);
            }
            offset += length;
            length = 0;
        }
        foreach (var run in runs)
        {
            if (severity != run.Severity) FinishSpan();
            severity = run.Severity;
            length += run.Text.Length;
        }
        FinishSpan();
    }

    private void ResetActivityLog()
    {
        _log.Clear();
        _activityClassifier.Reset();
        _activityHistory.Clear();
        _activityHistoryCharacters = 0;
        _activityRemovedCharacters = _activityDisplayRemovedCharacters = 0;
        _activityDisplayPaused = false;
        UpdateActivityDisplayTools();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityScrollInfo
    {
        public uint Size, Mask;
        public int Minimum, Maximum;
        public uint Page;
        public int Position, TrackPosition;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint ActivityMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetScrollInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetActivityScrollInfo(nint window, int bar, ref ActivityScrollInfo scroll);
}
