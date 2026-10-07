using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Qpro.Shared;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

namespace Qpro.GazeBridge;

[SupportedOSPlatform("windows")]

public sealed class TrackingModule : ExtTrackingModule
{
    private const string MapName = "VirtualDesktop.BodyState";
    private const int StateBytes = 360;
    private const int ExpressionOffset = 4;
    private const int ExpressionCount = 70;
    private const int GazePort = 27275;
    private const int GazePacketBytes = 24;
    private const long GazeTimeoutMs = 250;
    private const int TonguePort = 27276;
    private const int TonguePacketBytes = 56;
    private const long TongueTimeoutMs = 300;
    private const int PupilPort = 27277;
    private const int PupilPacketBytes = 16;
    private const long PupilTimeoutMs = 350;
    private const int LabelPort = 27274;
    private const long SteamFaceTimeoutMs = 500;
    private static readonly string CheekPuffModePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QproFaceTracking", "config", "cheek-puff-mode.txt");
    private static readonly string CheekSuckModePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QproFaceTracking", "config", "cheek-suck-mode.txt");
    private static readonly (int Source, int[] Targets)[] ExpressionMap =
    [
        (8, [67]), (9, [66]),
        (10, [65]), (11, [64]), (24, [22]), (25, [24]),
        (26, [23]), (27, [25]), (30, [61]), (31, [60]),
        (34, [39]), (35, [37]),
        (36, [38]), (37, [36]), (38, [69]), (39, [68]),
        (40, [41, 43]), (41, [40, 42]), (42, [63]), (43, [62]),
        (44, [33]), (46, [32]), (48, [71]), (49, [70]),
        (50, [29]), (51, [51]), (52, [50]), (53, [53, 55]),
        (54, [52, 54]), (55, [49]), (56, [48])
    ];
    // Matches the camera packet and NativeTongueMapping.WriteSlots order.
    private static readonly int[] TongueExpressions =
    [
        (int)UnifiedExpressions.TongueOut,
        (int)UnifiedExpressions.TongueUp,
        (int)UnifiedExpressions.TongueDown,
        (int)UnifiedExpressions.TongueLeft,
        (int)UnifiedExpressions.TongueRight,
        (int)UnifiedExpressions.TongueRoll,
        (int)UnifiedExpressions.TongueBendDown,
        (int)UnifiedExpressions.TongueCurlUp,
        (int)UnifiedExpressions.TongueSquish,
        (int)UnifiedExpressions.TongueFlat,
        (int)UnifiedExpressions.TongueTwistLeft,
        (int)UnifiedExpressions.TongueTwistRight
    ];

    private readonly byte[] _first = new byte[StateBytes];
    private readonly byte[] _second = new byte[StateBytes];
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private UdpClient? _gazeSocket;
    private UdpClient? _tongueSocket;
    private UdpClient? _pupilSocket;
    private UdpClient? _cheekCameraSocket;
    private readonly CheekCameraReceiver _cheekCamera = new();
    private bool _cheekCameraTransportReady = true;
    private UdpClient? _steamLabelSocket;
    private UdpClient? _cheekTelemetrySocket;
    private static readonly IPEndPoint CheekTelemetryEndpoint = new(IPAddress.Loopback, CheekPuffTelemetry.Port);
    private long _nextCheekTelemetryTick;
    private SteamOscSource? _steamSource;
    private bool _useSteamLink;
    private long _nextSteamLabelTick;
    private long _nextSteamSchemaTick;
    private long _steamLabelSequence;
    private long _steamSourceChangeSequence;
    private long _lastSteamFaceTick;
    private long _nextSteamFaceWarningTick;
    private bool _steamFaceUnavailableReported;
    private bool _needsEye;
    private bool _needsExpression;
    private CheekPuffMode _cheekPuffMode = CheekPuffMode.Strong;
    private readonly CheekPuffTracker _cheekPuffTracker = new();
    private CheekPuffCalibration? _cheekPuffCalibration;
    private string? _cheekPuffCalibrationOrigin;
    private long _nextCheekPuffModeCheckTick;
    private CheekSuckMode _cheekSuckMode = CheekSuckMode.Strong;
    private readonly CheekSuckTracker _cheekSuckTracker = new();
    private long _nextCheekSuckModeCheckTick;
    private readonly Func<bool> _cheekSessionProbe = () => CheekTrackingSession.IsActive();
    private bool _cheekSessionActive;
    private long _nextCheekSessionCheckTick;
    private bool _cheekPuffNativeOutputApplied;
    private bool _cheekSuckOutputApplied;
    private readonly SmirkTracker _smirkTracker = new();
    private EyebrowSettings _eyebrowSettings = EyebrowPreference.Default;
    private long _nextEyebrowSettingsCheckTick;
    private readonly Func<long> _tickClock;
    private readonly string _mapName;
    private bool _wasActive;
    private readonly LiveOverlayState _leftGazeOverlay = new(GazeTimeoutMs);
    private readonly LiveOverlayState _rightGazeOverlay = new(GazeTimeoutMs);
    private readonly LiveOverlayState _tongueOverlay = new(TongueTimeoutMs);
    private readonly LiveOverlayState _leftPupilOverlay = new(PupilTimeoutMs);
    private readonly LiveOverlayState _rightPupilOverlay = new(PupilTimeoutMs);
    private float _leftGazeX;
    private float _leftGazeY;
    private float _rightGazeX;
    private float _rightGazeY;
    private readonly float[] _tongueValues = new float[NativeTongueMapping.SlotCount];
    private bool _tongueDirty;
    private float _leftPupilMm;
    private float _rightPupilMm;

