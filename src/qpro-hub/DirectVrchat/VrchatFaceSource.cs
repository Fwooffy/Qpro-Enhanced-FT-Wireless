using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Qpro.GazeBridge;
using Qpro.Shared;

namespace QproFaceTracking.Hub;

internal readonly record struct VrchatFacePorts(int Steam, int Gaze, int Tongue, int Pupil, int? Labels);

internal sealed partial class VrchatFaceSource : IDisposable
{
    private const string BodyStateMap = "VirtualDesktop.BodyState";
    private const int StateBytes = 360;
    private const int ExpressionCount = 70;
    private static readonly IReadOnlyDictionary<string, float> Empty = ReadOnlyDictionary<string, float>.Empty;
    private readonly byte[] _bodyStateFirst = new byte[StateBytes];
    private readonly byte[] _bodyStateSecond = new byte[StateBytes];
    private readonly byte[] _bodyStateLast = new byte[StateBytes];
    private readonly NativeFaceSource _source;
    private readonly Func<long> _clock;
    private readonly bool _loadPreferences;
    private readonly bool _unadjustedNative;
    private VrchatFaceSelection _requestedAdjustments;
    private VrchatFaceSelection _adjustments;
    private bool RawNative => _unadjustedNative || !_adjustments.HasAdjustments;
    private readonly bool _labelsOnly;
    private readonly string _mapName;
    private readonly UdpClient? _gazeSocket;
    private readonly UdpClient? _tongueSocket;
    private readonly UdpClient? _pupilSocket;
    private readonly SteamOscSource? _steam;
    private UdpClient? _steamLabels;
    private readonly SmirkTracker _smirk = new();
    private readonly CheekPuffTracker _cheekPuff = new();
    private readonly CheekSuckTracker _cheekSuck = new();
    private CheekPuffMode _cheekPuffMode = CheekPuffMode.Strong;
    private CheekSuckMode _cheekSuckMode = CheekSuckMode.Strong;
    private CheekPuffCalibration? _cheekCalibration;
    private EyebrowSettings _eyebrows = EyebrowPreference.Default;
    private int _preferencesChanged = 1;
    private long _nextMapRead;
    private long? _lastStableBodyState;
    private bool _discardSteamBacklog;
    private bool _disposed;

    internal VrchatFaceSource(bool steamLink, bool labelsOnly = false,
        bool enableGaze = false, bool enableTongue = false, bool enablePupils = false, bool unadjustedNative = false)
        : this(steamLink, BodyStateMap, new(SteamOscSource.DefaultPort, 27275, 27276, 27277,
            labelsOnly || enableTongue || unadjustedNative ? 27274 : null),
            () => Environment.TickCount64, loadPreferences: true, labelsOnly, enableGaze, enableTongue, enablePupils,
            unadjustedNative)
    {
    }

