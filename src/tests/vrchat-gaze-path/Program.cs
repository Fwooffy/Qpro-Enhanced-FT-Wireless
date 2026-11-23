using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using QproFaceTracking.Hub;

const string legacyPrefix = "/avatar/parameters/v2/";
const string pitchYawAddress = "/tracking/eye/LeftRightPitchYaw";
const string vectorAddress = "/tracking/eye/LeftRightVec";
const string centerVectorAddress = "/tracking/eye/CenterVec";
const string blinkAddress = "/tracking/eye/EyesClosedAmount";
const string faceAddress = "/tracking/face/v1";
int checks = 0;

CheckSteam();
CheckLeftCameraEye();
if (OperatingSystem.IsWindows())
    CheckVirtualDesktop();
Console.WriteLine($"VRChat source-to-gaze path passed {checks} checks.");

void CheckSteam()
{
    int steamPort;
    using (var port = Listen())
        steamPort = ((IPEndPoint)port.Client.LocalEndPoint!).Port;

    using var steam = new VrchatFaceSource(true, "unused", new(steamPort, 0, 0, 0, null),
        () => Environment.TickCount64, loadPreferences: false, enableGaze: false, enableTongue: false, enablePupils: false);
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    var destination = new IPEndPoint(IPAddress.Loopback, steamPort);

    foreach (var point in new[] { new Vector3(.45f, .32f, -1), new Vector3(-.37f, -.26f, -.8f), new Vector3(0, 0, -1) })
    {
        byte[] packet = FloatPacket("/sl/eyeTrackedGazePoint", point.X, point.Y, point.Z);
        sender.Send(packet, packet.Length, destination);
        var raw = new Vector2(MathF.Atan2(point.X, -point.Z), MathF.Atan2(point.Y, -point.Z));
        var direction = Vector3.Normalize(new Vector3(point.X, point.Y, -point.Z));
        var result = WaitForSteamFrame(steam, raw);
        CheckRoutes(result, raw, raw, direction, direction);
    }
}

void CheckLeftCameraEye()
{
    int gazePort;
    using (var port = Listen())
        gazePort = ((IPEndPoint)port.Client.LocalEndPoint!).Port;
    using var source = new VrchatFaceSource(true, "unused", new(0, gazePort, 0, 0, null),
        () => Environment.TickCount64, loadPreferences: false, enableTongue: false, enablePupils: false);
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    const float yaw = .27f, pitch = -.21f;
    var packet = new byte[24];
    "QPGE"u8.CopyTo(packet);
    packet[4] = packet[5] = 1; // Only physical left is valid.
    BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), yaw);
    BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), pitch);
    BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16), -.83f);
    sender.Send(packet, new IPEndPoint(IPAddress.Loopback, gazePort));
    var wait = Stopwatch.StartNew();
    var frame = source.Read();
    while (!frame.ContainsKey("EyeLeftDirectionX") && wait.Elapsed < TimeSpan.FromSeconds(1))
    {
        Thread.Sleep(1);
        frame = source.Read();
    }

    using var receiver = Listen();
    var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
    var blocked = new HashSet<string> { blinkAddress };
    var legacy = new Dictionary<string, string> { [legacyPrefix + "EyeLeftX"] = "f", [legacyPrefix + "EyeRightX"] = "f" };
    using (var output = new VrchatFaceOutput(endpoint, legacy, blockedInputs: blocked))
    {
        output.Send(frame);
        var received = Receive(receiver);
        Check(received.Address == legacyPrefix + "EyeRightX", "A single physical left camera eye drives only the historical right avatar lane.");
        CheckFloats(received, yaw);
        Check(receiver.Available == 0, "Missing camera eye produces no fabricated legacy counterpart.");
    }

    Vector3 left = new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
    using (var output = new VrchatFaceOutput(endpoint, new Dictionary<string, string> { [vectorAddress] = "ffffff" }, blockedInputs: blocked))
    {
        output.Send(frame);
        var received = Receive(receiver);
        Check(received.Address == vectorAddress, "The standard pair endpoint uses the single anatomic direction for both eyes.");
        CheckFloats(received, left.X, left.Y, left.Z, left.X, left.Y, left.Z);
    }
    using (var output = new VrchatFaceOutput(endpoint, legacy, nativeFace: true))
    {
        output.Send(frame);
        var received = Receive(receiver);
        byte[] expected = [0, 0, 0, 0, Weight(-left.X), Weight(left.X), Weight(left.Y), Weight(-left.Y)];
        Check(received.Address == faceAddress && received.Arguments.AsSpan(4, 8).SequenceEqual(expected),
            "Native face v1 retains only the physical left camera eye.");
    }
}