    // One module owns both VRCFT slots for the selected headset streaming
    // source and overlays fresh Qpro gaze, tongue, and pupil packets.
    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

    public TrackingModule() : this(() => Environment.TickCount64) { }

    internal TrackingModule(Func<long> tickClock, string mapName = MapName)
    {
        _tickClock = tickClock;
        _mapName = mapName;
    }

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(
        bool eyeAvailable,
        bool expressionAvailable)
    {
        _needsEye = eyeAvailable;
        _needsExpression = expressionAvailable;
        ResetOverlayState();
        _wasActive = false;
        _useSteamLink = ReadSteamLinkSelection();
        _cheekPuffTracker.Reset();
        _cheekSessionActive = false;
        _nextCheekSessionCheckTick = 0;
        _nextCheekPuffModeCheckTick = 0;
        _cheekPuffCalibration = null;
        _cheekPuffCalibrationOrigin = null;
        ModuleInformation = new ModuleMetadata
        {
            Name = _useSteamLink
                ? "Quest Pro + Steam Link face/tongue/pupil"
                : "Quest Pro gaze + Virtual Desktop face/tongue/pupil v0.7"
        };

        if (_useSteamLink)
        {
            try
            {
                _steamSource = new SteamOscSource();
                _steamLabelSocket = new UdpClient(AddressFamily.InterNetwork);
                _steamLabelSocket.Connect(IPAddress.Loopback, LabelPort);
            }
            catch (SocketException error)
            {
                Logger.LogError(error,
                    "Could not bind Steam Link OSC port 9015. Close any other Steam Link VRCFT module and restart VRCFaceTracking.");
                _steamSource?.Dispose();
                _steamSource = null;
                _steamLabelSocket?.Dispose();
                _steamLabelSocket = null;
                return (false, false);
            }
        }

        try
        {
            _gazeSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, GazePort));
            _gazeSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        {
            Logger.LogError(error, "Could not bind the local Quest Pro gaze port {Port}", GazePort);
        }

