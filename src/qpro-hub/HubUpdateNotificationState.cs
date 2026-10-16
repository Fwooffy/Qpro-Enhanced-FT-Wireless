namespace QproFaceTracking.Hub;

// Availability and permission to interrupt the user are separate decisions.
// A deferred notice stays pending until the Hub is idle and in the foreground.
internal sealed class HubUpdateNotificationState
{
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    internal HubUpdateCheck? Pending { get; private set; }
    private HubUpdateCheck? _reviewable;

    internal void Observe(HubUpdateCheck check, Version current, bool includePrereleases)
    {
        if (check.State == HubUpdateCheckState.Unavailable)
        {
            // A transient network failure does not erase an earlier notice.
            RestrictChannel(includePrereleases);
            return;
        }
        _reviewable = Eligible(check, current, includePrereleases) ? check : null;
        Pending = _reviewable is not null && !_notified.Contains(Key(_reviewable.Release!)) ? _reviewable : null;
    }

    internal void RestrictChannel(bool includePrereleases)
    {
        if (!includePrereleases && Pending?.Release?.IsPrerelease == true) Pending = null;
        if (!includePrereleases && _reviewable?.Release?.IsPrerelease == true) _reviewable = null;
    }

    // Dismissing a notice only suppresses that popup. A later network failure
    // must not replace an already checked release with an unavailable dialog.
    internal HubUpdateCheck? ForReview(HubUpdateCheck? latest) =>
        latest is null || latest.State == HubUpdateCheckState.Unavailable ? _reviewable ?? latest : latest;

    internal void Acknowledge(HubUpdateRelease release)
    {
        string key = Key(release);
        _notified.Add(key);
        if (Pending?.Release is { } pending && Key(pending) == key) Pending = null;
    }

    internal static bool Eligible(HubUpdateCheck check, Version current, bool includePrereleases) =>
        (check.State is HubUpdateCheckState.Available or HubUpdateCheckState.ManualDownloadOnly) &&
        check.Release is { } release && release.Version > current && (!release.IsPrerelease || includePrereleases);

    internal static bool CanPrompt(bool windowReady, bool actionBusy, bool modalOpen, bool closing) =>
        windowReady && !actionBusy && !modalOpen && !closing;

    private static string Key(HubUpdateRelease release) => (release.IsPrerelease ? "prerelease:" : "stable:") + release.Tag;
}