[SupportedOSPlatform("windows")]
void CheckVirtualDesktop()
{
    string name = "Qpro.VrchatGazePath." + Guid.NewGuid();
    using var map = MemoryMappedFile.CreateNew(name, 360);
    using var view = map.CreateViewAccessor();
    using var vd = new VrchatFaceSource(false, name, new(0, 0, 0, 0, null),
        () => Environment.TickCount64, loadPreferences: false, enableGaze: false, enableTongue: false, enablePupils: false);

    var primary = Quaternion.CreateFromYawPitchRoll(-.48f, .32f, .14f);
    var secondary = Quaternion.CreateFromYawPitchRoll(.36f, -.29f, -.17f);
    foreach (var (left, right) in new[] { (primary, secondary), (secondary, primary), (Quaternion.Identity, Quaternion.Identity) })
    {
        var frame = new byte[360];
        frame[292] = frame[293] = 1;
        WriteQuaternion(frame.AsSpan(296), left);
        WriteQuaternion(frame.AsSpan(324), right);
        view.WriteArray(0, frame, 0, frame.Length);

        // The stock VD bridge applies the inverse eye orientation to forward.
        var leftLegacy = Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(left));
        var rightLegacy = Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(right));
        CheckRoutes(vd.Read(), LegacyVdAngles(leftLegacy), LegacyVdAngles(rightLegacy),
            PhysicalEyeRay(left), PhysicalEyeRay(right));
    }
}

void CheckRoutes(IReadOnlyDictionary<string, float> frame, Vector2 legacyLeft, Vector2 legacyRight,
    Vector3 left, Vector3 right)
{
    using var receiver = Listen();
    var destination = (IPEndPoint)receiver.Client.LocalEndPoint!;
    var legacyInputs = new Dictionary<string, string>
    {
        [legacyPrefix + "EyeLeftX"] = "f", [legacyPrefix + "EyeLeftY"] = "f",
        [legacyPrefix + "EyeRightX"] = "f", [legacyPrefix + "EyeRightY"] = "f",
    };
    var expectedLegacy = new Dictionary<string, float>
    {
        [legacyPrefix + "EyeLeftX"] = legacyLeft.X, [legacyPrefix + "EyeLeftY"] = legacyLeft.Y,
        [legacyPrefix + "EyeRightX"] = legacyRight.X, [legacyPrefix + "EyeRightY"] = legacyRight.Y,
    };
    var noBlink = new HashSet<string>(StringComparer.Ordinal) { blinkAddress };
    using (var legacy = new VrchatFaceOutput(destination, legacyInputs, blockedInputs: noBlink))
    {
        legacy.Send(frame);
        for (int i = 0; i < 4; i++)
        {
            var packet = Receive(receiver);
            Check(expectedLegacy.Remove(packet.Address, out float expected), "Receive only the four negotiated legacy gaze addresses.");
            CheckFloats(packet, expected);
        }
    }

    var defaults = new Dictionary<string, string>();
    using (var builtin = new VrchatFaceOutput(destination, defaults, blockedInputs: noBlink))
    {
        builtin.Send(frame);
        var packet = Receive(receiver);
        Check(packet.Address == pitchYawAddress, "Use documented built-in per-eye angles for a verified service without gaze parameters.");
        CheckFloats(packet, PitchDownDegrees(left), YawRightDegrees(left), PitchDownDegrees(right), YawRightDegrees(right));
    }

    using (var builtin = new VrchatFaceOutput(destination, new Dictionary<string, string> { [vectorAddress] = "ffffff" }, blockedInputs: noBlink))
    {
        builtin.Send(frame);
        var packet = Receive(receiver);
        Check(packet.Address == vectorAddress, "Prefer the advertised vector endpoint over the documented angle fallback.");
        CheckFloats(packet, left.X, left.Y, left.Z, right.X, right.Y, right.Z);
    }

    using (var builtin = new VrchatFaceOutput(destination, new Dictionary<string, string> { [centerVectorAddress] = "fff" }, blockedInputs: noBlink))
    {
        builtin.Send(frame);
        var packet = Receive(receiver);
        Check(packet.Address == centerVectorAddress, "Preserve the standard center vector OSC endpoint.");
        var center = Vector3.Normalize(left + right);
        CheckFloats(packet, center.X, center.Y, center.Z);
    }

    using (var face = new VrchatFaceOutput(destination, legacyInputs, nativeFace: true))
    {
        face.Send(frame);
        var packet = Receive(receiver);
        Check(packet.Address == faceAddress && packet.Tags == ",b"
            && BinaryPrimitives.ReadInt32BigEndian(packet.Arguments) == 102, "Send native face tracking as a complete blob.");
        byte[] expected =
        [
            Weight(right.X), Weight(-right.X), Weight(right.Y), Weight(-right.Y),
            Weight(-left.X), Weight(left.X), Weight(left.Y), Weight(-left.Y),
        ];
        Check(packet.Arguments.AsSpan(4, 8).SequenceEqual(expected),
            "Quantize right/left inward, outward, upward, and downward strengths from the normalized directions.");
    }
}