        try
        {
            _tongueSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, TonguePort));
            _tongueSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        {
            Logger.LogError(error, "Could not bind the local Quest Pro tongue port {Port}", TonguePort);
        }

        try
        {
            _pupilSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, PupilPort));
            _pupilSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        {
            Logger.LogError(error, "Could not bind the local Quest Pro pupil port {Port}", PupilPort);
        }

        try
        {
            _cheekCameraSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, CheekCameraReceiver.Port));
            _cheekCameraSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        {
            Logger.LogError(error, "Could not bind the local Quest Pro camera cheek port {Port}", CheekCameraReceiver.Port);
        }

        if (!_useSteamLink)
            TryOpenMap();
        try
        {
            _cheekTelemetrySocket = new UdpClient(AddressFamily.InterNetwork);
        }
        catch (SocketException error)
        {
            Logger.LogWarning(error, "Cheek calibration preview could not open its local output socket.");
        }
        _nextCheekTelemetryTick = 0;
        Logger.LogInformation(
            "Quest Pro combined bridge initialized: source={Source}, eye={Eye}, face={Face}; Qpro gaze freshness={Timeout} ms",
            _useSteamLink ? "Steam Link OSC 9015" : "Virtual Desktop", _needsEye,
            _needsExpression, GazeTimeoutMs);
        return (_needsEye, _needsExpression);
    }

    public override void Update()
    {
        if (Status != ModuleState.Active)
        {
            DiscardInactivePackets();
            _wasActive = false;
            Thread.Sleep(10);
            return;
        }
        if (!_wasActive)
        {
            // Packets queued while another module owned the slots must not
            // become fresh merely because this module was activated again.
            _wasActive = DiscardInactivePackets();
            if (!_wasActive)
            {
                Thread.Sleep(5);
                return;
            }
        }
        RefreshCheekSession();
        ReceiveGaze();
        ReceiveTongue();
        ReceivePupil();
        ReceiveCameraCheeks();
        if (_useSteamLink)
        {
            UpdateSteamLink();
            Thread.Sleep(5);
            return;
        }
        if (!TryReadState())
        {
            UpdateUnavailableSource();
            Thread.Sleep(10);
            return;
        }

        Span<float> expressions = stackalloc float[ExpressionCount];
        for (int index = 0; index < ExpressionCount; ++index)
            expressions[index] = BitConverter.ToSingle(_second, ExpressionOffset + index * 4);

        byte faceFlags = _second[0];
        bool lowerFaceAvailable = (faceFlags & 1) != 0;
        if (lowerFaceAvailable)
            PublishCheekCalibrationSample(expressions, NativeFaceSource.VirtualDesktop, _tickClock());

        if (_needsEye)
            UpdateEyes(expressions);
        if ((_needsEye || _needsExpression) && _second[1] != 0)
            UpdateBrowExpressions(expressions);
        if (_needsExpression)
        {
            if (lowerFaceAvailable)
                UpdateMouth(expressions, faceFlags, NativeFaceSource.VirtualDesktop);
            else
            {
                UpdateTongueOutput(expressions, faceFlags, NativeFaceSource.VirtualDesktop, nativeAvailable: false);
                UpdateCheekOutput(default, nativeAvailable: false);
                UpdateCheekSuckOutput(default, nativeAvailable: false);
            }
        }
        Thread.Sleep(5);
    }

    public override void Teardown()
    {
        ResetOverlayState();
        _wasActive = false;
        _smirkTracker.Reset();
        _cheekPuffTracker.Reset();
        _gazeSocket?.Dispose();
        _gazeSocket = null;
        _tongueSocket?.Dispose();
        _tongueSocket = null;
        _pupilSocket?.Dispose();
        _pupilSocket = null;
        _cheekCameraSocket?.Dispose();
        _cheekCameraSocket = null;
        _cheekCamera.Reset();
        _steamSource?.Dispose();
        _steamSource = null;
        _steamLabelSocket?.Dispose();
        _steamLabelSocket = null;
        _cheekTelemetrySocket?.Dispose();
        _cheekTelemetrySocket = null;
        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }

    private void TryOpenMap()
    {
        if (_view is not null)
            return;
        try
        {
            _map = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, StateBytes, MemoryMappedFileAccess.Read);
        }
        catch (FileNotFoundException)
        {
            _map?.Dispose();
            _map = null;
        }
    }

    private bool TryReadState()
    {
        TryOpenMap();
        if (_view is null)
            return false;
        try
        {
            _view.ReadArray(0, _first, 0, StateBytes);
            Thread.MemoryBarrier();
            _view.ReadArray(0, _second, 0, StateBytes);
            return _first.AsSpan().SequenceEqual(_second);
        }
        catch (ObjectDisposedException)
        {
            _view = null;
            _map = null;
            return false;
        }
    }

    private static bool ReadSteamLinkSelection()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QproFaceTracking", "config", "tracking-source.txt");
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim().Equals(
                "steam-link", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void UpdateSteamLink()
    {
        if (_steamSource is null)
            return;
        _steamSource.Poll();
        long now = _tickClock();
        bool faceFresh = _steamSource.HasFreshFace(now, SteamFaceTimeoutMs);
        ReadOnlySpan<float> expressions = _steamSource.Expressions;
        bool upperFaceAvailable = faceFresh && _steamSource.HasAvailableUpperFace(now, SteamFaceTimeoutMs);
        bool lowerFaceAvailable = faceFresh && _steamSource.HasAvailableLowerFace(now, SteamFaceTimeoutMs);
        if (faceFresh)
            ReportSteamFaceAvailability(now, expressions, upperFaceAvailable, lowerFaceAvailable);
        Span<float> neutral = stackalloc float[ExpressionCount];
        neutral.Clear();
        ReadOnlySpan<float> upperValues = upperFaceAvailable ? expressions : neutral;
        ReadOnlySpan<float> lowerValues = lowerFaceAvailable ? expressions : neutral;
        if (lowerFaceAvailable)
            PublishCheekCalibrationSample(expressions, NativeFaceSource.SteamLink, now);
        if (_needsEye)
            UpdateSteamEyes(upperValues, now);
        if (_needsEye || _needsExpression)
            UpdateBrowExpressions(upperValues);
        if (_needsExpression)
            UpdateMouth(lowerValues, 3, NativeFaceSource.SteamLink, lowerFaceAvailable);
        if (faceFresh)
            PublishSteamLabels(now, expressions);
    }

    private void ReportSteamFaceAvailability(long now, ReadOnlySpan<float> expressions,
        bool upperFaceAvailable, bool lowerFaceAvailable)
    {
        if (_steamSource is null)
            return;
        bool bothCapabilitiesFresh = _steamSource.HasFreshUpperFaceCapability(now, SteamFaceTimeoutMs) &&
            _steamSource.HasFreshLowerFaceCapability(now, SteamFaceTimeoutMs);
        bool allWeightsZero = true;
        foreach (float weight in expressions)
        {
            if (weight == 0.0f) continue;
            allWeightsZero = false;
            break;
        }
        if (bothCapabilitiesFresh && !upperFaceAvailable && !lowerFaceAvailable &&
            allWeightsZero && _steamSource.HasFreshGaze(now, SteamFaceTimeoutMs))
        {
            if (now < _nextSteamFaceWarningTick) return;
            Logger.LogWarning(
                "Steam Link OSC gaze is live, but upper/lower face capability is OFF and all face weights are zero. " +
                "Enable eye and face tracking on the Quest Pro, then in SteamVR > Steam Link enable OSC and " +
                "'Share face tracking data to other apps on this PC via OSC' with output port 9015 (ALT). " +
                "Reconnect Steam Link if those settings were already enabled.");
            _steamFaceUnavailableReported = true;
            _nextSteamFaceWarningTick = now + 30_000;
        }
        else if (_steamFaceUnavailableReported && upperFaceAvailable && lowerFaceAvailable)
        {
            Logger.LogInformation("Steam Link upper and lower face capability is available again.");
            _steamFaceUnavailableReported = false;
            _nextSteamFaceWarningTick = 0;
        }
    }

    private void UpdateSteamEyes(ReadOnlySpan<float> values, long now)
    {
        float leftX = 0, leftY = 0, rightX = 0, rightY = 0;
        bool steamGazeFresh = _steamSource is not null &&
            _steamSource.HasFreshGaze(now, SteamFaceTimeoutMs) &&
            _steamSource.TryGetGaze(now, out leftX, out leftY,
                out rightX, out rightY);

        UpdateGazeOutput(now, left: true, steamGazeFresh, leftX, leftY);
        UpdateGazeOutput(now, left: false, steamGazeFresh, rightX, rightY);

        UnifiedTracking.Data.Eye.Left.Openness = 1.0f - Math.Clamp(
            values[12] + values[12] * values[28], 0.0f, 1.0f);
        UnifiedTracking.Data.Eye.Right.Openness = 1.0f - Math.Clamp(
            values[13] + values[13] * values[29], 0.0f, 1.0f);
        UpdatePupilOutput(now, nativeAvailable: true);
        UpdateEyeExpressions(values);
    }

    private void PublishSteamLabels(long now, ReadOnlySpan<float> expressions)
    {
        if (_steamLabelSocket is null || _steamSource is null)
            return;
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            if (qpc >= _nextSteamSchemaTick)
            {
                SendSteamLabel(new
                {
                    V = 1, Type = "schema", Names = SteamOscSource.ExpressionNames
                });
                _nextSteamSchemaTick = qpc + Stopwatch.Frequency * 2;
            }
            if (qpc < _nextSteamLabelTick)
                return;
            _nextSteamLabelTick = qpc + Stopwatch.Frequency / 60;
            if (_steamSource.LastFaceTick != _lastSteamFaceTick)
            {
                _lastSteamFaceTick = _steamSource.LastFaceTick;
                ++_steamSourceChangeSequence;
            }
            bool gazeValid = _steamSource.HasFreshGaze(now, SteamFaceTimeoutMs);
            SendSteamLabel(new
            {
                V = 1,
                Type = "sample",
                Sequence = ++_steamLabelSequence,
                Qpc = qpc,
                QpcFrequency = Stopwatch.Frequency,
                UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SourceChangeSequence = _steamSourceChangeSequence,
                SourceUnchangedMs = (double)(now - _steamSource.LastFaceTick),
                Values = expressions.ToArray(),
                // Bit 1 is Virtual Desktop's alternate tongue layout, which
                // Steam Link's named XR_FB weights do not use.
                FaceFlags = _steamSource.HasAvailableLowerFace(now, SteamFaceTimeoutMs) ? 1 : 0,
                IsEyeFollowingBlendshapesValid = _steamSource.HasAvailableUpperFace(now, SteamFaceTimeoutMs),
                LeftEyeIsValid = gazeValid,
                RightEyeIsValid = gazeValid,
                LeftEyeOrientation = new float[] { 0, 0, 0, 1 },
                RightEyeOrientation = new float[] { 0, 0, 0, 1 },
                LeftEyePosition = new float[] { 0, 0, 0 },
                RightEyePosition = new float[] { 0, 0, 0 },
                LeftEyeConfidence = gazeValid ? 1.0f : 0.0f,
                RightEyeConfidence = gazeValid ? 1.0f : 0.0f
            });
        }
        catch (SocketException)
        {
            // The label receiver is optional for ordinary live tracking.
        }
    }

    private void SendSteamLabel<T>(T message)
    {
        byte[] packet = JsonSerializer.SerializeToUtf8Bytes(message,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        _steamLabelSocket!.Send(packet, packet.Length);
    }

    private void ReceiveGaze()
    {
        if (_gazeSocket is null)
            return;
        try
        {
            foreach (byte[] packet in LocalDatagrams.ReadPending(_gazeSocket))
            {
                if (packet.Length != GazePacketBytes ||
                    packet[0] != (byte)'Q' || packet[1] != (byte)'P' ||
                    packet[2] != (byte)'G' || packet[3] != (byte)'E' ||
                    packet[4] != 1)
                    continue;
                _leftGazeX = ReadFloat(packet, 8);
                _leftGazeY = ReadFloat(packet, 12);
                _rightGazeX = ReadFloat(packet, 16);
                _rightGazeY = ReadFloat(packet, 20);
                long now = _tickClock();
                _leftGazeOverlay.Receive(now, (packet[5] & 1) != 0 &&
                    float.IsFinite(_leftGazeX) && float.IsFinite(_leftGazeY));
                _rightGazeOverlay.Receive(now, (packet[5] & 2) != 0 &&
                    float.IsFinite(_rightGazeX) && float.IsFinite(_rightGazeY));
            }
        }
        catch (SocketException error) when (
            error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
        }
    }

    private void ReceiveCameraCheeks()
    {
        if (!_cheekCameraTransportReady)
        {
            // A large queue may need several bounded drains. Do not retime
            // packets from an earlier inactive session as new camera output.
            _cheekCameraTransportReady = LocalDatagrams.DiscardPending(_cheekCameraSocket);
            return;
        }
        foreach (byte[] packet in LocalDatagrams.ReadPending(_cheekCameraSocket))
            if (_cheekSessionActive) _cheekCamera.Receive(packet, _tickClock());
    }

    private void UpdateCheekOutput(ReadOnlySpan<float> values, bool nativeAvailable)
    {
        CheekPuffWeights native = default;
        if (nativeAvailable)
        {
            if (_cheekSessionActive) RefreshCheekPuffMode();
            native = _cheekPuffTracker.Update(values,
                _cheekSessionActive ? _cheekPuffMode : CheekPuffMode.Off,
                _tickClock(), _cheekPuffCalibration);
            // Native passthrough is still a write owned by this module. Clear
            // it once if its source disappears, just like adjusted output.
            _cheekPuffNativeOutputApplied = true;
        }
        CheekPuffWeights? resolved = _cheekCamera.Resolve(_tickClock(), nativeAvailable, native);
        if (!nativeAvailable && _cheekPuffNativeOutputApplied)
        {
            resolved ??= new CheekPuffWeights(0, 0);
            _cheekPuffNativeOutputApplied = false;
        }
        if (resolved is not CheekPuffWeights cheeks) return;
        Set((int)UnifiedExpressions.CheekPuffLeft, cheeks.Left);
        Set((int)UnifiedExpressions.CheekPuffRight, cheeks.Right);
    }

    private void ReceiveTongue()
    {
        if (_tongueSocket is null)
            return;
        try
        {
            foreach (byte[] packet in LocalDatagrams.ReadPending(_tongueSocket))
            {
                if (packet.Length != TonguePacketBytes ||
                    packet[0] != (byte)'Q' || packet[1] != (byte)'P' ||
                    packet[2] != (byte)'T' || packet[3] != (byte)'O' ||
                    packet[4] != 1)
                    continue;
                bool valid = true;
                for (int index = 0; index < _tongueValues.Length; ++index)
                {
                    float value = ReadFloat(packet, 8 + index * 4);
                    valid &= float.IsFinite(value);
                    _tongueValues[index] = float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;
                }
                _tongueDirty = true;
                _tongueOverlay.Receive(_tickClock(), (packet[5] & 1) != 0 && valid);
            }
        }
        catch (SocketException error) when (
            error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
        }
    }

    private void ReceivePupil()
    {
        if (_pupilSocket is null)
            return;
        try
        {
            foreach (byte[] packet in LocalDatagrams.ReadPending(_pupilSocket))
            {
                if (packet.Length != PupilPacketBytes ||
                    packet[0] != (byte)'Q' || packet[1] != (byte)'P' ||
                    packet[2] != (byte)'D' || packet[3] != (byte)'I' ||
                    packet[4] != 1)
                    continue;
                float left = ReadFloat(packet, 8);
                float right = ReadFloat(packet, 12);
                long now = _tickClock();
                _leftPupilOverlay.Receive(now, (packet[5] & 1) != 0 &&
                    float.IsFinite(left) && left is >= 2.0f and <= 9.0f);
                _rightPupilOverlay.Receive(now, (packet[5] & 2) != 0 &&
                    float.IsFinite(right) && right is >= 2.0f and <= 9.0f);
                _leftPupilMm = left;
                _rightPupilMm = right;
            }
        }
        catch (SocketException error) when (
            error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
        }
    }

    private void UpdateEyes(ReadOnlySpan<float> values)
    {
        bool leftValid = _second[292] != 0;
        bool rightValid = _second[293] != 0;
        long now = _tickClock();
        (float leftX, float leftY) = leftValid ? QuaternionToCartesian(_second, 296) : default;
        (float rightX, float rightY) = rightValid ? QuaternionToCartesian(_second, 324) : default;
        UpdateGazeOutput(now, left: true, leftValid, leftX, leftY);
        UpdateGazeOutput(now, left: false, rightValid, rightX, rightY);

        // Blink drives closure. A smile can raise the cheeks and tighten the
        // lids without actually closing either eye.
        UnifiedTracking.Data.Eye.Left.Openness = 1.0f - Math.Clamp(
            values[12] + values[12] * values[28], 0.0f, 1.0f);
        UnifiedTracking.Data.Eye.Right.Openness = 1.0f - Math.Clamp(
            values[13] + values[13] * values[29], 0.0f, 1.0f);
        // VRCFT normalizes combined dilation using these limits. The relative
        // camera estimate eases from 2 to 8 with neutral at 5, so use the
        // same range to make VRChat's 0..1 animation respond visibly.
        UpdatePupilOutput(now, nativeAvailable: true);

        if (_second[1] != 0)
            UpdateEyeExpressions(values);
    }

    private static void UpdateEyeExpressions(ReadOnlySpan<float> values)
    {
        Set(1, values[28]); Set(0, values[29]);
        Set(3, values[59]); Set(2, values[60]);
    }

    private void UpdateBrowExpressions(ReadOnlySpan<float> values)
    {
        // Brow blendshapes are present even when VRCFT owns only the face
        // expression slot. Preserve Virtual Desktop's separate left/right
        // inner, outer, and lowering values in either module configuration.
        RefreshEyebrowSettings();
        EyebrowWeights brow = EyebrowMapping.FromFaceWeights(
            values, _eyebrowSettings.Enabled, _eyebrowSettings.Sensitivity);
        Set((int)UnifiedExpressions.BrowPinchLeft, brow.LowerLeft);
        Set((int)UnifiedExpressions.BrowLowererLeft, brow.LowerLeft);
        Set((int)UnifiedExpressions.BrowPinchRight, brow.LowerRight);
        Set((int)UnifiedExpressions.BrowLowererRight, brow.LowerRight);
        Set((int)UnifiedExpressions.BrowInnerUpLeft, brow.InnerLeft);
        Set((int)UnifiedExpressions.BrowInnerUpRight, brow.InnerRight);
        Set((int)UnifiedExpressions.BrowOuterUpLeft, brow.OuterLeft);
        Set((int)UnifiedExpressions.BrowOuterUpRight, brow.OuterRight);
    }

    private void UpdateMouth(ReadOnlySpan<float> values, byte faceFlags, NativeFaceSource source,
        bool nativeTongueAvailable = true)
    {
        // Both sources use the same XR_FB indices for direct face shapes.
        long frameTickMs = _tickClock();
        foreach ((int expressionIndex, int[] targets) in ExpressionMap)
            foreach (int target in targets)
                Set(target, values[expressionIndex]);

        SmirkWeights smile = _smirkTracker.Update(values, frameTickMs);
        Set((int)UnifiedExpressions.MouthCornerPullLeft, smile.Left);
        Set((int)UnifiedExpressions.MouthCornerSlantLeft, smile.Left);
        Set((int)UnifiedExpressions.MouthCornerPullRight, smile.Right);
        Set((int)UnifiedExpressions.MouthCornerSlantRight, smile.Right);

        // Both sources use the same left/right XR_FB cheek indices. The Hub
        // selects native passthrough, calibrated strength, balanced separation,
        // or a confirmed 1/0 pose.
        UpdateCheekOutput(values, nativeTongueAvailable);
        Set((int)UnifiedExpressions.CheekSquintLeft, values[4]);
        Set((int)UnifiedExpressions.CheekSquintRight, values[5]);
        UpdateCheekSuckOutput(values, nativeTongueAvailable);

        NativeLipWeights lips = NativeLipMapping.FromFaceWeights(values, source);
        Set((int)UnifiedExpressions.MouthUpperUpLeft, lips.UpperUpLeft);
        Set((int)UnifiedExpressions.MouthUpperDeepenLeft, lips.UpperDeepenLeft);
        Set((int)UnifiedExpressions.MouthUpperUpRight, lips.UpperUpRight);
        Set((int)UnifiedExpressions.MouthUpperDeepenRight, lips.UpperDeepenRight);
        Set((int)UnifiedExpressions.LipSuckUpperLeft, lips.SuckUpperLeft);
        Set((int)UnifiedExpressions.LipSuckUpperRight, lips.SuckUpperRight);

        UpdateTongueOutput(values, faceFlags, source, nativeTongueAvailable);
    }

    private void UpdateTongueOutput(ReadOnlySpan<float> values, byte faceFlags,
        NativeFaceSource source, bool nativeAvailable)
    {
        OverlayOutput output = _tongueOverlay.Resolve(_tickClock(), nativeAvailable);
        if (output == OverlayOutput.Custom)
        {
            // Do not rewrite unchanged tongue shapes at the module's ~200 Hz
            // face cadence. A new camera packet is the only thing that should
            // dirty these slots; this avoids flooding VRCFT/VRChat while the
            // rest of the face remains on its normal update path.
            if (_tongueDirty)
            {
                ApplyTongueOverride();
                _tongueDirty = false;
            }
        }
        else if (output == OverlayOutput.Native)
        {
            NativeTongueWeights nativeTongue = NativeTongueMapping.Resolve(
                values, source, faceFlags, customFresh: false, customEnabled: false)!.Value;
            Span<float> nativeSlots = stackalloc float[NativeTongueMapping.SlotCount];
            NativeTongueMapping.WriteSlots(nativeTongue, nativeSlots);
            SetTongueSlots(nativeSlots);
            _tongueDirty = false;
        }
        else if (output == OverlayOutput.Neutral)
        {
            Span<float> neutral = stackalloc float[NativeTongueMapping.SlotCount];
            neutral.Clear();
            SetTongueSlots(neutral);
            _tongueDirty = false;
        }
    }

    private void UpdateUnavailableSource()
    {
        if (_needsEye)
        {
            long now = _tickClock();
            UpdateGazeOutput(now, left: true, nativeAvailable: false, 0, 0);
            UpdateGazeOutput(now, left: false, nativeAvailable: false, 0, 0);
            UpdatePupilOutput(now, nativeAvailable: false);
        }
        if (_needsExpression)
        {
            UpdateTongueOutput(default, 0, NativeFaceSource.VirtualDesktop, nativeAvailable: false);
            UpdateCheekOutput(default, nativeAvailable: false);
            UpdateCheekSuckOutput(default, nativeAvailable: false);
        }
    }

    private void UpdateGazeOutput(long now, bool left, bool nativeAvailable,
        float nativeX, float nativeY)
    {
        LiveOverlayState overlay = left ? _leftGazeOverlay : _rightGazeOverlay;
        OverlayOutput output = overlay.Resolve(now,
            nativeAvailable && float.IsFinite(nativeX) && float.IsFinite(nativeY));
        if (output == OverlayOutput.Unchanged) return;
        float x = output switch
        {
            OverlayOutput.Custom => left ? _leftGazeX : _rightGazeX,
            OverlayOutput.Native => nativeX,
            _ => 0.0f,
        };
        float y = output switch
        {
            OverlayOutput.Custom => left ? _leftGazeY : _rightGazeY,
            OverlayOutput.Native => nativeY,
            _ => 0.0f,
        };
        // VRCFT stores each eye as a value type; a local copy would discard
        // the update and leave the previous gaze in place.
        if (left)
        {
            UnifiedTracking.Data.Eye.Left.Gaze.x = x;
            UnifiedTracking.Data.Eye.Left.Gaze.y = y;
        }
        else
        {
            UnifiedTracking.Data.Eye.Right.Gaze.x = x;
            UnifiedTracking.Data.Eye.Right.Gaze.y = y;
        }
    }

    private void UpdatePupilOutput(long now, bool nativeAvailable)
    {
        OverlayOutput left = _leftPupilOverlay.Resolve(now, nativeAvailable);
        OverlayOutput right = _rightPupilOverlay.Resolve(now, nativeAvailable);
        if (left != OverlayOutput.Unchanged)
            UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = left == OverlayOutput.Custom ? _leftPupilMm : 5.0f;
        if (right != OverlayOutput.Unchanged)
            UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = right == OverlayOutput.Custom ? _rightPupilMm : 5.0f;
        if (left != OverlayOutput.Unchanged || right != OverlayOutput.Unchanged)
        {
            UnifiedTracking.Data.Eye._minDilation = 2.0f;
            UnifiedTracking.Data.Eye._maxDilation = 8.0f;
        }
    }

    private bool DiscardInactivePackets()
    {
        bool drained = LocalDatagrams.DiscardPending(_gazeSocket);
        drained &= LocalDatagrams.DiscardPending(_tongueSocket);
        drained &= LocalDatagrams.DiscardPending(_pupilSocket);
        drained &= LocalDatagrams.DiscardPending(_cheekCameraSocket);
        drained &= _steamSource?.DiscardPending() ?? true;
        foreach (LiveOverlayState overlay in OverlayStates()) overlay.DiscardPackets();
        _tongueDirty = false;
        _cheekCamera.DiscardPackets();
        return drained;
    }

    private IEnumerable<LiveOverlayState> OverlayStates()
    {
        yield return _leftGazeOverlay;
        yield return _rightGazeOverlay;
        yield return _tongueOverlay;
        yield return _leftPupilOverlay;
        yield return _rightPupilOverlay;
    }

    private void ResetOverlayState()
    {
        foreach (LiveOverlayState overlay in OverlayStates()) overlay.Reset();
        _cheekCamera.Reset();
        _cheekPuffNativeOutputApplied = false;
        _cheekSuckOutputApplied = false;
        _cheekCameraTransportReady = true;
        _tongueDirty = false;
    }

    private void UpdateCheekSuckOutput(ReadOnlySpan<float> values, bool nativeAvailable)
    {
        if (!nativeAvailable)
        {
            if (!_cheekSuckOutputApplied) return;
            Set((int)UnifiedExpressions.CheekSuckLeft, 0);
            Set((int)UnifiedExpressions.CheekSuckRight, 0);
            _cheekSuckOutputApplied = false;
            return;
        }
        if (_cheekSessionActive) RefreshCheekSuckMode();
        CheekSuckWeights suck = _cheekSuckTracker.Update(values,
            _cheekSessionActive ? _cheekSuckMode : CheekSuckMode.Off, _tickClock());
        Set((int)UnifiedExpressions.CheekSuckLeft, suck.Left);
        Set((int)UnifiedExpressions.CheekSuckRight, suck.Right);
        _cheekSuckOutputApplied = true;
    }

    private void RefreshCheekSession()
    {
        long now = _tickClock();
        if (now < _nextCheekSessionCheckTick) return;
        _nextCheekSessionCheckTick = now + 250;
        bool active = _cheekSessionProbe();
        if (active == _cheekSessionActive) return;
        _cheekSessionActive = active;
        _cheekPuffTracker.Reset();
        _cheekSuckTracker.Reset();
        // Keep ownership of the last camera write until it is restored or
        // cleared, including when the native face feed is unavailable.
        _cheekCamera.DiscardPackets();
        _cheekCameraTransportReady = LocalDatagrams.DiscardPending(_cheekCameraSocket);
        _nextCheekPuffModeCheckTick = _nextCheekSuckModeCheckTick = 0;
        Logger.LogInformation(active
            ? "Companion tracking started: saved cheek adjustments enabled."
            : "Companion tracking stopped: native cheek values restored.");
    }

    private void RefreshCheekPuffMode()
    {
        long now = _tickClock();
        if (now < _nextCheekPuffModeCheckTick) return;
        _nextCheekPuffModeCheckTick = now + 250;
        CheekPuffMode selected;
        try
        {
            string saved = File.Exists(CheekPuffModePath)
                ? File.ReadAllText(CheekPuffModePath).Trim()
                : "strong";
            selected = saved.ToLowerInvariant() switch
            {
                "off" => CheekPuffMode.Off,
                "balanced" => CheekPuffMode.Balanced,
                "calibrated" => CheekPuffMode.Calibrated,
                _ => CheekPuffMode.Strong
            };
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        string source = _useSteamLink
            ? CheekPuffCalibrationProfile.SteamLinkSource
            : CheekPuffCalibrationProfile.VirtualDesktopSource;
        CheekPuffCalibration? calibration = selected == CheekPuffMode.Calibrated
            ? CheekPuffCalibrationProfile.Load(source) : null;
        string? origin = calibration.HasValue ? "personal profile" : null;
        if (selected == CheekPuffMode.Calibrated && !calibration.HasValue)
        {
            calibration = DeveloperCheekPuffBaseline.ForSource(source);
            origin = calibration.HasValue ? "developer baseline" : "balanced fallback; no profile";
        }
        if (_cheekPuffMode == selected && _cheekPuffCalibration == calibration &&
            _cheekPuffCalibrationOrigin == origin) return;
        _cheekPuffMode = selected;
        _cheekPuffCalibration = calibration;
        _cheekPuffCalibrationOrigin = origin;
        _cheekPuffTracker.Reset();
        Logger.LogInformation("Cheek puff style: {Style}", selected switch
        {
            CheekPuffMode.Off => "native passthrough",
            CheekPuffMode.Balanced => "balanced",
            CheekPuffMode.Calibrated => $"calibrated ({origin})",
            _ => "1/0 individual"
        });
    }

    private void PublishCheekCalibrationSample(ReadOnlySpan<float> values, NativeFaceSource source, long nowMs)
    {
        if (_cheekTelemetrySocket is null || nowMs < _nextCheekTelemetryTick) return;
        _nextCheekTelemetryTick = nowMs + 50;
        // Calibration always measures Balanced strengths, even while another
        // output mode is selected, so its anchors describe the same input that
        // Calibrated mode later normalizes.
        CheekPuffWeights cheeks = CheekPuffMapping.FromFaceWeights(values);
        byte sourceId = source == NativeFaceSource.SteamLink
            ? CheekPuffTelemetry.SourceSteamLink : CheekPuffTelemetry.SourceVirtualDesktop;
        byte[] packet = CheekPuffTelemetry.Create(sourceId, cheeks.Left, cheeks.Right);
        try
        {
            _cheekTelemetrySocket.Send(packet, packet.Length, CheekTelemetryEndpoint);
        }
        catch (SocketException)
        {
            // A closed calibration window must not interrupt face tracking.
            _nextCheekTelemetryTick = nowMs + 1000;
        }
    }

    private void RefreshCheekSuckMode()
    {
        long now = _tickClock();
        if (now < _nextCheekSuckModeCheckTick) return;
        _nextCheekSuckModeCheckTick = now + 250;
        CheekSuckMode selected;
        try
        {
            string saved = File.Exists(CheekSuckModePath)
                ? File.ReadAllText(CheekSuckModePath).Trim()
                : "strong";
            selected = saved.ToLowerInvariant() switch
            {
                "off" => CheekSuckMode.Off,
                "balanced" => CheekSuckMode.Balanced,
                _ => CheekSuckMode.Strong
            };
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (_cheekSuckMode == selected) return;
        _cheekSuckMode = selected;
        _cheekSuckTracker.Reset();
        Logger.LogInformation("Cheek suck style: {Style}", selected switch
        {
            CheekSuckMode.Off => "native passthrough",
            CheekSuckMode.Balanced => "balanced",
            _ => "strong individual"
        });
    }

    private void RefreshEyebrowSettings()
    {
        long now = _tickClock();
        if (now < _nextEyebrowSettingsCheckTick) return;
        _nextEyebrowSettingsCheckTick = now + 250;
        EyebrowSettings selected = EyebrowPreference.Load();
        if (_eyebrowSettings == selected) return;
        _eyebrowSettings = selected;
        Logger.LogInformation("Eyebrow adjustment: {Enabled}; sensitivity: {Sensitivity:F2}x",
            selected.Enabled, selected.Sensitivity);
    }

    private void ApplyTongueOverride()
    {
        SetTongueSlots(_tongueValues);
    }

    private static void SetTongueSlots(ReadOnlySpan<float> slots)
    {
        Debug.Assert(slots.Length == TongueExpressions.Length);
        for (int index = 0; index < TongueExpressions.Length; ++index)
            Set(TongueExpressions[index], slots[index]);
    }

    private static void Set(int expression, float value) =>
        UnifiedTracking.Data.Shapes[expression].Weight = value;

    private static float ReadFloat(byte[] bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));

    private static (float x, float y) QuaternionToCartesian(byte[] bytes, int offset)
    {
        float x = BitConverter.ToSingle(bytes, offset);
        float y = BitConverter.ToSingle(bytes, offset + 4);
        float z = BitConverter.ToSingle(bytes, offset + 8);
        float w = BitConverter.ToSingle(bytes, offset + 12);
        float length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (length <= 1e-6f)
            return (0.0f, 0.0f);
        x /= length; y /= length; z /= length; w /= length;
        float first = MathF.Asin(Math.Clamp(2.0f * (x * z - w * y), -1.0f, 1.0f));
        float second = MathF.Atan2(
            2.0f * (y * z + w * x),
            w * w - x * x - y * y + z * z);
        return (first, second);
    }
}
