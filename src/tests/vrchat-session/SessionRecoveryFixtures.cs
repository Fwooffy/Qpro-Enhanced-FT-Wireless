using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using QproFaceTracking.Hub;

internal static class SessionRecoveryFixtures
{
    internal static async Task Run()
    {
        await Reconnect("avatar-a", "avatar-a", sameClient: true);
        await Reconnect("avatar-a", "avatar-b", sameClient: true);
        await Reconnect(null, null, sameClient: true);
        await Reconnect("avatar-a", "avatar-a", sameClient: false);
        await PendingDiscovery();
        await StopDuringAvatarChange();
        await SourceFailure();
    }

    private static readonly IReadOnlyDictionary<string, float> Jaw = new Dictionary<string, float> { ["JawOpen"] = .75f };
    private static readonly IReadOnlyDictionary<string, float> Empty = new Dictionary<string, float>();

    private static async Task Reconnect(string? originalId, string? returningId, bool sameClient)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)game.Client.LocalEndPoint!;
        var target = new VrchatOscTarget(endpoint, new Dictionary<string, string> { ["/avatar/parameters/v2/JawOpen"] = "f" })
        {
            AvatarId = originalId, Service = new("VRChat-Client-ABCD", endpoint)
        };
        var refresh = new TaskCompletionSource<VrchatOscTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rediscover = new TaskCompletionSource<VrchatOscTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int discoveries = 0, refreshes = 0;
        IReadOnlyDictionary<string, float> frame = Jaw;
        var session = new VrchatFaceSession(() => Volatile.Read(ref frame), new MemoryStream(), status =>
        {
            history.Enqueue(status.Text);
            if (refresh.Task.IsCompleted && status.Text.StartsWith("Looking for VRChat")) disconnected.TrySetResult();
            if (status.Text.Contains("Waiting for matching face data")) resumed.TrySetResult();
        }, ct => Interlocked.Increment(ref discoveries) == 1 ? Task.FromResult<VrchatOscTarget?>(target) : rediscover.Task.WaitAsync(ct),
            (current, ct) => Interlocked.Increment(ref refreshes) == 1 ? refresh.Task.WaitAsync(ct) : Task.FromResult<VrchatOscTarget?>(current),
            TimeSpan.FromMilliseconds(20));
        try
        {
            var packet = await game.ReceiveAsync(timeout.Token);
            if (BinaryPrimitives.ReadSingleBigEndian(packet.Buffer.AsSpan(^4)) != .75f) throw new Exception("Initial jaw missing.");
            refresh.SetResult(null);
            await disconnected.Task.WaitAsync(timeout.Token);
            Volatile.Write(ref frame, Empty);
            if (game.Available != 0) throw new Exception("Output sent to an unverified client.");
            rediscover.SetResult(target with { AvatarId = returningId,
                Service = sameClient ? target.Service : new("VRChat-Client-NEW", endpoint) });
            await resumed.Task.WaitAsync(timeout.Token);
            if (sameClient && originalId is not null && originalId == returningId)
            {
                if (game.Available == 0) throw new Exception("A returning avatar retained the last jaw value after its source expired.");
                var reset = await game.ReceiveAsync(timeout.Token);
                if (BinaryPrimitives.ReadSingleBigEndian(reset.Buffer.AsSpan(^4)) != 0) throw new Exception("Expected neutral jaw.");
            }
            else if (game.Available != 0) throw new Exception("Previous avatar's reset reached its replacement.");
        }
        catch (OperationCanceledException ex) { throw new Exception("Timed out: " + string.Join(" / ", history), ex); }
        finally { await session.StopAsync(); }
        Console.WriteLine($"PASS reconnect {originalId ?? "(unknown)"} to {returningId ?? "(unknown)"}, same client: {sameClient}.");
    }

    private static async Task PendingDiscovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<VrchatOscTarget?> Discover(CancellationToken token)
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.SetResult(); await finish.Task; }
            return null;
        }
        var session = new VrchatFaceSession(() => Empty, new MemoryStream(), _ => { }, Discover,
            (_, _) => throw new Exception("Unexpected refresh"), TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopped = session.StopAsync();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (stopped.IsCompleted) throw new Exception("Stop completed while network discovery still owned resources.");
        }
        finally { finish.TrySetResult(); await stopped.WaitAsync(TimeSpan.FromSeconds(3)); }
        Console.WriteLine("PASS Stop waits for cancelled discovery to finish.");
    }

    private static async Task StopDuringAvatarChange()
    {
        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var release = new ManualResetEventSlim();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TaskCompletionSource<VrchatOscTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new VrchatOscTarget((IPEndPoint)game.Client.LocalEndPoint!,
            new Dictionary<string, string> { ["/avatar/parameters/v2/JawOpen"] = "f" }) { AvatarId = "avatar-a" };
        bool refreshing = false;
        var session = new VrchatFaceSession(() =>
        {
            if (!refreshing) return Jaw;
            reading.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture did not release the source.");
            return Empty;
        }, new MemoryStream(), _ => { }, _ => Task.FromResult<VrchatOscTarget?>(target),
            (_, _) => { refreshing = true; return replacement.Task; }, TimeSpan.FromMilliseconds(20));
        try
        {
            await game.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
            replacement.SetResult(target with { AvatarId = "avatar-b" });
            var stopped = session.StopAsync();
            release.Set();
            await stopped.WaitAsync(TimeSpan.FromSeconds(3));
            if (game.Available != 0) throw new Exception("A superseded avatar received the previous session's reset during Stop.");
        }
        finally { replacement.TrySetResult(null); release.Set(); await session.StopAsync(); }
        Console.WriteLine("PASS Stop skips stale writes after an avatar change is already known.");
    }

    private static async Task SourceFailure()
    {
        var queryClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new MemoryStream();
        async Task<VrchatOscTarget?> Discover(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { queryClosed.SetResult(); }
            return null;
        }
        var session = new VrchatFaceSession(() => throw new IOException("fixture source failed"), source,
            _ => { }, Discover, (_, _) => throw new Exception("Unexpected refresh"), TimeSpan.FromSeconds(1));
        try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Source failure was lost."); }
        catch (IOException error) when (error.Message == "fixture source failed") { }
        if (source.CanRead || !queryClosed.Task.IsCompleted) throw new Exception("Source failure leaked pending discovery or its receiver.");
        try { await session.StopAsync(); } catch (IOException) { }
        Console.WriteLine("PASS source failure cancels discovery, releases the receiver and preserves the error.");
    }
}
