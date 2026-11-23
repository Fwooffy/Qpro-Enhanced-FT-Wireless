namespace QproFaceTracking.Hub;

internal sealed record NativeIdleFeed(VrchatFaceSession Session, Action<bool> SetCaptureLabels)
{
    internal static NativeIdleFeed Open(bool steamLink, Action<VrchatFaceStatus> report)
    {
        var source = new VrchatFaceSource(steamLink, unadjustedNative: true);
        try
        {
            return new(new VrchatFaceSession(source, report), source.SetSteamCaptureLabels);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }
}

internal sealed partial class HubForm
{
    private readonly Func<bool, Action<VrchatFaceStatus>, NativeIdleFeed> _nativeIdleFactory;
    private readonly SemaphoreSlim _nativeIdleTransition = new(1);
    private NativeIdleFeed? _nativeIdleFeed;
    private VrchatFaceSession? _nativeIdleSession => _nativeIdleFeed?.Session;
    private CancellationTokenSource? _nativeIdleRetry;
    private bool _retrySteam;
    private string? _nativeIdleProblem;
    private bool _nativeIdleShown;
    private bool _nativeIdleSteam;
    private bool _nativeCaptureLabels;

    private void BeginNativeIdle()
    {
        _nativeIdleShown = true;
        RequestNativeIdleRefresh();
    }

    private bool NativeIdleAvailable => _nativeIdleShown && !IsDisposed && !Disposing && !_closingInProgress;
    private bool MayOpenNativeIdle => NativeIdleAvailable && DirectRouteAvailable && _directSession is null && !_foregroundFaceStopping &&
        !(_starting && FaceAdjustmentsSelected && _startCancellation?.IsCancellationRequested != true);

    // Source changes, window closure and a foreground start can arrive during
    // a Steam socket release. Only one reader/writer may own the handover.
    private void RequestNativeIdleRefresh()
    {
        if (NativeIdleAvailable) _ = RefreshNativeIdleAsync();
    }

    private async Task RefreshNativeIdleAsync()
    {
        await _nativeIdleTransition.WaitAsync();
        try
        {
            if (!MayOpenNativeIdle || !EnsureDirectOwnership()) return;
            bool steam = _environment.SteamLinkSelected;
            if (_nativeIdleRetry is not null && _retrySteam == steam) return;
            if (_nativeIdleFeed is not null && _nativeIdleSteam == steam && !_nativeIdleFeed.Session.Completion.IsCompleted) return;
            await ReleaseNativeIdleAsync();
            if (!MayOpenNativeIdle) return;
            steam = _environment.SteamLinkSelected;
            CancelVrchatCheck();
            NativeIdleFeed? feed = null;
            feed = _nativeIdleFactory(steam, status => PostProcessUpdate("VRChat", () =>
            {
                if (feed is not null && ReferenceEquals(_nativeIdleFeed, feed) && NativeIdleAvailable && DirectRouteAvailable)
                {
                    if (status.Ready) _nativeIdleProblem = null;
                    ShowVrchatStatus(status);
                }
            }));
            _nativeIdleFeed = feed;
            _nativeIdleSteam = steam;
            if (steam && _nativeCaptureLabels) feed.SetCaptureLabels(true);
            _ = ObserveNativeIdleAsync(feed);
        }
        catch (Exception error)
        {
            await ReleaseNativeIdleAsync();
            RetryNativeIdle(error);
        }
        finally { _nativeIdleTransition.Release(); }
    }

    private async Task ObserveNativeIdleAsync(NativeIdleFeed feed)
    {
        Exception? failure = null;
        try { await feed.Session.Completion; }
        catch (Exception error) { failure = error; }
        if (!ReferenceEquals(_nativeIdleFeed, feed)) return;
        await _nativeIdleTransition.WaitAsync();
        try
        {
            if (!ReferenceEquals(_nativeIdleFeed, feed)) return;
            await ReleaseNativeIdleAsync();
            if (NativeIdleAvailable) RetryNativeIdle(failure ?? new IOException("The streaming source stopped."));
        }
        finally { _nativeIdleTransition.Release(); }
    }

    private void RetryNativeIdle(Exception error)
    {
        if (!NativeIdleAvailable) return;
        if (!string.Equals(_nativeIdleProblem, error.Message, StringComparison.Ordinal))
        {
            _nativeIdleProblem = error.Message;
            ShowVrchatStatus(new("Native tracking is waiting. " + error.Message + " Companion will retry automatically.", false, true));
        }
        if (_nativeIdleRetry is null)
        {
            _retrySteam = _environment.SteamLinkSelected;
            _ = RetryNativeIdleAsync();
        }
    }

    private async Task RetryNativeIdleAsync()
    {
        using var retry = new CancellationTokenSource();
        _nativeIdleRetry = retry;
        try { await Task.Delay(TimeSpan.FromSeconds(5), retry.Token); }
        catch (OperationCanceledException) when (retry.IsCancellationRequested) { return; }
        finally { if (ReferenceEquals(_nativeIdleRetry, retry)) _nativeIdleRetry = null; }
        RequestNativeIdleRefresh();
    }

    private async Task ReleaseNativeIdleAsync()
    {
        _nativeIdleRetry?.Cancel();
        _nativeIdleRetry = null;
        var previous = _nativeIdleFeed;
        _nativeIdleFeed = null;
        if (previous is null) return;
        try { await previous.Session.StopAsync(); }
        catch (Exception) when (previous.Session.Completion.IsFaulted)
        {
            // The session observer reports source failures. Its finally has
            // already reset owned outputs and released the reader and sockets.
        }
    }

    private async Task SuspendNativeIdleAsync()
    {
        await _nativeIdleTransition.WaitAsync();
        try { await ReleaseNativeIdleAsync(); }
        finally { _nativeIdleTransition.Release(); }
    }

    private async Task SetNativeCaptureLabelsAsync(bool enabled)
    {
        _nativeCaptureLabels = enabled;
        if (enabled) await RefreshNativeIdleAsync();
        if (_nativeIdleFeed is { } feed && _nativeIdleSteam) feed.SetCaptureLabels(enabled);
        else if (enabled)
        {
            _nativeCaptureLabels = false;
            throw new InvalidOperationException(_nativeIdleProblem ?? "The Steam Link face stream is unavailable for capture.");
        }
    }
}