    internal VrchatFaceSource(bool steamLink, string mapName, VrchatFacePorts ports,
        Func<long> clock, bool loadPreferences, bool labelsOnly = false,
        bool enableGaze = true, bool enableTongue = true, bool enablePupils = true, bool unadjustedNative = false)
    {
        if (labelsOnly && !steamLink)
            throw new ArgumentException("Labels-only capture requires Steam Link.", nameof(labelsOnly));
        _source = steamLink ? NativeFaceSource.SteamLink : NativeFaceSource.VirtualDesktop;
        _mapName = mapName;
        _clock = clock;
        _unadjustedNative = unadjustedNative;
        _adjustments = _requestedAdjustments = new(enableGaze, enableTongue, enablePupils, true, true, true);
        _loadPreferences = loadPreferences && !unadjustedNative;
        _labelsOnly = labelsOnly;
        try
        {
            bool overlays = !labelsOnly && !unadjustedNative;
            if (overlays && enableGaze)
                _gazeSocket = BindOverlay("Qpro camera gaze", ports.Gaze);
            if (overlays && enableTongue)
                _tongueSocket = BindOverlay("Qpro camera tongue", ports.Tongue);
            if (overlays && enablePupils)
                _pupilSocket = BindOverlay("Qpro camera pupil", ports.Pupil);
            if (steamLink)
            {
                try
                {
                    _steam = new SteamOscSource(ports.Steam);
                }
                catch (SocketException error)
                {
                    throw PortUnavailable("Steam Link OSC", ports.Steam, error);
                }
                if (ports.Labels is int labelsPort)
                {
                    _steamLabelTarget = new IPEndPoint(IPAddress.Loopback, labelsPort);
                    if (!unadjustedNative)
                        _steamLabels = new UdpClient(AddressFamily.InterNetwork);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Source reads, label sends and disposal all belong to the output loop.
    internal void ReadWithoutTarget()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyFaceAdjustments();
        if (!RawNative)
        {
            Read(); // Foreground camera freshness and labels follow the normal path.
            return;
        }
        if (_gazeSocket is not null) LocalDatagrams.DiscardPending(_gazeSocket);
        if (_tongueSocket is not null) LocalDatagrams.DiscardPending(_tongueSocket);
        if (_pupilSocket is not null) LocalDatagrams.DiscardPending(_pupilSocket);
        if (_steam is null) return; // The next VD read samples the current map directly.

        long now = _clock();
        ApplySteamCaptureRequest(now);
        if (_steamLabels is null || !_unadjustedNative || _discardSteamBacklog)
        {
            // Steam timestamps packets on receipt. Drain while waiting so data
            // queued without an output target cannot become a new tracking pose.
            _discardSteamBacklog = !_steam.DiscardPending();
            return;
        }
        _steam.Poll(now);
        Span<float> weights = stackalloc float[ExpressionCount];
        ReadSteam(now, weights, _steam); // Publish native capture samples without mapping an avatar.
    }

    internal IReadOnlyDictionary<string, float> Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyFaceAdjustments();
        long now = _clock();
        if (_unadjustedNative && _steam is not null)
            ApplySteamCaptureRequest(now);
        if (_discardSteamBacklog && _steam is not null)
        {
            _discardSteamBacklog = !_steam.DiscardPending();
            return Empty;
        }
        _steam?.Poll(now);
        Span<float> weights = stackalloc float[ExpressionCount];
        NativeFrame native = _steam is not null
            ? ReadSteam(now, weights, _steam)
            : ReadVirtualDesktop(now, weights);
        if (_labelsOnly)
            return Empty;

        if (!RawNative) RefreshPreferences();
        if (_gazeSocket is not null || _pupilSocket is not null || _tongueSocket is not null)
            ReadCameraPackets(now);
        if (!RawNative && !native.LowerAvailable)
        {
            _smirk.Reset();
            _cheekPuff.Reset();
            _cheekSuck.Reset();
        }
        bool cameraTongue = _adjustments.Tongue && _tongue.Resolve(now, native.TongueAvailable) == OverlayOutput.Custom;
        EyeAngles? cameraLeft = _adjustments.Gaze && _leftGaze.Resolve(now, native.Left.HasValue) == OverlayOutput.Custom ? _leftCameraAngles : null;
        EyeAngles? cameraRight = _adjustments.Gaze && _rightGaze.Resolve(now, native.Right.HasValue) == OverlayOutput.Custom ? _rightCameraAngles : null;
        EyeAngles? left = cameraLeft ?? native.Left;
        EyeAngles? right = cameraRight ?? native.Right;
        // Neither native transport measures pupil diameter. The active camera
        // session uses its neutral fallback, while raw forwarding owns no pupil.
        float? leftPupil = RawNative || !_adjustments.Pupils ? null :
            ResolvePupil(_leftPupil, now, _leftCameraPupil, native.LeftBlink || native.Left.HasValue);
        float? rightPupil = RawNative || !_adjustments.Pupils ? null :
            ResolvePupil(_rightPupil, now, _rightCameraPupil, native.RightBlink || native.Right.HasValue);
        if (!native.UpperAvailable && !native.LowerAvailable &&
            !cameraTongue && left is null && right is null && leftPupil is null && rightPupil is null)
            return Empty;

        var output = new Dictionary<string, float>(native.LowerAvailable ? 88 : 36, StringComparer.Ordinal);
        if (native.UpperAvailable)
            WriteUpperFace(output, weights);
        if (native.LeftBlink)
            output["EyeLeftOpenness"] = Openness(weights[12], weights[28]);
        if (native.RightBlink)
            output["EyeRightOpenness"] = Openness(weights[13], weights[29]);
        if (native.LowerAvailable)
            WriteLowerFace(output, weights, now);
        if (native.TongueAvailable || cameraTongue)
            WriteTongue(output, weights, native.FaceFlags, cameraTongue);
        // QPGE direct mode uses anatomical eyes. Legacy avatar parameters need
        // the camera-only crossover measured on that route, with native fallback
        // on each named channel. Built-in and native-face directions stay anatomical.
        WriteEyeGaze(output, left, cameraRight ?? native.Left, left: true);
        WriteEyeGaze(output, right, cameraLeft ?? native.Right, left: false);
        if (leftPupil is float leftDiameter)
            output["EyeLeftPupil"] = leftDiameter;
        if (rightPupil is float rightDiameter)
            output["EyeRightPupil"] = rightDiameter;
        return output;
    }

    private NativeFrame ReadSteam(long now, Span<float> weights, SteamOscSource steam)
    {
        steam.Expressions.CopyTo(weights);
        bool faceFresh = steam.HasFreshFace(now);
        bool upper = faceFresh && steam.HasAvailableUpperFace(now);
        bool lower = faceFresh && steam.HasAvailableLowerFace(now);
        long? tongueAge = lower ? steam.ExpressionAge(68, now) : null;
        EyeAngles? gaze = steam.TryGetCombinedGaze(now, out float x, out float y, out var point)
            ? new(x, y, VrchatGazeDirection.FromSteamPoint(point.X, point.Y, point.Z)) : null;
        if (faceFresh && (!RawNative || _unadjustedNative || _labelsOnly))
            PublishSteamLabels(now, weights, lower, upper, tongueAge, gaze);
        return new((byte)(lower ? 1 : 0), upper, lower, upper && steam.ExpressionAge(12, now).HasValue,
            upper && steam.ExpressionAge(13, now).HasValue, tongueAge.HasValue, gaze, gaze);
    }

    private NativeFrame ReadVirtualDesktop(long now, Span<float> weights)
    {
        // This map has no frame timestamp. Read handles are scoped to a
        // sample so a stopped Streamer cannot leave us holding its old map.
        if (now < _nextMapRead || !OperatingSystem.IsWindows())
            return default;
        try
        {
            if (VirtualDesktopFaceMap.ReadStable(_mapName, _bodyStateFirst, _bodyStateSecond))
            {
                _bodyStateSecond.CopyTo(_bodyStateLast, 0);
                _lastStableBodyState = now;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            _lastStableBodyState = null;
            _nextMapRead = now + 250;
            return default;
        }

        // A torn read can briefly reuse the last coherent snapshot while
        // the producer still owns the map. A missing map clears it immediately.
        if (_lastStableBodyState is not long stable || now - stable is < 0 or > 100)
            return default;
        ReadOnlySpan<byte> state = _bodyStateLast;
        for (int index = 0; index < weights.Length; index++)
            weights[index] = Weight(ReadFloat(state, 4 + index * sizeof(float)));
        bool upper = state[1] != 0;
        bool lower = (state[0] & 1) != 0;
        EyeAngles? left = state[292] != 0 ? QuaternionToAngles(state, 296) : null;
        EyeAngles? right = state[293] != 0 ? QuaternionToAngles(state, 324) : null;
        return new(state[0], upper, lower, upper, upper, lower, left, right);
    }

    internal void SetFaceAdjustments(VrchatFaceSelection selection)
    {
        if (_unadjustedNative) return;
        Volatile.Write(ref _requestedAdjustments, selection);
        Interlocked.Exchange(ref _preferencesChanged, 1);
    }

    private void ApplyFaceAdjustments()
    {
        var next = Volatile.Read(ref _requestedAdjustments);
        if (next == _adjustments) return;
        if (_adjustments.Gaze != next.Gaze)
        {
            _leftGaze.Reset();
            _rightGaze.Reset();
            LocalDatagrams.DiscardPending(_gazeSocket);
        }
        if (_adjustments.Tongue != next.Tongue)
        {
            _tongue.Reset();
            LocalDatagrams.DiscardPending(_tongueSocket);
        }
        if (_adjustments.Pupils != next.Pupils)
        {
            _leftPupil.Reset();
            _rightPupil.Reset();
            LocalDatagrams.DiscardPending(_pupilSocket);
        }
        if (_adjustments.CheekPuff != next.CheekPuff) _cheekPuff.Reset();
        if (_adjustments.CheekSuck != next.CheekSuck) _cheekSuck.Reset();
        if (_adjustments.HasAdjustments != next.HasAdjustments) _smirk.Reset();
        _adjustments = next;
    }

    private void RefreshPreferences()
    {
        if (!_loadPreferences || Interlocked.Exchange(ref _preferencesChanged, 0) == 0)
            return;
        _eyebrows = EyebrowPreference.Load();
        _cheekPuffMode = (CheekPuffMode)HubCheekPuffPreference.LoadMode();
        _cheekSuckMode = (CheekSuckMode)HubCheekSuckPreference.LoadMode();
        _cheekCalibration = _cheekPuffMode == CheekPuffMode.Calibrated
            ? CheekPuffCalibrationProfile.Load(_source == NativeFaceSource.SteamLink
                ? CheekPuffCalibrationProfile.SteamLinkSource : CheekPuffCalibrationProfile.VirtualDesktopSource)
            : null;
    }

    private static UdpClient BindOverlay(string name, int port)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.ExclusiveAddressUse = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Client.Blocking = false;
            return socket;
        }
        catch (SocketException error)
        {
            socket.Dispose();
            throw PortUnavailable(name, port, error);
        }
    }

    private static InvalidOperationException PortUnavailable(string name, int port, SocketException error) =>
        new($"Could not listen for {name} on UDP port {port}. Close any other tracking app using that port.", error);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _steamLabels?.Dispose();
        _steam?.Dispose();
        _pupilSocket?.Dispose();
        _tongueSocket?.Dispose();
        _gazeSocket?.Dispose();
    }

    private readonly record struct EyeAngles(float X, float Y, VrchatGazeDirection Direction);
    private readonly record struct NativeFrame(byte FaceFlags, bool UpperAvailable, bool LowerAvailable,
        bool LeftBlink, bool RightBlink, bool TongueAvailable, EyeAngles? Left, EyeAngles? Right);
}
