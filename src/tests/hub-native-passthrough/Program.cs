using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static partial class NativePassthroughTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Jaw = "/avatar/parameters/v2/JawOpen";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var loop = new ApplicationContext();
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        dispatcher.BeginInvoke(() =>
        {
            try { RunFixtures(args); }
            finally { loop.ExitThread(); }
        });
        Application.Run(loop);
    }

    private static void RunFixtures(string[] args)
    {
        var root = Directory.CreateTempSubdirectory("qpro-hub-native-");
        try
        {
            if (!args.Contains("--routes"))
            {
                TestLifetime(root.FullName);
                TestPendingRetryAndClose(root.FullName);
            }
            TestOutputRouteOwnership(root.FullName);
            TestInvalidRouteAndHostConflict(root.FullName);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Environment.ExitCode = 1;
        }
        finally { root.Delete(recursive: true); }
    }

    private static T Field<T>(HubForm form, string name) =>
        (T)typeof(HubForm).GetField(name, Private)!.GetValue(form)!;

    private static void Set(HubForm form, string name, object? value) =>
        typeof(HubForm).GetField(name, Private)!.SetValue(form, value);

    private static object? Call(HubForm form, string name, params object[] args) =>
        typeof(HubForm).GetMethod(name, Private)!.Invoke(form, args);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Wait(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            if (condition()) return;
            Application.DoEvents();
            Thread.Sleep(2);
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException(message);
    }

    private static void Complete(Task task, string message)
    {
        Wait(() => task.IsCompleted, message);
        task.GetAwaiter().GetResult();
    }

    private static HubForm Create(string root, FeedFixture fixture) =>
        new(root, rememberLaunch: false, previewOnly: true, nativeIdleFactory: fixture.Create)
        { ShowInTaskbar = false, Opacity = 0 };

    private static void TestLifetime(string root)
    {
        using var fixture = new FeedFixture();
        using var form = Create(root, fixture);
        Field<HubEnvironment>(form, "_environment").SetPreviewTrackingSource(false);
        Field<ComboBox>(form, "_trackingSourceLive").SelectedIndex = 0;
        Check(fixture.Attempts == 0, "Constructing the Hub started native output before Shown.");
        var saved = JsonSerializer.SerializeToElement(form.CaptureLiveOptions());
        Check(!saved.EnumerateObject().Any(property => property.Name.StartsWith("Native", StringComparison.OrdinalIgnoreCase)),
            "A native off preference is still persisted.");
        Check(typeof(HubForm).GetField("_nativeFace", Private) is null, "The native output checkbox still exists.");

        form.Show();
        Wait(() => fixture.Feeds.Count == 1 && fixture.Feeds[0].Reads >= 2,
            "Showing the Hub did not start Virtual Desktop input.");
        var first = fixture.Feeds[0];
        Check(!first.SteamLink && fixture.Game.Available == 0, "A pending discovery unexpectedly sent OSC.");
        fixture.MakeGameVisible();
        Wait(() => fixture.ReceiveJaw(.35f), "A late VRChat target did not receive the Virtual Desktop frame.");

        var workers = Field<List<Process>>(form, "_trackingProcesses");
        using var currentProcess = Process.GetCurrentProcess();
        Field<CheckBox>(form, "_controllerTouchpad").Checked = true;
        workers.Add(currentProcess);
        try
        {
            Call(form, "RequestNativeIdleRefresh");
            int before = first.Reads;
            Wait(() => first.Reads > before, "Controller-only tracking interrupted native polling.");
            Check(fixture.Attempts == 1 && !first.Disposed, "Controller-only tracking replaced its native session.");
        }
        finally
        {
            workers.Remove(currentProcess);
            Field<CheckBox>(form, "_controllerTouchpad").Checked = false;
        }

        first.HoldRefresh();
        try
        {
            Wait(() => first.RefreshPending, "The game discovery refresh did not enter its fixture gate.");
            Field<ComboBox>(form, "_trackingSourceLive").SelectedIndex = 1;
            Wait(() => first.RefreshCancelled, "Selecting Steam Link did not cancel the Virtual Desktop session.");
            Check(!first.Disposed, "The source disposed before its pending discovery finished cancelling.");
            Check(fixture.Attempts == 1, "Steam Link started before the previous session released its source.");
        }
        finally { first.AllowRefresh(); }
        Wait(() => fixture.Feeds.Count == 2 && fixture.ReceiveJaw(.75f),
            "Steam Link did not take over the native output after source switch.");
        var steam = fixture.Feeds[1];
        Check(first.Disposed && steam.SteamLink, "The source switch kept the previous reader alive.");
        Check(Field<ComboBox>(form, "_trackingSourceSetup").SelectedIndex == 1,
            "Switching providers did not update the setup source selector.");

        Complete((Task)Call(form, "SetNativeCaptureLabelsAsync", true)!, "Training label forwarding did not start.");
        Complete((Task)Call(form, "SetNativeCaptureLabelsAsync", false)!, "Training label forwarding did not stop.");
        Check(fixture.Attempts == 2 && steam.LabelStates.SequenceEqual([true, false]),
            "Steam training replaced native output or did not pair the label start and stop.");

        // Only the native/input sessions are simulated. The form's actual
        // suspend, foreground Stop, and resume code own the transition.
        Field<CheckBox>(form, "_eyebrowBoost").Checked = true;
        Set(form, "_starting", true);
        Complete((Task)Call(form, "SuspendNativeIdleAsync")!, "Foreground startup did not release the native feed.");
        Check(steam.Disposed, "Foreground startup returned before the idle source disposed.");
        var foreground = fixture.Create(true, _ => { }).Session;
        Set(form, "_directSession", foreground);
        Set(form, "_starting", false);
        Call(form, "RequestNativeIdleRefresh");
        Wait(() => fixture.Feeds[2].Reads > 0, "The foreground session did not read its fixture source.");
        Check(fixture.Attempts == 3, "The background session competed with foreground face adjustments.");
        Complete((Task)Call(form, "TryStopTrackingAsync")!, "Stopping foreground face adjustments hung.");
        Wait(() => fixture.Feeds.Count == 4 && fixture.Feeds[3].Reads >= 2 && fixture.ReceiveJaw(.75f),
            "Stopping foreground face adjustments did not resume native passthrough.");
        Check(fixture.Feeds[2].Disposed, "Foreground Stop left its session alive.");

        form.Close();
        Wait(() => fixture.Feeds[3].Disposed, "Closing the Hub did not release native passthrough.");
        Wait(() => fixture.ReceiveJaw(0f), "Hub close did not release the last active jaw value.");
        fixture.AssertPortReleased();
        Console.WriteLine("PASS native on Shown, late game, controller-only, source handoff, capture and foreground Stop");
    }

    private static void TestPendingRetryAndClose(string root)
    {
        using (var fixture = new FeedFixture { FailFirstStart = true })
        using (var form = Create(root, fixture))
        {
            form.Show();
            Wait(() => fixture.Attempts == 1, "The deliberate native port failure did not reach startup.");
            Call(form, "RequestNativeIdleRefresh");
            Call(form, "RequestNativeIdleRefresh");
            Check(fixture.Attempts == 1, "A pending retry was bypassed by routine refresh events.");
            Wait(() => fixture.Feeds.Count == 1 && fixture.Feeds[0].Reads >= 2,
                "A transient failure did not retry in the open Hub.", 9000);
            form.Close();
            Wait(() => fixture.Feeds[0].Disposed, "Closing after retry left the native source running.");
            fixture.AssertPortReleased();
        }
        using (var fixture = new FeedFixture { FailFirstStart = true })
        using (var form = Create(root, fixture))
        {
            form.Show();
            Wait(() => fixture.Attempts == 1, "The close-before-retry fixture did not start.");
            var retryToken = Field<CancellationTokenSource>(form, "_nativeIdleRetry").Token;
            form.Close();
            Wait(() => form.IsDisposed, "Closing the Hub with a scheduled retry did not finish.");
            Check(retryToken.IsCancellationRequested && fixture.Attempts == 1 && fixture.Feeds.Count == 0,
                "A pending retry restarted native output after the Hub closed.");
        }
        TestRetryTransition(root, switchProvider: false);
        TestRetryTransition(root, switchProvider: true);
        Console.WriteLine("PASS native retry cadence, cancellation during discovery, suspend, source switch and close");
    }

    private static void TestRetryTransition(string root, bool switchProvider)
    {
        using var fixture = new FeedFixture { FailFirstStart = true };
        using var form = Create(root, fixture);
        Field<HubEnvironment>(form, "_environment").SetPreviewTrackingSource(false);
        Field<ComboBox>(form, "_trackingSourceLive").SelectedIndex = 0;
        form.Show();
        Wait(() => fixture.Attempts == 1, "The transition fixture did not schedule the first-source retry.");
        var retryToken = Field<CancellationTokenSource>(form, "_nativeIdleRetry").Token;
        if (switchProvider)
        {
            Field<ComboBox>(form, "_trackingSourceLive").SelectedIndex = 1;
            Wait(() => fixture.Feeds.Count == 1, "Source switch waited for the different provider's retry.", 2000);
            Check(retryToken.IsCancellationRequested && fixture.Feeds[0].SteamLink,
                "Source switch kept the prior retry or opened the wrong provider.");
        }
        else
        {
            Complete((Task)Call(form, "SuspendNativeIdleAsync")!, "Foreground suspension hung on the scheduled retry.");
            Check(retryToken.IsCancellationRequested && fixture.Attempts == 1,
                "Foreground suspension did not cancel the scheduled retry.");
        }
        form.Close();
    }

    private sealed class FeedFixture : IDisposable
    {
        private readonly int _sourcePort;
        private readonly TaskCompletionSource<VrchatOscTarget?> _target =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal UdpClient Game { get; } = new(new IPEndPoint(IPAddress.Loopback, 0));
        internal List<FakeNativeSource> Feeds { get; } = [];
        internal int Attempts { get; private set; }
        internal bool FailFirstStart { get; init; }

        internal FeedFixture()
        {
            using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _sourcePort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        }

        internal NativeIdleFeed Create(bool steamLink, Action<VrchatFaceStatus> report)
        {
            Attempts++;
            if (FailFirstStart && Attempts == 1) throw new SocketException((int)SocketError.AddressAlreadyInUse);
            var source = new FakeNativeSource(_sourcePort, steamLink);
            var session = new VrchatFaceSession(source.Read, source, report,
                ct => _target.Task.WaitAsync(ct), source.Refresh,
                TimeSpan.FromMilliseconds(100));
            source.Session = session;
            Feeds.Add(source);
            return new(session, source.LabelStates.Add);
        }

        internal void MakeGameVisible() =>
            _target.TrySetResult(new((IPEndPoint)Game.Client.LocalEndPoint!, new Dictionary<string, string> { [Jaw] = "f" }));

        internal bool ReceiveJaw(float value)
        {
            bool found = false;
            IPEndPoint? sender = null;
            while (Game.Available > 0)
            {
                var bytes = Game.Receive(ref sender);
                int end = Array.IndexOf(bytes, (byte)0);
                if (end > 0 && Encoding.ASCII.GetString(bytes, 0, end) == Jaw &&
                    BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(^4)) == value)
                    found = true;
            }
            return found;
        }

        internal void AssertPortReleased()
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.ExclusiveAddressUse = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, _sourcePort));
        }

        public void Dispose()
        {
            foreach (var source in Feeds)
            {
                source.AllowRefresh();
                source.Session!.StopAsync().GetAwaiter().GetResult();
            }
            Game.Dispose();
        }
    }

    private sealed class FakeNativeSource : IDisposable
    {
        private readonly UdpClient _input = new(AddressFamily.InterNetwork);
        private readonly IReadOnlyDictionary<string, float> _frame;
        private TaskCompletionSource? _pendingRefresh;
        private int _reads, _refreshPending, _refreshCancelled, _disposed;
        internal bool SteamLink { get; }
        internal int Reads => Volatile.Read(ref _reads);
        internal bool RefreshPending => Volatile.Read(ref _refreshPending) != 0;
        internal bool RefreshCancelled => Volatile.Read(ref _refreshCancelled) != 0;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal VrchatFaceSession? Session;
        internal List<bool> LabelStates { get; } = [];

        internal FakeNativeSource(int port, bool steamLink)
        {
            SteamLink = steamLink;
            _frame = new Dictionary<string, float> { ["JawOpen"] = steamLink ? .75f : .35f };
            try
            {
                _input.Client.ExclusiveAddressUse = true;
                _input.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch { _input.Dispose(); throw; }
        }

        internal IReadOnlyDictionary<string, float> Read()
        {
            Interlocked.Increment(ref _reads);
            return _frame;
        }

        internal void HoldRefresh() =>
            Volatile.Write(ref _pendingRefresh, new(TaskCreationOptions.RunContinuationsAsynchronously));

        internal void AllowRefresh() => Volatile.Read(ref _pendingRefresh)?.TrySetResult();

        internal async Task<VrchatOscTarget?> Refresh(VrchatOscTarget target, CancellationToken token)
        {
            if (Volatile.Read(ref _pendingRefresh) is { } refresh)
            {
                using var registration = token.Register(() => Volatile.Write(ref _refreshCancelled, 1));
                Volatile.Write(ref _refreshPending, 1);
                await refresh.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            return target;
        }

        public void Dispose()
        {
            _input.Dispose();
            Volatile.Write(ref _disposed, 1);
        }
    }
}
