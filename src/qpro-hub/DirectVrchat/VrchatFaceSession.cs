using System.Diagnostics;
using System.Net.Sockets;

namespace QproFaceTracking.Hub;

// Pending discovery preserves the previous offer until a fresh check either
// identifies a direct failure or verifies a writable avatar input.
internal sealed record VrchatFaceStatus(string Text, bool Ready, bool? OfferBackup = null);

// The polling loop owns the native source and UDP writer. Slow discovery runs
// independently, so avatar changes and an unopened game cannot stall input.
internal sealed class VrchatFaceSession
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _stopLock = new();
    private bool _stopRequested;
    private TaskCompletionSource? _refreshRequested;
    private readonly Action<VrchatFaceSelection>? _setAdjustments;
    internal Task Completion => _loop;

    internal VrchatFaceSession(VrchatFaceSource source, Action<VrchatFaceStatus> report)
        : this(source.Read, source, report, VrchatOscQuery.DiscoverAsync, VrchatOscQuery.RefreshAsync,
            TimeSpan.FromSeconds(2), source.SetFaceAdjustments, source.ReadWithoutTarget) { }

    internal VrchatFaceSession(Func<IReadOnlyDictionary<string, float>> read, IDisposable source,
        Action<VrchatFaceStatus> report,
        Func<CancellationToken, Task<VrchatOscTarget?>> discover,
        Func<VrchatOscTarget, CancellationToken, Task<VrchatOscTarget?>> refresh, TimeSpan queryInterval,
        Action<VrchatFaceSelection>? setAdjustments = null, Action? readWithoutTarget = null)
    {
        _setAdjustments = setAdjustments;
        _loop = Task.Run(async () =>
        {
            using (source)
                await RunAsync(read, report, discover, refresh, queryInterval, readWithoutTarget, _stop.Token).ConfigureAwait(false);
        });
    }

    internal void UpdateSelection(VrchatFaceSelection selection) => _setAdjustments?.Invoke(selection);

    internal async Task RequestRefreshAsync(CancellationToken token)
    {
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Interlocked.CompareExchange(ref _refreshRequested, request, null) ?? request;
        await Task.WhenAny(pending.Task, _loop).WaitAsync(token).ConfigureAwait(false);
    }

    internal Task StopAsync()
    {
        lock (_stopLock)
        {
            if (!_stopRequested)
            {
                _stopRequested = true;
                _stop.Cancel();
                _ = _loop.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return _loop;
        }
    }

    private async Task RunAsync(Func<IReadOnlyDictionary<string, float>> read,
        Action<VrchatFaceStatus> report, Func<CancellationToken, Task<VrchatOscTarget?>> discover,
        Func<VrchatOscTarget, CancellationToken, Task<VrchatOscTarget?>> refresh, TimeSpan queryInterval,
        Action? readWithoutTarget, CancellationToken token)
    {
        VrchatOscTarget? target = null;
        VrchatOscTarget? writerTarget = null;
        VrchatOscTarget? queriedTarget = null;
        VrchatFaceOutput? output = null;
        Task<VrchatOscTarget?>? query = null;
        VrchatFaceStatus? lastStatus = null;
        TaskCompletionSource? checkedRequest = null;
        bool checkedWithoutTarget = false;
        var clock = Stopwatch.StartNew();
        var nextQuery = TimeSpan.Zero;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        using var queryLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);

        void Status(string text, bool ready = false, bool? offerBackup = null)
        {
            if (checkedRequest is null && lastStatus?.Text == text && lastStatus.Ready == ready && lastStatus.OfferBackup == offerBackup) return;
            lastStatus = new(text, ready, offerBackup);
            report(lastStatus);
            checkedRequest?.TrySetResult();
            checkedRequest = null;
        }

        try
        {
            Status("Looking for VRChat. Enable OSC in the VRChat Action Menu.");
            do
            {
                token.ThrowIfCancellationRequested();
                if (query is null && (clock.Elapsed >= nextQuery || Volatile.Read(ref _refreshRequested) is not null))
                {
                    queriedTarget = target;
                    query = target is null ? discover(queryLifetime.Token) : refresh(target, queryLifetime.Token);
                }
                if (query is { IsCompleted: true })
                {
                    VrchatOscTarget? found;
                    try { found = await query.ConfigureAwait(false); }
                    catch (Exception ex) when (ex is SocketException or IOException or HttpRequestException) { found = null; }
                    query = null;
                    checkedRequest = Interlocked.Exchange(ref _refreshRequested, null);
                    nextQuery = clock.Elapsed + queryInterval;
                    // A send failure may have disconnected this target while its refresh was in flight.
                    if (!ReferenceEquals(queriedTarget, target)) found = null;
                    if (found is not null) checkedWithoutTarget = false;
                    else if (checkedRequest is not null) checkedWithoutTarget = true;
                    if (found is not null && !SameAvatar(writerTarget, found))
                    {
                        // A changed avatar may reuse addresses for other actions. Never
                        // send the previous avatar's reset into the new namespace.
                        output?.Dispose();
                        output = new(found.Endpoint, found.Inputs, found.NativeFace, blockedInputs: found.BlockedInputs);
                        writerTarget = found;
                    }
                    else if (found is null && writerTarget?.AvatarId is null)
                    {
                        output?.Dispose();
                        output = null;
                        writerTarget = null;
                    }
                    // Suspend on discovery loss. A positively identified returning avatar can then
                    // release any expressions that went stale while its namespace was unavailable.
                    target = found;
                }

                if (target is null || output is null || output.SupportedInputCount == 0)
                {
                    if (readWithoutTarget is null) read();
                    else readWithoutTarget();
                    Status(target is not null ? "VRChat found. This avatar has no recognized face or eye inputs."
                        : checkedWithoutTarget ? "VRChat not found. Enable OSC in the Action Menu. Companion will keep looking."
                        : "Looking for VRChat. Enable OSC in the VRChat Action Menu.", offerBackup: target is not null || checkedWithoutTarget ? true : null);
                    continue;
                }
                var frame = read();
                token.ThrowIfCancellationRequested();
                try { output.Send(frame); }
                catch (SocketException)
                {
                    output.Dispose();
                    output = null;
                    writerTarget = null;
                    target = null;
                    Status("VRChat output disconnected. Looking for the client again.", offerBackup: true);
                    continue;
                }
                if (!output.HasActiveOutput) Status(output.EyeOnly
                    ? "This avatar accepts built-in eyes only. Waiting for eye data."
                    : "VRChat found. Waiting for matching face data from the selected source.", offerBackup: false);
                else Status(target.NativeFace ? "Sending native face tracking to VRChat." :
                    output.EyeOnly ? "Sending built-in eye tracking. This avatar has no recognized facial expression inputs." :
                    "Sending face tracking to this avatar's OSC parameters.", true);
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            // A refresh can identify a replacement while the last source read is in progress.
            // Stop must honor that observation before sending a neutral frame.
            bool stillCurrent = target is not null &&
                (query is not { IsCompletedSuccessfully: true } || SameAvatar(target, query.Result));
            try { if (stillCurrent) output?.Reset(); }
            catch (SocketException) { /* The game can exit before Companion. */ }
            finally { output?.Dispose(); }
            if (query is not null)
            {
                queryLifetime.Cancel();
                // Discovery owns HTTP and mDNS resources. Join their cleanup on both Stop and source failure.
                try { await query.ConfigureAwait(false); }
                catch (Exception) when (query.IsCanceled || query.IsFaulted) { }
            }
        }
    }

    private static bool SameAvatar(VrchatOscTarget? previous, VrchatOscTarget? next) =>
        ReferenceEquals(previous, next) || previous is not null && next is not null &&
        previous.Endpoint.Equals(next.Endpoint) && previous.Service == next.Service && previous.NativeFace == next.NativeFace &&
        previous.AvatarId == next.AvatarId && previous.BlockedInputs.SetEquals(next.BlockedInputs) && previous.Inputs.Count == next.Inputs.Count &&
        previous.Inputs.All(entry => next.Inputs.TryGetValue(entry.Key, out var type) && entry.Value == type);
}
