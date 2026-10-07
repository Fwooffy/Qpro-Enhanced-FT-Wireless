using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qpro.GazeBridge;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

int checks = 0;
try
{
    RunChecks();
    Console.WriteLine($"PASS: {checks} live-overlay checks against the production module and installed VRCFT API.");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL after {checks} checks: {error}");
    Environment.ExitCode = 1;
}

void RunChecks()
{
    foreach (string nativeStyle in new[] { "Off", "Balanced", "Strong", "Calibrated" })
    {
        using var f = new Fixture();
        f.ConfigureCheekStyle(nativeStyle);
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true,
            leftCheek: .62f, rightCheek: .65f);
        // A fresh camera model supplies continuous, independently trained
        // cheek strengths. Native cheek calibration must not map those a
        // second time or turn a unilateral camera pose into a bilateral one.
        foreach ((float left, float right) in new[]
        {
            (.82f, 0f), (.35f, 0f), (0f, .74f), (0f, .23f),
            (.9f, .85f), (.4f, .3f), (0f, 0f), (.15f, .03f)
        })
        {
            f.SendCameraCheeks(left, right);
            f.Module.Update();
            Near(Tongue(UnifiedExpressions.CheekPuffLeft), left,
                $"camera left remains exact through {nativeStyle} native style");
            Near(Tongue(UnifiedExpressions.CheekPuffRight), right,
                $"camera right remains exact through {nativeStyle} native style");
            Near(Tongue(UnifiedExpressions.TongueOut), .85f,
                "changing camera cheek sides leaves native tongue active");
            f.Time += 50;
        }
    }

    foreach (bool missingMap in new[] { false, true })
    {
        using var f = new Fixture();
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true);
        f.SendCameraCheeks(.75f, .1f);
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.CheekPuffLeft), .75f, "fresh camera left cheek reaches production output");
        Near(Tongue(UnifiedExpressions.CheekPuffRight), .1f, "fresh camera right cheek stays independent");
        Near(Tongue(UnifiedExpressions.TongueOut), .85f, "camera cheeks do not replace native tongue");
        if (missingMap) f.LoseMap();
        else f.Native(faceFlags: 0, eyeFollowing: true, eyesValid: false);
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.CheekPuffLeft), .75f, "fresh camera cheeks survive an unavailable native face source");
        f.Time += 501;
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.CheekPuffLeft), 0f, "expired camera cheeks clear without a native source");
        Near(Tongue(UnifiedExpressions.CheekPuffRight), 0f, "expired camera cheeks clear both sides");
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.CheekPuffLeft].Weight = .37f;
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.CheekPuffLeft), .37f, "released camera cheek slots are not repeatedly overwritten");
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true);
        f.SendCameraCheeks(.9f, .2f);
        f.Module.Update();
        f.SendCameraCheeks(0, 0, enabled: false);
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.CheekPuffLeft), 0f, "camera stop restores native cheek style immediately");
        Near(Tongue(UnifiedExpressions.CheekPuffRight), 0f, "camera stop restores the other native cheek");
    }

    // Named test maps and ephemeral loopback ports keep these checks separate
    // from an installed module, Virtual Desktop, or a connected headset.
    using (var f = new Fixture())
    {
        f.Native(faceFlags: 3, eyeFollowing: false, eyesValid: true);
        f.Module.Update();
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .2f, "native left ray preserved");
        Near(UnifiedTracking.Data.Eye.Right.Gaze.x, -.3f, "native right ray stays independent");
        Near(Tongue(UnifiedExpressions.TongueOut), .25f, "VD alternate tongue layout uses FaceFlags byte zero");
        Near(Tongue(UnifiedExpressions.TongueLeft), .4f, "alternate layout keeps native tongue direction");
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true);
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .85f, "VD standard tongue layout stays native");
        Near(Tongue(UnifiedExpressions.TongueLeft), 0f, "standard layout clears previous direction");
    }

    foreach (bool missingMap in new[] { false, true })
    {
        using var f = new Fixture();
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true);
        f.SendAll();
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .7f, "fresh camera tongue applies");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .65f, "fresh left custom gaze applies");
        Near(UnifiedTracking.Data.Eye.Right.Gaze.x, -.55f, "fresh right custom gaze applies");
        Near(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM, 6.2f, "fresh pupil applies");
        if (missingMap) f.LoseMap();
        else f.Native(faceFlags: 0, eyeFollowing: true, eyesValid: false);
        f.Time += 351;
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), 0f, $"expired tongue clears with missingMap={missingMap}");
        Near(Tongue(UnifiedExpressions.TongueRoll), 0f, "expired tongue clears camera-only shapes");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, 0f, "expired left overlay clears without native gaze");
        Near(UnifiedTracking.Data.Eye.Right.Gaze.x, 0f, "expired right overlay clears without native gaze");
        Near(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM, 5f, "expired pupil returns to neutral");
        if (missingMap)
        {
            SetSentinels();
            f.Module.Update();
            CheckSentinels("already released overlays do not repeatedly overwrite slots");
        }
        f.Native(faceFlags: 3, eyeFollowing: false, eyesValid: true);
        f.Module.Update();
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .2f, "native gaze recovers after source loss");
        Near(Tongue(UnifiedExpressions.TongueOut), .25f, "native tongue recovers after source loss");
    }

    foreach (bool missingMap in new[] { false, true })
    {
        using var f = new Fixture();
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true);
        f.SendAll();
        f.Module.Update();
        if (missingMap) f.LoseMap();
        else f.Native(faceFlags: 0, eyeFollowing: true, eyesValid: false);
        f.SendAll(enabled: false);
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), 0f, "disabled tongue clears immediately without source");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, 0f, "disabled gaze clears immediately without source");
        Near(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM, 5f, "disabled pupil clears immediately without source");
    }

    using (var f = new Fixture())
    {
        f.Native(faceFlags: 3, eyeFollowing: true, eyesValid: true);
        f.SendAll();
        f.Module.Update();
        f.SendAll(enabled: false);
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .25f, "stop packet selects valid native tongue immediately");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .2f, "stop packet selects exact native left gaze");
        Near(UnifiedTracking.Data.Eye.Right.Gaze.x, -.3f, "stop packet selects exact native right gaze");
        f.SendAll();
        f.Module.Update();
        f.Time += 351;
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .25f, "expiry selects valid native tongue");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .2f, "expiry selects valid native gaze");
    }

    using (var f = new Fixture())
    {
        f.Native(faceFlags: 1, eyeFollowing: true, eyesValid: true);
        f.SendGaze(.65f, .1f, float.NaN, float.PositiveInfinity);
        f.Module.Update();
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .65f, "finite left packet remains independent");
        Near(UnifiedTracking.Data.Eye.Right.Gaze.x, -.3f, "nonfinite right packet uses native right eye");
        f.SendGaze(float.NaN, .1f, -.55f, -.1f);
        f.Module.Update();
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .2f, "nonfinite left packet returns to native left eye");
        Near(UnifiedTracking.Data.Eye.Right.Gaze.x, -.55f, "finite right packet remains valid");
        f.Native(faceFlags: 0, eyeFollowing: false, eyesValid: false);
        f.SendTongue(valid: false);
        f.Module.Update();
        Check(float.IsFinite(Tongue(UnifiedExpressions.TongueOut)), "nonfinite tongue packet never writes NaN");
    }

    using (var f = new Fixture())
    {
        f.LoseMap();
        SetSentinels();
        f.Module.Update();
        CheckSentinels("a module without an applied overlay leaves unowned slots untouched");
        f.SetSlotOwnership(eyes: false, mouth: false);
        f.SendAll();
        f.Module.Update();
        f.Time += 500;
        f.Module.Update();
        CheckSentinels("unavailable VRCFT eye and expression slots are never written");
    }

    using (var f = new Fixture())
    {
        f.Native(faceFlags: 3, eyeFollowing: true, eyesValid: true);
        f.Module.Status = ModuleState.Idle;
        SetSentinels();
        f.SendAll();
        f.Module.Update();
        CheckSentinels("inactive module leaves other module output untouched");
        f.SendAll(); // Queued after the inactive Update: must be discarded on resume.
        f.Module.Status = ModuleState.Active;
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .25f, "queued inactive tongue cannot become fresh on resume");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .2f, "queued inactive gaze cannot become fresh on resume");
        f.SendAll();
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .7f, "new active packet may acquire tongue ownership");
    }

    using (var f = new Fixture())
    {
        f.Native(faceFlags: 3, eyeFollowing: true, eyesValid: true);
        f.SendMalformedThenValid();
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .7f, "short and empty datagrams do not block valid tongue");
        Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .65f, "short and empty datagrams do not block valid gaze");
        Near(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM, 6.2f, "short and empty datagrams do not block valid pupil");
    }

    using (var f = new Fixture())
    {
        f.Native(faceFlags: 3, eyeFollowing: true, eyesValid: true);
        f.Module.Status = ModuleState.Idle;
        f.Module.Update();
        for (int i = 0; i < 300; i++) f.SendTongue();
        f.Module.Status = ModuleState.Active;
        f.Module.Update();
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .25f, "a multi-update drain never replays the remainder as fresh");
        f.SendTongue();
        f.Module.Update();
        Near(Tongue(UnifiedExpressions.TongueOut), .7f, "active packets work after a long queued backlog drains");
    }
}

