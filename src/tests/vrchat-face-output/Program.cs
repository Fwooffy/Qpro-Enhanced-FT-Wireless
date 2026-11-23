using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QproFaceTracking.Hub;

using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
receiver.Client.ReceiveTimeout = 2000;
var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
var clock = new ManualClock();

const string avatar = "/avatar/parameters/";
const string eye = "/tracking/eye/";
var inputs = new Dictionary<string, string>
{
    [avatar + "FT/Nested/v2/JawX"] = "f",
    [avatar + "JawOpen"] = "Float",
    [avatar + "v2/JawLeft"] = "i",
    [avatar + "FT/v2/EyeLidLeft"] = "f",
    [avatar + "FT/v2/CheekPuffSuckLeft1"] = "T",
    [avatar + "FT/v2/CheekPuffSuckLeft2"] = "F",
    [avatar + "FT/v2/CheekPuffSuckLeft4"] = "T",
    [avatar + "FT/v2/CheekPuffSuckLeftNegative"] = "Bool",
    [avatar + "ExpressionTrackingActive"] = "T",
    [avatar + "FT/v2/EyeTrackingActive"] = "T",
    [avatar + "UserEyeTracking"] = "T",
    [avatar + "Costume/JawOpen"] = "f",
    [avatar + "*/v2/JawOpen"] = "f",
    [avatar + "CostumeToggle"] = "T",
    [avatar + "v2/JawOpen3"] = "T",
    [avatar + "v2/JawOpen4"] = "f",
    [avatar + "v3/JawOpen"] = "f",
    [eye + "EyesClosedAmount"] = "f",
    [eye + "LeftRightPitchYaw"] = "ffff",
    [eye + "CenterPitchYaw"] = "ff",
};

using (var output = new VrchatFaceOutput(endpoint, inputs, time: clock))
{
    Check(output.SupportedInputCount == 9, "Only recognized bindings and one gaze fallback are counted");
    output.Reset();
    output.Send(new Dictionary<string, float> { ["CostumeToggle"] = 1, ["JawOpen"] = float.NaN });
    Check(receiver.Available == 0, "No packets are sent before tracking owns a valid channel");

    var values = new Dictionary<string, float>
    {
        ["JawLeft"] = .75f, ["JawRight"] = .1f, ["JawOpen"] = 1.1f,
        ["CheekPuffLeft"] = .1f, ["CheekSuckLeft"] = .8f,
        ["EyeLeftOpenness"] = .6f, ["EyeWideLeft"] = .4f,
        ["EyeLeftDirectionX"] = 1 / MathF.Sqrt(69), ["EyeLeftDirectionY"] = 2 / MathF.Sqrt(69), ["EyeLeftDirectionZ"] = 8 / MathF.Sqrt(69),
        ["EyeRightDirectionX"] = -3 / MathF.Sqrt(89), ["EyeRightDirectionY"] = -4 / MathF.Sqrt(89), ["EyeRightDirectionZ"] = 8 / MathF.Sqrt(89),
    };
    output.Send(values);
    var initial = Receive(11);
    Check(output.HasActiveOutput && !output.EyeOnly, "Successful avatar writes report active expression output");
    Near(initial[avatar + "FT/Nested/v2/JawX"].FloatAt(0), -.65f, "Jaw right minus left");
    Near(initial[avatar + "JawOpen"].FloatAt(0), 1, "Unsigned values are clamped");
    Check(initial[avatar + "v2/JawLeft"].Tags == ",i" && initial[avatar + "v2/JawLeft"].IntAt(0) == 1, "Advertised int has an integer tag and value");
    Near(initial[avatar + "FT/v2/EyeLidLeft"].FloatAt(0), .55f, "VRCFT eyelid combines openness and widening");
    Check(initial[avatar + "FT/v2/CheekPuffSuckLeft1"].Tags == ",T" &&
        initial[avatar + "FT/v2/CheekPuffSuckLeft2"].Tags == ",F" &&
        initial[avatar + "FT/v2/CheekPuffSuckLeft4"].Tags == ",T" &&
        initial[avatar + "FT/v2/CheekPuffSuckLeftNegative"].Tags == ",T", "Signed binary encodes magnitude bits and sign");
    Check(initial[avatar + "ExpressionTrackingActive"].Tags == ",T" && initial[avatar + "FT/v2/EyeTrackingActive"].Tags == ",T", "Root and prefixed tracking toggles use boolean OSC tags");
    var gaze = initial[eye + "LeftRightPitchYaw"];
    Check(gaze.Tags == ",ffff", "Only the preferred advertised gaze endpoint is used");
    Near(gaze.FloatAt(0), -MathF.Atan2(2, MathF.Sqrt(65)) * 180 / MathF.PI, "Positive source pitch becomes negative OSC pitch");
    Near(gaze.FloatAt(3), MathF.Atan2(-3, 8) * 180 / MathF.PI, "Right-eye yaw retains its sign and has degrees");
    Check(receiver.Available == 0, "Unrelated, malformed, wrong-version and wrong-type addresses receive zero packets");

    output.Send(values);
    Check(receiver.Available == 0, "An unchanged frame sends no packets before the heartbeat");
    clock.Advance(500);
    output.Send(values);
    Check(Receive(11).Count == 11, "Heartbeat republishes all currently owned channels");

    // Camera/native source disappears while jaw updates continue.
    var jawOnly = new Dictionary<string, float> { ["JawOpen"] = .2f };
    output.Send(jawOnly);
    var loss = Receive(10);
    Near(loss[avatar + "FT/v2/EyeLidLeft"].FloatAt(0), .75f, "Dropped eyelids return to open neutral");
    Near(loss[avatar + "FT/Nested/v2/JawX"].FloatAt(0), 0, "Dropped signed axes return to center");
    Check(loss[avatar + "FT/v2/EyeTrackingActive"].Tags == ",F" && loss[avatar + "FT/v2/CheekPuffSuckLeftNegative"].Tags == ",F", "Lost sign and prefixed eye ownership clear");
    Check(!loss.ContainsKey(avatar + "ExpressionTrackingActive"), "Still-live expressions retain current ownership");
    output.Send(jawOnly);
    Check(receiver.Available == 0, "Dropped channels neutralize once");
    output.Reset();
    var stopped = Receive(2);
    Check(!output.HasActiveOutput, "Stop clears output readiness");
    Near(stopped[avatar + "JawOpen"].FloatAt(0), 0, "Stop clears the remaining face sample");
    Check(stopped[avatar + "ExpressionTrackingActive"].Tags == ",F", "Stop releases the expression toggle");
    output.Reset();
    Check(receiver.Available == 0, "Repeated Stop sends no extra packets");
}

