using System.Collections.Concurrent;
using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private sealed record ProcessUiUpdate(string Label, string? Text, string Timestamp, Action? Apply);
    private readonly ConcurrentQueue<ProcessUiUpdate> _processUiUpdates = new();
    private int _processUiDrainScheduled;
    private List<ActivityEntry>? _activityBatch;
    private bool _trackingHealthRefreshPending;

    // Keep one dispatch outstanding for pipe output. Each turn has a work
    // budget; messages, timestamps and terminal output are never truncated.
    private void QueueProcessUiUpdate(string label, string? text, Action? update)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        _processUiUpdates.Enqueue(new(label, text, DateTime.Now.ToString("HH:mm:ss"), update));
        ScheduleProcessUiDrain();
    }

    private void ScheduleProcessUiDrain()
    {
        if (Interlocked.CompareExchange(ref _processUiDrainScheduled, 1, 0) != 0) return;
        try { BeginInvoke(DrainProcessUiUpdates); }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _processUiDrainScheduled, 0);
            if (IsDisposed || Disposing || !IsHandleCreated) _processUiUpdates.Clear();
        }
    }

    private void DrainProcessUiUpdates()
    {
        try
        {
            if (IsDisposed || Disposing) { _processUiUpdates.Clear(); return; }
            var entries = new List<ActivityEntry>();
            _activityBatch = entries;
            using var feedback = new ProcessFeedbackLayoutScope([_actionTitle.Parent, _trackingHealthCard]);
            using var readiness = BeginReadinessUpdate();
            try
            {
                var elapsed = Stopwatch.StartNew();
                var characters = 0;
                for (var count = 0; count < 32 && _processUiUpdates.TryDequeue(out var item); count++)
                {
                    if (item.Text is not null)
                    {
                        RecordActivity(item.Text, item.Timestamp);
                        characters += item.Text.Length;
                    }
                    ApplyProcessUpdate(item.Label, item.Apply);
                    if (IsDisposed || Disposing || characters >= 32_768 || elapsed.ElapsedMilliseconds >= 8) break;
                }
            }
            finally
            {
                _activityBatch = null;
                var refreshHealth = _trackingHealthRefreshPending;
                _trackingHealthRefreshPending = false;
                if (!IsDisposed && !Disposing)
                {
                    if (refreshHealth) RefreshTrackingHealth();
                    AppendActivityEntries(entries);
                }
            }
        }
        finally
        {
            if (IsDisposed || Disposing)
            {
                _processUiUpdates.Clear();
                Interlocked.Exchange(ref _processUiDrainScheduled, 0);
            }
            else if (!_processUiUpdates.IsEmpty) _ = ScheduleNextProcessUiDrainAsync();
            else
            {
                Interlocked.Exchange(ref _processUiDrainScheduled, 0);
                // A producer may have arrived between the empty check and
                // releasing dispatch ownership.
                if (!_processUiUpdates.IsEmpty) ScheduleProcessUiDrain();
            }
        }
    }

    private async Task ScheduleNextProcessUiDrainAsync()
    {
        // WinForms can run a BeginInvoke posted from its current callback in
        // the same callback loop. Yield between batches so continuous output
        // cannot monopolize that loop and postpone input or native painting.
        await Task.Delay(16).ConfigureAwait(false);
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            _processUiUpdates.Clear();
            Interlocked.Exchange(ref _processUiDrainScheduled, 0);
            return;
        }
        try { BeginInvoke(DrainProcessUiUpdates); }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _processUiDrainScheduled, 0);
            if (IsDisposed || Disposing || !IsHandleCreated) _processUiUpdates.Clear();
        }
    }

    private void ApplyProcessUpdate(string label, Action? update)
    {
        if (update is null || IsDisposed || Disposing) return;
        try { update(); }
        catch (Exception error) { AppendLog($"[{label}] Progress display could not update: {error.Message}"); }
    }

    private void RequestTrackingHealthRefresh()
    {
        if (_activityBatch is not null) _trackingHealthRefreshPending = true;
        else RefreshTrackingHealth();
    }

    private sealed class ProcessFeedbackLayoutScope : IDisposable
    {
        private readonly Control[] _containers;
        internal ProcessFeedbackLayoutScope(IEnumerable<Control?> containers)
        {
            _containers = containers.OfType<Control>().Distinct().ToArray();
            foreach (var container in _containers) container.SuspendLayout();
        }
        public void Dispose()
        {
            foreach (var container in _containers.Reverse())
                if (!container.IsDisposed && !container.Disposing) container.ResumeLayout(true);
        }
    }

    private void PostProcessOutput(string label, string text, Action update)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired) { QueueProcessUiUpdate(label, text, update); return; }
        using var readiness = BeginReadinessUpdate();
        AppendLog(text);
        ApplyProcessUpdate(label, update);
    }

    private void RecordActivity(string text, string timestamp)
    {
        void Add(ActivityEntry entry)
        {
            if (_activityBatch is not null) _activityBatch.Add(entry);
            else AppendActivityEntries([entry]);
        }
        try { _componentUpdateOutput?.Invoke(text); }
        catch (Exception)
        {
            Add(new("Component update progress could not refresh. Its helper output is still shown here.",
                timestamp, ActivitySeverity.Warning));
        }
        Add(new(text, timestamp));
    }
}