float Tongue(UnifiedExpressions expression) => UnifiedTracking.Data.Shapes[(int)expression].Weight;
void Near(float actual, float expected, string label) =>
    Check(float.IsFinite(actual) && Math.Abs(actual - expected) < .0001f, $"{label}: expected {expected}, got {actual}");
void Check(bool ok, string label)
{
    checks++;
    if (!ok) throw new InvalidOperationException(label);
}
void SetSentinels()
{
    UnifiedTracking.Data.Eye.Left.Gaze.x = .123f;
    UnifiedTracking.Data.Eye.Right.Gaze.x = .234f;
    UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 4.321f;
    UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.TongueOut].Weight = .345f;
}
void CheckSentinels(string label)
{
    Near(UnifiedTracking.Data.Eye.Left.Gaze.x, .123f, label + ": left gaze");
    Near(UnifiedTracking.Data.Eye.Right.Gaze.x, .234f, label + ": right gaze");
    Near(UnifiedTracking.Data.Eye.Left.PupilDiameter_MM, 4.321f, label + ": pupil");
    Near(Tongue(UnifiedExpressions.TongueOut), .345f, label + ": tongue");
}

sealed class Fixture : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly UdpClient _sender = new(AddressFamily.InterNetwork);
    private readonly UdpClient _gaze = Socket();
    private readonly UdpClient _tongue = Socket();
    private readonly UdpClient _pupil = Socket();
    private readonly UdpClient _cameraCheeks = Socket();
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    internal long Time = 1000;
    internal TrackingModule Module { get; }

    internal Fixture()
    {
        Module = (TrackingModule)Activator.CreateInstance(typeof(TrackingModule), Private,
            binder: null, args: [new Func<long>(() => Time), "QproOverlayTest." + Guid.NewGuid().ToString("N")], culture: null)!;
        for (Type? type = Module.GetType(); type is not null; type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (typeof(ILogger).IsAssignableFrom(field.FieldType)) field.SetValue(Module, NullLogger.Instance);
            foreach (PropertyInfo property in type.GetProperties(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (property.CanWrite && typeof(ILogger).IsAssignableFrom(property.PropertyType))
                    property.SetValue(Module, NullLogger.Instance);
        }
        Set("_gazeSocket", _gaze); Set("_tongueSocket", _tongue); Set("_pupilSocket", _pupil);
        Set("_cheekCameraSocket", _cameraCheeks);
        Set("_cheekSessionActive", true);
        Set("_nextCheekSessionCheckTick", long.MaxValue);
        Set("_wasActive", true);
        SetSlotOwnership(eyes: true, mouth: true);
        Module.Status = ModuleState.Active;
        // Drain tests use a larger receiver buffer to preserve their complete backlog.
        _tongue.Client.ReceiveBufferSize = 1024 * 1024;
    }

    internal void SetSlotOwnership(bool eyes, bool mouth)
    {
        Set("_needsEye", eyes); Set("_needsExpression", mouth);
    }

    internal void ConfigureCheekStyle(string style)
    {
        Assembly assembly = typeof(TrackingModule).Assembly;
        Type mode = assembly.GetType("Qpro.GazeBridge.CheekPuffMode")!;
        Type calibration = assembly.GetType("Qpro.Shared.CheekPuffCalibration")!;
        Set("_cheekPuffMode", Enum.Parse(mode, style));
        Set("_cheekPuffCalibration", Activator.CreateInstance(calibration,
            [.03f, .4f, .02f, .5f]));
        // Keep an offline fixture from reading or changing the user's saved
        // Hub preferences while exercising each native mapping style.
        Set("_nextCheekPuffModeCheckTick", long.MaxValue);
    }

    internal void Native(byte faceFlags, bool eyeFollowing, bool eyesValid,
        float leftCheek = 0f, float rightCheek = 0f)
    {
        if (_view is null)
        {
            _map = MemoryMappedFile.CreateNew(null, 360);
            _view = _map.CreateViewAccessor();
            Set("_map", _map); Set("_view", _view);
        }
        byte[] state = new byte[360];
        state[0] = faceFlags;
        state[1] = (byte)(eyeFollowing ? 1 : 0);
        state[292] = state[293] = (byte)(eyesValid ? 1 : 0);
        WriteFloat(state, 4 + 2 * 4, leftCheek);
        WriteFloat(state, 4 + 3 * 4, rightCheek);
        WriteFloat(state, 4 + 63 * 4, .25f);
        WriteFloat(state, 4 + 64 * 4, .4f);
        WriteFloat(state, 4 + 68 * 4, .85f);
        WriteFloat(state, 296 + 4, -MathF.Sin(.1f));
        WriteFloat(state, 296 + 12, MathF.Cos(.1f));
        WriteFloat(state, 324 + 4, MathF.Sin(.15f));
        WriteFloat(state, 324 + 12, MathF.Cos(.15f));
        _view.WriteArray(0, state, 0, state.Length);
    }

    internal void LoseMap()
    {
        _view?.Dispose(); _map?.Dispose();
        _view = null; _map = null;
        Set("_map", null); Set("_view", null);
    }

    internal void SendAll(bool enabled = true)
    {
        SendGaze(.65f, .1f, -.55f, -.1f, enabled ? (byte)3 : (byte)0);
        SendTongue(enabled);
        SendPupil(enabled);
    }

    internal void SendGaze(float leftX, float leftY, float rightX, float rightY, byte flags = 3)
    {
        byte[] packet = Packet("QPGE", 24, flags);
        WriteFloat(packet, 8, leftX); WriteFloat(packet, 12, leftY);
        WriteFloat(packet, 16, rightX); WriteFloat(packet, 20, rightY);
        Send(_gaze, packet);
    }

    internal void SendTongue(bool enabled = true, bool valid = true)
    {
        byte[] packet = Packet("QPTO", 56, enabled ? (byte)1 : (byte)0);
        for (int i = 0; i < 12; i++) WriteFloat(packet, 8 + i * 4, .7f);
        if (!valid) WriteFloat(packet, 8, float.NaN);
        Send(_tongue, packet);
    }

    internal void SendPupil(bool enabled = true)
    {
        byte[] packet = Packet("QPDI", 16, enabled ? (byte)3 : (byte)0);
        WriteFloat(packet, 8, 6.2f); WriteFloat(packet, 12, 6.4f);
        Send(_pupil, packet);
    }

    internal void SendCameraCheeks(float left, float right, bool enabled = true)
    {
        byte[] packet = Packet("QPCO", 16, enabled ? (byte)1 : (byte)0);
        WriteFloat(packet, 8, left); WriteFloat(packet, 12, right);
        Send(_cameraCheeks, packet);
    }

    internal void SendMalformedThenValid()
    {
        foreach (UdpClient target in new[] { _gaze, _tongue, _pupil })
        {
            Send(target, []);
            Send(target, [1]);
        }
        SendAll();
    }

    private void Send(UdpClient target, byte[] packet) =>
        _sender.Send(packet, packet.Length, (IPEndPoint)target.Client.LocalEndPoint!);
    private void Set(string field, object? value) =>
        typeof(TrackingModule).GetField(field, Private)!.SetValue(Module, value);
    private static UdpClient Socket()
    {
        var result = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        result.Client.Blocking = false;
        return result;
    }
    private static byte[] Packet(string magic, int length, byte flags)
    {
        byte[] result = new byte[length];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(result, 0);
        result[4] = 1; result[5] = flags;
        return result;
    }
    private static void WriteFloat(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
    public void Dispose()
    {
        Module.Teardown();
        _sender.Dispose();
    }
}
