namespace QproFaceTracking.Hub;

// Only worker termination can consume the session's alert. Health refreshes,
// stale frames and diagnostic errors are observations, not confirmed stops.
internal sealed class HubTrackingStopAlert
{
    private bool _played;
    internal void BeginSession() => _played = false;
    internal bool TryNotify(bool workerExited, bool stopRequested, bool starting)
    {
        if (_played || !workerExited || stopRequested || starting) return false;
        _played = true;
        return true;
    }
}
