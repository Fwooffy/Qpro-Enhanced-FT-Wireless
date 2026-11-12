using System.Text;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private sealed record ActivityRenderRun(string Text, ActivitySeverity Severity);
    private readonly List<ActivityRenderRun> _activityHistory = [];
    private int _activityHistoryCharacters;
    private long _activityRemovedCharacters;
    private long _activityDisplayRemovedCharacters;
    private bool _activityDisplayPaused;
    private DarkButton? _activityPauseDisplay;
    private Label? _activityDisplayHint;

    private Control BuildActivityTools()
    {
        var tools = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            Margin = new Padding(0, 0, 0, 6), Padding = Padding.Empty,
        };
        _activityPauseDisplay = ActionButton("Pause display", (_, _) => SetActivityDisplayPaused(!_activityDisplayPaused));
        _activityPauseDisplay.AccessibleDescription = "Pause only this log view. Tracking and diagnostic collection continue.";
        var latest = ActionButton("Jump to latest", (_, _) => JumpToLatestActivity());
        latest.AccessibleDescription = "Resume the log display and follow new messages at the bottom.";
        _activityDisplayHint = Info("Tracking and logging continue while paused.");
        _activityDisplayHint.AutoSize = true;
        _activityDisplayHint.Margin = new Padding(4, 14, 0, 4);
        tools.Controls.Add(_activityPauseDisplay);
        tools.Controls.Add(latest);
        tools.Controls.Add(_activityDisplayHint);
        UpdateActivityDisplayTools();
        return tools;
    }

    private void SetActivityDisplayPaused(bool paused)
    {
        if (_activityDisplayPaused == paused) return;
        _activityDisplayPaused = paused;
        if (!paused) RenderActivityRuns(_activityHistory, rebuild: true);
        UpdateActivityDisplayTools();
    }

    private void JumpToLatestActivity()
    {
        SetActivityDisplayPaused(false);
        _log.Select(_log.TextLength, 0);
        ActivityMessage(_log.Handle, 0x115, 7, 0); // WM_VSCROLL, SB_BOTTOM
    }

    private void UpdateActivityDisplayTools()
    {
        if (_activityPauseDisplay is not null)
        {
            _activityPauseDisplay.Text = _activityDisplayPaused ? "Resume display" : "Pause display";
            _activityPauseDisplay.AccessibleName = _activityPauseDisplay.Text;
        }
        if (_activityDisplayHint is not null)
        {
            _activityDisplayHint.Text = _activityDisplayPaused
                ? "Display paused. Tracking and logging continue."
                : "Tracking and logging continue while paused.";
            _activityDisplayHint.ForeColor = _activityDisplayPaused ? Warning : Muted;
        }
    }

    // Retain the same bounded history whether or not RichEdit is being updated.
    // Copy diagnostics and Save log use this buffer, not the frozen viewport.
    private string ActivityDiagnosticText()
    {
        var text = new StringBuilder(_activityHistoryCharacters);
        foreach (var run in _activityHistory) text.Append(run.Text);
        return text.ToString();
    }

    private void RememberActivityRuns(IReadOnlyList<ActivityRenderRun> runs)
    {
        foreach (var run in runs)
        {
            _activityHistory.Add(run);
            _activityHistoryCharacters += run.Text.Length;
        }
        if (_activityHistoryCharacters <= ActivityCharacterLimit) return;

        var history = ActivityDiagnosticText();
        var minimumRemoval = history.Length - ActivityCharactersToKeep;
        var lineEnd = history.IndexOf('\n', minimumRemoval);
        var removal = lineEnd >= 0 && lineEnd < history.Length - 1 ? lineEnd + 1 : minimumRemoval;
        if (removal < history.Length && char.IsLowSurrogate(history[removal]) && char.IsHighSurrogate(history[removal - 1]))
            removal++;
        _activityHistoryCharacters -= removal;
        _activityRemovedCharacters += removal;

        var remaining = removal;
        var fullRuns = 0;
        while (fullRuns < _activityHistory.Count && remaining >= _activityHistory[fullRuns].Text.Length)
            remaining -= _activityHistory[fullRuns++].Text.Length;
        if (fullRuns > 0) _activityHistory.RemoveRange(0, fullRuns);
        if (remaining > 0)
            _activityHistory[0] = _activityHistory[0] with { Text = _activityHistory[0].Text[remaining..] };
    }
}
