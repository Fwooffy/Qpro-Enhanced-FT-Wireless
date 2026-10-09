using System.Runtime.InteropServices;
using System.Text;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private const int ActivityCharacterLimit = 180_000;
    private const int ActivityCharactersToKeep = 120_000;
    private readonly HubActivityClassifier _activityClassifier = new();
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
            var run = new StringBuilder();
            ActivitySeverity? runLevel = null;
            void AppendRun()
            {
                if (run.Length == 0) return;
                _log.Select(_log.TextLength, 0);
                _log.SelectionColor = ActivityColor(runLevel!.Value);
                _log.AppendText(run.ToString());
                run.Clear();
            }
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var level = severity ?? _activityClassifier.Classify(line);
                if (runLevel != level) AppendRun();
                runLevel = level;
                if (!string.IsNullOrWhiteSpace(line))
                    run.Append(timestamp).Append("  [").Append(ActivityName(level)).Append("] ").Append(line);
                run.AppendLine();
            }
            AppendRun();

            // Remove whole old lines without replacing Text, which would discard
            // the remaining colors. A very long single line still stays bounded.
            var removed = 0;
            if (_log.TextLength > ActivityCharacterLimit)
            {
                var textToTrim = _log.Text;
                var minimumRemoval = textToTrim.Length - ActivityCharactersToKeep;
                var lineEnd = textToTrim.IndexOf('\n', minimumRemoval);
                removed = lineEnd >= 0 && lineEnd < textToTrim.Length - 1 ? lineEnd + 1 : minimumRemoval;
                _log.Select(0, removed);
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
                _log.Invalidate();
            }
        }
    }

    private void ResetActivityLog()
    {
        _log.Clear();
        _activityClassifier.Reset();
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