using (var output = new VrchatFaceOutput(endpoint, inputs, nativeFace: true, time: clock))
{
    Check(output.SupportedInputCount == 1, "Full face tracking selects native independently of legacy parameters");
    output.Send(new Dictionary<string, float>
    {
        ["EyeRightDirectionX"] = .25f, ["EyeRightDirectionY"] = -.5f, ["EyeRightDirectionZ"] = MathF.Sqrt(.6875f),
        ["EyeLeftDirectionX"] = .5f, ["EyeLeftDirectionY"] = .25f, ["EyeLeftDirectionZ"] = MathF.Sqrt(.6875f),
        ["EyeRightOpenness"] = 0, ["EyeLeftOpenness"] = 1,
        ["EyeRightPupil"] = .75f, ["EyeLeftPupil"] = .25f,
        ["JawOpen"] = 1, ["TongueOut"] = .5f, ["NeckFlexLeft"] = 1,
    });
    const string native = "/tracking/face/v1";
    var packet = Receive(1)[native];
    Check(packet.Tags == ",b" && packet.IntAt(0) == 102 && packet.Payload.Length == 108, "Native blob has exactly 102 data bytes plus OSC framing and padding");
    ReadOnlySpan<byte> face = packet.Payload.AsSpan(4, 102);
    Check(face[0] == 63 && face[1] == 0 && face[2] == 0 && face[3] == 127 &&
        face[4] == 0 && face[5] == 127 && face[6] == 63 && face[7] == 0, "Gaze splits into the documented opposing per-eye directions");
    Check(face[8] == 255 && face[9] == 0 && face[10] == 127 && face[11] == 0 && face[12] == 0 && face[13] == 127, "Blink and pupil channels remain independent");
    Check(face[36] == 255 && face[86] == 127 && face[101] == 255, "Jaw, tongue and last face key occupy the documented slots");
    Check(packet.Payload.AsSpan(106).IndexOfAnyExcept((byte)0) < 0 && receiver.Available == 0, "Padding is zero and legacy writes are disabled");
    output.Send(new Dictionary<string, float> { ["JawOpen"] = .25f });
    var partial = Receive(1)[native];
    Check(partial.Payload[4 + 36] == 63 && partial.Payload[4 + 8] == 0 && partial.Payload[4 + 86] == 0,
        "Native partial loss clears unavailable keys while preserving live data");
    var empty = new Dictionary<string, float>();
    output.Send(empty);
    Check(Receive(1)[native].Payload.AsSpan(4).IndexOfAnyExcept((byte)0) < 0, "Loss of all native channels sends one neutral blob");
    output.Send(empty);
    output.Reset();
    Check(receiver.Available == 0, "A stopped native source does not hold VRChat tracking active with repeated zeros");
}

using (var output = new VrchatFaceOutput(endpoint, new Dictionary<string, string>
{
    [eye + "LeftRightVec"] = "ffffff",
    [eye + "EyesClosedAmount"] = "f",
}))
{
    output.Send(new Dictionary<string, float> { ["EyeLeftX"] = .25f, ["EyeLeftY"] = .1f, ["EyeRightOpenness"] = .2f });
    var builtin = Receive(2);
    Near(builtin[eye + "EyesClosedAmount"].FloatAt(0), .8f, "Builtin blink can use the only available eye");
    var nativeGaze = builtin[eye + "LeftRightVec"];
    Check(nativeGaze.FloatAt(0) > 0 && nativeGaze.FloatAt(1) > 0 && nativeGaze.FloatAt(2) > .95f &&
        nativeGaze.FloatAt(0) == nativeGaze.FloatAt(3), "Builtin gaze uses a normalized vector and an available-eye fallback");
    output.Dispose();
    Check(receiver.Available == 0, "Disposal on a destination change does not reset a new avatar");
}

