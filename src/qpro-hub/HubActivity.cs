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
        var followTail = selectionLength == 0 && selectionStart >= _log.TextLength;
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var level = severity ?? _activityClassifier.Classify(line);
            _log.Select(_log.TextLength, 0);
            _log.SelectionColor = ActivityColor(level);
            _log.AppendText(string.IsNullOrWhiteSpace(line) ? Environment.NewLine
                : $"{timestamp}  [{ActivityName(level)}] {line}{Environment.NewLine}");
        }

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
        if (followTail)
        {
            _log.Select(_log.TextLength, 0);
            _log.ScrollToCaret();
        }
        else
        {
            var start = Math.Clamp(selectionStart - removed, 0, _log.TextLength);
            var end = Math.Clamp(selectionStart + selectionLength - removed, start, _log.TextLength);
            _log.Select(start, end - start);
        }
    }

    private void ResetActivityLog()
    {
        _log.Clear();
        _activityClassifier.Reset();
    }
}