IReadOnlyDictionary<string, float> WaitForSteamFrame(VrchatFaceSource source, Vector2 expected)
{
    var wait = Stopwatch.StartNew();
    while (wait.Elapsed < TimeSpan.FromSeconds(2))
    {
        var sample = source.Read();
        if (sample.TryGetValue("EyeLeftX", out float yaw) && sample.TryGetValue("EyeLeftY", out float pitch)
            && MathF.Abs(yaw - expected.X) <= .00001f && MathF.Abs(pitch - expected.Y) <= .00001f)
            return sample;
        Thread.Sleep(5);
    }
    throw new InvalidOperationException("Missing Steam Link gaze sample.");
}

void CheckFloats((string Address, string Tags, byte[] Arguments) packet, params float[] values)
{
    Check(packet.Tags == "," + new string('f', values.Length) && packet.Arguments.Length == sizeof(float) * values.Length,
        $"Receive {values.Length} OSC float arguments for {packet.Address}.");
    for (int i = 0; i < values.Length; i++)
    {
        float actual = BinaryPrimitives.ReadSingleBigEndian(packet.Arguments.AsSpan(i * sizeof(float)));
        Check(MathF.Abs(actual - values[i]) < .0001f, $"{packet.Address} argument {i + 1} expected {values[i]} and got {actual}.");
    }
}

void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
    checks++;
}

static Vector2 LegacyVdAngles(Vector3 direction) => new(MathF.Asin(direction.X), MathF.Atan2(direction.Y, direction.Z));
static Vector3 PhysicalEyeRay(Quaternion orientation)
{
    Vector3 projected = Vector3.Transform(-Vector3.UnitZ, orientation);
    return new(projected.X, projected.Y, -projected.Z);
}
static float PitchDownDegrees(Vector3 direction) => -MathF.Asin(direction.Y) * 180 / MathF.PI;
static float YawRightDegrees(Vector3 direction) => MathF.Atan2(direction.X, direction.Z) * 180 / MathF.PI;
static byte Weight(float value) => (byte)(MathF.Max(0, value) * 255);

static UdpClient Listen()
{
    var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    socket.Client.ReceiveTimeout = 1000;
    return socket;
}

static byte[] FloatPacket(string address, params float[] values)
{
    int tagOffset = Pad(address.Length + 1);
    int valueOffset = tagOffset + Pad(values.Length + 2);
    var packet = new byte[valueOffset + sizeof(float) * values.Length];
    Encoding.ASCII.GetBytes(address, packet);
    Encoding.ASCII.GetBytes("," + new string('f', values.Length), packet.AsSpan(tagOffset));
    for (int i = 0; i < values.Length; i++)
        BinaryPrimitives.WriteSingleBigEndian(packet.AsSpan(valueOffset + i * sizeof(float)), values[i]);
    return packet;
}

static (string Address, string Tags, byte[] Arguments) Receive(UdpClient receiver)
{
    IPEndPoint? sender = null;
    byte[] packet = receiver.Receive(ref sender);
    int tagOffset = Pad(Array.IndexOf(packet, (byte)0) + 1);
    string address = Encoding.ASCII.GetString(packet, 0, Array.IndexOf(packet, (byte)0));
    int tagLength = Array.IndexOf(packet, (byte)0, tagOffset) - tagOffset;
    string tags = Encoding.ASCII.GetString(packet, tagOffset, tagLength);
    return (address, tags, packet[(tagOffset + Pad(tagLength + 1))..]);
}

static int Pad(int length) => (length + 3) & ~3;

static void WriteQuaternion(Span<byte> target, Quaternion rotation)
{
    BinaryPrimitives.WriteSingleLittleEndian(target, rotation.X);
    BinaryPrimitives.WriteSingleLittleEndian(target[4..], rotation.Y);
    BinaryPrimitives.WriteSingleLittleEndian(target[8..], rotation.Z);
    BinaryPrimitives.WriteSingleLittleEndian(target[12..], rotation.W);
}