using (var output = new VrchatFaceOutput(endpoint, new Dictionary<string, string>()))
{
    Check(output.EyeOnly && output.SupportedInputCount == 2, "Verified clients can use the two documented eye inputs even if the OSCQuery tree omits them");
    output.Send(new Dictionary<string, float> { ["JawOpen"] = 1 });
    Check(!output.HasActiveOutput && receiver.Available == 0, "An eye-only route with jaw-only input sends nothing and stays inactive");
    output.Send(new Dictionary<string, float> { ["EyeLeftX"] = .25f, ["EyeLeftY"] = .1f, ["EyeRightOpenness"] = .2f });
    var builtin = Receive(2);
    Check(output.HasActiveOutput && builtin.ContainsKey(eye + "LeftRightPitchYaw") && builtin.ContainsKey(eye + "EyesClosedAmount"),
        "The documented eye fallback reports activity after compatible tracking arrives");
}

using (var output = new VrchatFaceOutput(endpoint, new Dictionary<string, string>
{
    [eye + "LeftRightPitchYaw"] = "f",
}, blockedInputs: new HashSet<string>(StringComparer.Ordinal) { eye + "EyesClosedAmount" }))
{
    output.Send(new Dictionary<string, float> { ["EyeLeftX"] = .25f, ["EyeLeftY"] = .1f, ["EyeRightOpenness"] = .2f });
    Check(output.SupportedInputCount == 0 && !output.EyeOnly && !output.HasActiveOutput && receiver.Available == 0,
        "An explicit incompatible type or blocked input suppresses that fallback");
}

// The calibrated camera uses physical eye order for VRChat and a historical side mapping for custom avatar parameters.
var singleEye = new Dictionary<string, float>
{
    ["EyeLeftDirectionX"] = .6f, ["EyeLeftDirectionY"] = 0, ["EyeLeftDirectionZ"] = .8f,
    ["EyeRightX"] = .3f, ["EyeRightY"] = .1f,
};
using (var output = new VrchatFaceOutput(endpoint, new Dictionary<string, string>
{
    [avatar + "v2/EyeLeftX"] = "f", [avatar + "v2/EyeRightX"] = "f",
}))
{
    output.Send(singleEye);
    Near(Receive(1)[avatar + "v2/EyeRightX"].FloatAt(0), .3f, "Custom avatars receive the legacy camera side");
    Check(receiver.Available == 0, "The missing legacy eye is left unowned");
}
using (var output = new VrchatFaceOutput(endpoint, new Dictionary<string, string> { [eye + "LeftRightVec"] = "ffffff" }))
{
    output.Send(singleEye);
    var packet = Receive(1)[eye + "LeftRightVec"];
    Near(packet.FloatAt(0), .6f, "Built-in eye output uses the physical camera side");
    Near(packet.FloatAt(3), .6f, "Built-in monocular fallback mirrors the physical eye");
}
using (var output = new VrchatFaceOutput(endpoint, inputs, nativeFace: true))
{
    output.Send(singleEye);
    var packet = Receive(1)["/tracking/face/v1"];
    Check(packet.Payload[4 + 5] == 153 && packet.Payload.AsSpan(4, 4).IndexOfAnyExcept((byte)0) < 0,
        "Native face tracking sends physical left gaze and keeps the unavailable right eye neutral");
}

Console.WriteLine("VRChat face output loopback checks passed.");

Dictionary<string, OscPacket> Receive(int count)
{
    var result = new Dictionary<string, OscPacket>(StringComparer.Ordinal);
    for (int i = 0; i < count; i++)
    {
        IPEndPoint? remote = null;
        byte[] bytes;
        try
        {
            bytes = receiver.Receive(ref remote);
        }
        catch (SocketException error)
        {
            throw new InvalidOperationException($"Received {result.Count}/{count} expected OSC packets: {string.Join(", ", result.Keys)}", error);
        }
        int offset = 0;
        string address = OscString(bytes, ref offset);
        string tags = OscString(bytes, ref offset);
        result.Add(address, new OscPacket(tags, bytes[offset..]));
    }
    return result;
}

static string OscString(byte[] bytes, ref int offset)
{
    int end = Array.IndexOf(bytes, (byte)0, offset);
    Check(end >= 0, "OSC strings are terminated");
    string result = Encoding.ASCII.GetString(bytes, offset, end - offset);
    offset = (end + 4) & ~3;
    return result;
}

static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < .0001f, message);

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed record OscPacket(string Tags, byte[] Payload)
{
    internal float FloatAt(int index) => BinaryPrimitives.ReadSingleBigEndian(Payload.AsSpan(index * 4));
    internal int IntAt(int index) => BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(index * 4));
}

sealed class ManualClock : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _timestamp;
    internal void Advance(long milliseconds) => _timestamp += milliseconds;
}
