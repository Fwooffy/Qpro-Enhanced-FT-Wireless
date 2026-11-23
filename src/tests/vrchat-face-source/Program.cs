using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using QproFaceTracking.Hub;

var occupied = new HashSet<int>();
int checks = 0;
if (OperatingSystem.IsWindows())
    CheckVirtualDesktop();
CheckSteam();
CheckSteamTongueFreshness();
CheckLabelsOnly();
CheckConflictCleanup();
Console.WriteLine($"Direct VRChat face source passed {checks} checks.");

[SupportedOSPlatform("windows")]
void CheckVirtualDesktop()
{
    string mapName = "Qpro.DirectFaceTest." + Guid.NewGuid();
    var ports = new VrchatFacePorts(0, FreePort(), FreePort(), FreePort(), null);
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    long now = 1000;
    using var source = new VrchatFaceSource(false, mapName, ports, () => now, loadPreferences: false);

    using (var map = MemoryMappedFile.CreateNew(mapName, 360))
    using (var view = map.CreateViewAccessor())
    {
        var state = new byte[360];
        state[0] = 3; // Lower face available and alternate VD tongue layout.
        state[1] = state[292] = state[293] = 1;
        PutWeight(2, 0.7f); PutWeight(3, 0.2f);
        PutWeight(6, .16f); PutWeight(7, .74f);
        PutWeight(32, .1f); PutWeight(33, .06f);
        PutWeight(12, 0.5f); PutWeight(13, 0.1f);
        PutWeight(24, 0.62f); PutWeight(28, 0.4f);
        PutWeight(35, 0.22f); PutWeight(40, 0.31f);
        PutWeight(55, 0.2f); PutWeight(61, 0.5f);
        PutWeight(63, 0.3f); PutWeight(64, 0.45f);
        PutWeight(68, 0.95f); PutWeight(52, float.NaN);
        PutFloat(state, 300, -MathF.Sin(0.1f));
        PutFloat(state, 308, MathF.Cos(0.1f));
        Quaternion right = Quaternion.CreateFromYawPitchRoll(-0.25f, 0.4f, 0);
        PutFloat(state, 324, right.X);
        PutFloat(state, 328, right.Y);
        PutFloat(state, 332, right.Z);
        PutFloat(state, 336, right.W);
        view.WriteArray(0, state, 0, state.Length);

        var face = source.Read();
        Equal(face, "EyeLeftX", 0.2f);
        Equal(face, "EyeRightY", 0.4f);
        Equal(face, "EyeRightDirectionX", MathF.Sin(0.25f) * MathF.Cos(0.4f));
        Equal(face, "EyeRightDirectionY", MathF.Sin(0.4f));
        Equal(face, "EyeRightDirectionZ", MathF.Cos(0.25f) * MathF.Cos(0.4f));
        Equal(face, "EyeLeftOpenness", 0.3f);
        Equal(face, "EyeRightOpenness", 0.9f);
        Equal(face, "EyeSquintLeft", 0.4f);
        Equal(face, "CheekPuffLeft", 1);
        Equal(face, "CheekPuffRight", 0);
        Equal(face, "JawOpen", 0.62f);
        Equal(face, "LipFunnelUpperLeft", 0.22f);
        Equal(face, "LipPuckerUpperLeft", 0.31f);
        Equal(face, "LipPuckerLowerLeft", 0.31f);
        Equal(face, "MouthLowerDownRight", 0);
        Equal(face, "MouthUpperUpLeft", 0.3f);
        Equal(face, "MouthUpperDeepenLeft", 0.3f);
        Equal(face, "TongueOut", 0.3f);
        Equal(face, "TongueLeft", 0.45f);
        Equal(face, "TongueCurlUp", 0);
        var allExtras = new VrchatFaceSelection(true, true, true, true, true, true);
        var noExtras = new VrchatFaceSelection(false, false, false, false, false, false);
        using (var idle = new VrchatFaceSource(false, mapName, ports, () => now, loadPreferences: true, unadjustedNative: true))
        {
            var raw = idle.Read();
            Equal(raw, "CheekPuffLeft", .7f);
            Equal(raw, "CheekPuffRight", .2f);
            Equal(raw, "CheekSuckLeft", .16f);
            Equal(raw, "CheekSuckRight", .74f);
            Equal(raw, "MouthCornerPullLeft", .1f);
            Equal(raw, "MouthCornerPullRight", .06f);
            Check(!raw.ContainsKey("EyeLeftPupil") && !raw.ContainsKey("EyeRightPupil"),
                "raw Virtual Desktop pose has no measured pupil");
        }
        int waitingReads = 0;
        using (var idleWaiting = new VrchatFaceSource(false, mapName, ports, () => { ++waitingReads; return now; },
            loadPreferences: false, unadjustedNative: true))
        {
            for (int tick = 0; tick < 100; tick++) idleWaiting.ReadWithoutTarget();
            Check(waitingReads == 0, "100 raw VD ticks without a target perform zero source reads even with a live map");
            Equal(idleWaiting.Read(), "JawOpen", .62f);
            Check(waitingReads == 1, "a returning target samples the latest VD map immediately");
        }

        Send(sender, ports.Gaze, []);
        Send(sender, ports.Gaze, Camera("QPGE", 3, 0.35f, 0.33f, float.NaN, 0));
        Send(sender, ports.Tongue, Camera("QPTO", 1, 0.8f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Send(sender, ports.Pupil, Camera("QPDI", 3, 8f, 10f));
        face = AwaitFace(source, current => Near(current.GetValueOrDefault("EyeRightX"), 0.35f) &&
            Near(current.GetValueOrDefault("TongueOut"), 0.8f) && Near(current.GetValueOrDefault("EyeLeftPupil"), 1));
        Equal(face, "EyeRightX", 0.35f);
        Equal(face, "EyeRightY", 0.33f);
        Equal(face, "EyeLeftDirectionX", MathF.Sin(0.35f) * MathF.Cos(0.33f));
        Equal(face, "EyeLeftDirectionY", MathF.Sin(0.33f));
        Equal(face, "EyeLeftX", 0.2f);
        Equal(face, "EyeRightDirectionY", MathF.Sin(0.4f));
        Equal(face, "EyeLeftOpenness", 0.3f);
        Equal(face, "EyeLeftPupil", 1);
        Equal(face, "EyeRightPupil", 0.5f);
        Equal(face, "TongueOut", 0.8f);
        Equal(face, "TongueUp", 0.4f);
        Equal(face, "TongueLeft", 0);
        Equal(face, "JawOpen", 0.62f);

        // A failed gaze worker may still send during cleanup. The same session
        // can keep another adjustment running, go fully native and reselect it.
        source.SetFaceAdjustments(noExtras with { CheekPuff = true });
        face = source.Read();
        Equal(face, "EyeRightX", .25f);
        Equal(face, "EyeLeftDirectionX", MathF.Sin(.2f));
        Equal(face, "CheekPuffLeft", 1);
        Equal(face, "CheekSuckLeft", .16f);
        Check(!face.ContainsKey("EyeLeftPupil"), "turning off pupil input also relinquishes its synthetic neutral");
        Send(sender, ports.Gaze, Camera("QPGE", 3, .8f, 0, -.8f, 0));
        source.SetFaceAdjustments(noExtras);
        face = source.Read();
        Equal(face, "CheekPuffLeft", .7f);
        Equal(face, "CheekPuffRight", .2f);
        Equal(face, "MouthCornerPullLeft", .1f);
        Equal(face, "EyeRightX", .25f);
        Equal(face, "TongueOut", .3f);
        source.SetFaceAdjustments(noExtras with { CheekPuff = true });
        face = source.Read();
        Equal(face, "CheekPuffLeft", 1);
        Equal(face, "EyeLeftDirectionX", MathF.Sin(.2f));
        source.SetFaceAdjustments(allExtras);
        face = source.Read();
        Equal(face, "EyeRightX", .25f);
        // Only fresh packets after reenabling a camera may take ownership.
        Send(sender, ports.Gaze, Camera("QPGE", 1, .35f, .33f, 0, 0));
        Send(sender, ports.Tongue, Camera("QPTO", 1, .8f, .4f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Send(sender, ports.Pupil, Camera("QPDI", 1, 8f, 0));
        AwaitFace(source, current => Near(current.GetValueOrDefault("EyeRightX"), .35f) &&
            Near(current.GetValueOrDefault("TongueOut"), .8f) && Near(current.GetValueOrDefault("EyeLeftPupil"), 1));

        now += 250;
        Equal(source.Read(), "EyeRightX", 0.35f);
        now++;
        Equal(source.Read(), "EyeLeftX", 0.2f);
        Equal(source.Read(), "TongueOut", 0.8f);
        now += 50;
        Equal(source.Read(), "TongueOut", 0.3f);
        Equal(source.Read(), "EyeLeftPupil", 1);
        now += 50;
        Equal(source.Read(), "EyeLeftPupil", 0.5f);

        state[0] = 1;
        state[293] = 0;
        view.WriteArray(0, state, 0, state.Length);
        face = source.Read();
        Equal(face, "TongueOut", 0.95f);
        Equal(face, "TongueCurlUp", 0.45f);
        Check(!face.ContainsKey("EyeRightX"), "VD right validity is independent");
        state[1] = 0;
        view.WriteArray(0, state, 0, state.Length);
        face = source.Read();
        Equal(face, "JawOpen", 0.62f);
        Equal(face, "EyeLeftPupil", 0.5f);
        Check(!face.ContainsKey("EyeLeftOpenness") && !face.ContainsKey("EyeRightOpenness") &&
            !face.ContainsKey("EyeRightPupil"), "one valid gaze and a moving jaw cannot revive invalid eye data");
        state[0] = 0;
        view.WriteArray(0, state, 0, state.Length);
        Send(sender, ports.Tongue, Camera("QPTO", 1, 0.85f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        face = AwaitFace(source, current => Near(current.GetValueOrDefault("TongueOut"), 0.85f));
        Equal(face, "TongueOut", 0.85f);
        Check(!face.ContainsKey("JawOpen"), "unavailable lower face never publishes frozen jaw");
        Send(sender, ports.Tongue, Camera("QPTO", 0, 0.85f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Check(!AwaitFace(source, current => !current.ContainsKey("TongueOut")).ContainsKey("TongueOut"),
            "explicit disable releases camera tongue ownership");

        Array.Clear(state);
        state[0] = state[1] = state[292] = state[293] = 1;
        PutFloat(state, 308, 1);
        PutFloat(state, 336, 1);
        view.WriteArray(0, state, 0, state.Length);
        Equal(source.Read(), "EyeLeftOpenness", 1);
        now += 5000;
        face = source.Read();
        Equal(face, "EyeLeftOpenness", 1);
        Equal(face, "EyeRightOpenness", 1);
        Equal(face, "EyeLeftX", 0);
        Equal(face, "EyeRightY", 0);
        Equal(face, "JawOpen", 0);

        void PutWeight(int index, float weight) => PutFloat(state, 4 + index * sizeof(float), weight);
    }
    Check(source.Read().Count == 0, "closing the VD producer releases the named map and expires native values");
    Send(sender, ports.Gaze, Camera("QPGE", 2, 0, 0, -0.19f, 0.17f));
    Send(sender, ports.Pupil, Camera("QPDI", 1, 5.6f, 0));
    var cameraOnly = AwaitFace(source, current => current.ContainsKey("EyeRightDirectionX") && current.ContainsKey("EyeLeftPupil"));
    Equal(cameraOnly, "EyeLeftX", -0.19f);
    Equal(cameraOnly, "EyeLeftPupil", 0.6f);
    Check(!cameraOnly.ContainsKey("EyeRightX") && !cameraOnly.ContainsKey("EyeLeftDirectionX") && !cameraOnly.ContainsKey("JawOpen") &&
        !cameraOnly.ContainsKey("EyeRightPupil"), "camera sources work independently while the streamer is offline");
}

void CheckSteam()
{
    using var labels = Listen();
    var ports = new VrchatFacePorts(FreePort(), FreePort(), FreePort(), FreePort(), Port(labels));
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    long offset = 0;
    using var source = new VrchatFaceSource(true, "unused", ports, () => Environment.TickCount64 + offset, loadPreferences: false);
    Send(sender, ports.Tongue, Camera("QPTO", 1, 0.92f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    Send(sender, ports.Steam, SteamFrame());
    var face = AwaitFace(source, current => current.ContainsKey("JawOpen") &&
        current.ContainsKey("EyeLeftX") && Near(current.GetValueOrDefault("TongueOut"), 0.92f));
    Equal(face, "EyeLeftX", MathF.Atan2(0.4f, 1));
    Equal(face, "EyeRightX", face["EyeLeftX"]);
    Equal(face, "EyeLeftY", MathF.Atan2(0.2f, 1));
    Equal(face, "EyeRightY", face["EyeLeftY"]);
    Vector3 forward = Vector3.Normalize(new(0.4f, 0.2f, 1));
    Equal(face, "EyeLeftDirectionX", forward.X);
    Equal(face, "EyeLeftDirectionY", forward.Y);
    Equal(face, "EyeLeftDirectionZ", forward.Z);
    Equal(face, "EyeLeftOpenness", 0.75f);
    Equal(face, "EyeRightOpenness", 0.5f);
    Equal(face, "MouthUpperUpLeft", 0.3f);
    Equal(face, "MouthUpperDeepenLeft", 0.1f);
    Equal(face, "JawOpen", 0.55f);
    Equal(face, "TongueOut", 0.92f);

    CheckLabels(labels);

    Send(sender, ports.Steam, Osc("/sl/eyeTrackedGazePoint", 0.4f, 0.2f, 0));
    face = AwaitFace(source, current => Near(current.GetValueOrDefault("EyeLeftX"), MathF.PI / 2));
    Vector3 sidePlane = Vector3.Normalize(new(0.4f, 0.2f, 0));
    Equal(face, "EyeLeftDirectionX", sidePlane.X);
    Equal(face, "EyeLeftDirectionY", sidePlane.Y);
    Equal(face, "EyeLeftDirectionZ", 0);

    Send(sender, ports.Steam, Osc("/sl/xrfb/facec/LowerFace", 0));
    face = AwaitFace(source, current => !current.ContainsKey("JawOpen"));
    Check(!face.ContainsKey("JawOpen"), "Steam lower-face capability masks jaw independently");
    Equal(face, "EyeRightOpenness", 0.5f);
    Equal(face, "TongueOut", 0.92f);
    offset += 501;
    Check(source.Read().Count == 0, "stale Steam face, gaze and camera clear together");
}

void CheckLabelsOnly()
{
    using var labels = Listen();
    using var gaze = Listen();
    using var tongue = Listen();
    using var pupil = Listen();
    var ports = new VrchatFacePorts(FreePort(), Port(gaze), Port(tongue), Port(pupil), Port(labels));
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    using var source = new VrchatFaceSource(true, "unused", ports,
        () => Environment.TickCount64, loadPreferences: false, labelsOnly: true);
    Send(sender, ports.Steam, SteamFrame());
    Check(AwaitFace(source, _ => labels.Available > 0).Count == 0,
        "labels-only works without opening occupied camera ports or producing face output");
    CheckLabels(labels);
    var idlePorts = ports with { Steam = FreePort() };
    using var idle = new VrchatFaceSource(true, "unused", idlePorts,
        () => Environment.TickCount64, loadPreferences: true, unadjustedNative: true);
    Send(sender, idlePorts.Steam, Bundle(SteamFrame(), Osc("/sl/xrfb/facew/CheekPuffL", .61f),
        Osc("/sl/xrfb/facew/CheekPuffR", .14f), Osc("/sl/xrfb/facew/LipCornerPullerR", .28f)));
    var raw = AwaitFace(idle, frame => frame.ContainsKey("CheekPuffLeft"));
    Equal(raw, "CheekPuffLeft", .61f);
    Equal(raw, "CheekPuffRight", .14f);
    Equal(raw, "MouthCornerPullRight", .28f);
    Check(!raw.ContainsKey("EyeLeftPupil") && !raw.ContainsKey("EyeRightPupil"), "raw Steam pose has no measured pupil");
    Check(labels.Available == 0, "idle mode reserves only the Steam source port and emits no capture labels");
    idle.SetSteamCaptureLabels(true);
    idle.ReadWithoutTarget();
    CheckLabels(labels);
    Equal(idle.Read(), "CheekPuffRight", .14f);
    idle.SetSteamCaptureLabels(false);
    idle.SetSteamCaptureLabels(true);
    Equal(idle.Read(), "MouthCornerPullRight", .28f);
    CheckLabels(labels); // A second recorder needs a fresh schema, even between polls.
    idle.SetSteamCaptureLabels(false);
    idle.Read();
    Thread.Sleep(25);
    Equal(idle.Read(), "CheekPuffLeft", .61f);
    Check(labels.Available == 0, "detaching the recorder stops label publication while native forwarding continues");
    Send(sender, idlePorts.Steam, Osc("/sl/xrfb/facew/JawDrop", .89f));
    idle.ReadWithoutTarget();
    Check(idle.Read().Count == 0, "a returning target cannot inherit Steam packets discarded before discovery");
    Send(sender, idlePorts.Steam, SteamFrame());
    Equal(AwaitFace(idle, frame => frame.ContainsKey("JawOpen")), "JawOpen", .55f);
}

void CheckSteamTongueFreshness()
{
    using var labels = Listen();
    var ports = new VrchatFacePorts(FreePort(), 0, 0, 0, Port(labels));
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    long now = 1000;
    using var source = new VrchatFaceSource(true, "unused", ports, () => now, loadPreferences: false,
        enableGaze: false, enableTongue: false, enablePupils: false);
    var motion = Bundle(Osc("/sl/xrfb/facec/LowerFace", 1), Osc("/sl/xrfb/facec/UpperFace", 1),
        Osc("/sl/xrfb/facew/JawDrop", .42f), Osc("/sl/xrfb/facew/EyesClosedL", .8f));
    Send(sender, ports.Steam, motion);
    var frame = AwaitFace(source, current => current.ContainsKey("JawOpen"));
    Equal(frame, "EyeLeftOpenness", .2f);
    Check(!frame.ContainsKey("EyeRightOpenness") && !frame.ContainsKey("TongueOut"),
        "missing Steam channels do not fabricate an open eye or retracted tongue");
    var initial = NextLabel();
    Check(!initial.GetProperty("nativeTongueValid").GetBoolean() && !initial.TryGetProperty("nativeTongueAgeMs", out _),
        "jaw-only weights leave the training tongue label unavailable");

    now += 20;
    Send(sender, ports.Steam, Osc("/sl/xrfb/facew/TongueOut", 0));
    frame = AwaitFace(source, current => current.ContainsKey("TongueOut"));
    var neutral = NextLabel();
    Check(neutral.GetProperty("nativeTongueValid").GetBoolean() && neutral.GetProperty("nativeTongueAgeMs").GetInt64() == 0,
        "a measured neutral tongue is valid");
    int change = neutral.GetProperty("sourceChangeSequence").GetInt32();
    now += 230;
    Send(sender, ports.Steam, motion);
    var held = NextLabel();
    Check(held.GetProperty("sourceChangeSequence").GetInt32() == change &&
        held.GetProperty("sourceUnchangedMs").GetDouble() == 230 && held.GetProperty("nativeTongueAgeMs").GetInt64() == 230,
        "repeated expression packets preserve held pose duration and the actual tongue age");

    now += 271;
    Send(sender, ports.Steam, motion);
    frame = AwaitFace(source, current => current.ContainsKey("JawOpen") && !current.ContainsKey("TongueOut"));
    Check(!NextLabel().GetProperty("nativeTongueValid").GetBoolean(), "fresh jaw cannot renew an expired tongue label");

    JsonElement NextLabel()
    {
        IPEndPoint? remote = null;
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(1))
        {
            source.Read();
            while (labels.Available > 0)
            {
                using var data = JsonDocument.Parse(labels.Receive(ref remote));
                if (data.RootElement.GetProperty("type").GetString() == "sample")
                    return data.RootElement.Clone();
            }
            Thread.Sleep(1);
        }
        throw new TimeoutException("Missing Steam label sample.");
    }
}

void CheckConflictCleanup()
{
    using var pupil = Listen();
    var ports = new VrchatFacePorts(0, FreePort(), FreePort(), Port(pupil), null);
    try
    {
        using var unexpected = new VrchatFaceSource(false, "unused", ports,
            () => Environment.TickCount64, loadPreferences: false);
        Check(false, "occupied pupil port must make startup fail");
    }
    catch (InvalidOperationException error)
    {
        Check(error.Message.Contains($"UDP port {Port(pupil)}"), "port conflict names the occupied input");
    }
    using var reboundGaze = Listen(ports.Gaze);
    using var reboundTongue = Listen(ports.Tongue);
    Check(true, "startup disposes inputs it opened before a later bind failed");
    using var nativeOnly = new VrchatFaceSource(false, "Qpro.DirectFaceTest." + Guid.NewGuid(), ports,
        () => Environment.TickCount64, loadPreferences: false, enableGaze: false, enableTongue: false, enablePupils: false);
    var waiting = nativeOnly.Read();
    Check(waiting.Count == 0, "native-only mode leaves occupied camera ports alone");
    Check(ReferenceEquals(waiting, nativeOnly.Read()), "waiting reuses the allocation-free empty snapshot");
}

void CheckLabels(UdpClient receiver)
{
    receiver.Client.ReceiveTimeout = 1000;
    IPEndPoint sender = new(IPAddress.Loopback, 0);
    using var schema = JsonDocument.Parse(receiver.Receive(ref sender));
    using var sample = JsonDocument.Parse(receiver.Receive(ref sender));
    Check(IPAddress.IsLoopback(sender.Address), "labels are local");
    Check(schema.RootElement.GetProperty("type").GetString() == "schema", "label schema precedes sample");
    Check(schema.RootElement.GetProperty("names")[68].GetString() == "TongueOut", "label schema order is native XR_FB");
    JsonElement root = sample.RootElement;
    Check(root.GetProperty("type").GetString() == "sample" &&
        root.GetProperty("trackingSource").GetString() == "SteamLink" &&
        root.GetProperty("faceFlags").GetInt32() == 1 &&
        root.GetProperty("isEyeFollowingBlendshapesValid").GetBoolean() &&
        root.GetProperty("leftEyeIsValid").GetBoolean() && root.GetProperty("rightEyeIsValid").GetBoolean(),
        "Steam label preserves native source, capabilities, and gaze validity");
    Check(Near(root.GetProperty("values")[68].GetSingle(), 0.35f), "label tongue remains stock before overrides");
    JsonElement q = root.GetProperty("leftEyeOrientation");
    Quaternion orientation = new(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle());
    Vector3 direction = Vector3.Transform(-Vector3.UnitZ, orientation);
    Vector3 expected = Vector3.Normalize(new(0.4f, 0.2f, -1));
    Check(Vector3.Distance(direction, expected) < 0.0001f, "Steam labels preserve the actual off-axis gaze direction");
}

static byte[] SteamFrame() => Bundle(
    Osc("/sl/xrfb/facec/UpperFace", 1), Osc("/sl/xrfb/facec/LowerFace", 1),
    Osc("/sl/xrfb/facew/EyesClosedL", 0.25f), Osc("/sl/xrfb/facew/EyesClosedR", 0.5f),
    Osc("/sl/xrfb/facew/JawDrop", 0.55f), Osc("/sl/xrfb/facew/UpperLipRaiserL", 0.5f),
    Osc("/sl/xrfb/facew/NoseWrinklerL", 0.2f), Osc("/sl/xrfb/facew/TongueOut", 0.35f),
    Osc("/sl/eyeTrackedGazePoint", 0.4f, 0.2f, -1));

static byte[] Camera(string magic, byte flags, params float[] values)
{
    byte[] packet = new byte[8 + values.Length * sizeof(float)];
    Encoding.ASCII.GetBytes(magic, packet);
    packet[4] = 1;
    packet[5] = flags;
    for (int index = 0; index < values.Length; index++)
        PutFloat(packet, 8 + index * sizeof(float), values[index]);
    return packet;
}

static void PutFloat(byte[] bytes, int offset, float value) =>
    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, sizeof(float)), value);

static byte[] Osc(string address, params float[] values)
{
    using var message = new MemoryStream();
    String(address);
    String("," + new string('f', values.Length));
    Span<byte> number = stackalloc byte[4];
    foreach (float value in values)
    {
        BinaryPrimitives.WriteSingleBigEndian(number, value);
        message.Write(number);
    }
    return message.ToArray();

    void String(string value)
    {
        message.Write(Encoding.ASCII.GetBytes(value));
        message.WriteByte(0);
        while (message.Position % 4 != 0)
            message.WriteByte(0);
    }
}

static byte[] Bundle(params byte[][] elements)
{
    using var bundle = new MemoryStream();
    bundle.Write("#bundle\0"u8);
    bundle.Write(new byte[8]);
    Span<byte> size = stackalloc byte[4];
    foreach (byte[] element in elements)
    {
        BinaryPrimitives.WriteInt32BigEndian(size, element.Length);
        bundle.Write(size);
        bundle.Write(element);
    }
    return bundle.ToArray();
}

int FreePort()
{
    while (true)
    {
        using var socket = Listen();
        if (occupied.Add(Port(socket)))
            return Port(socket);
    }
}

static UdpClient Listen(int port = 0)
{
    var client = new UdpClient(AddressFamily.InterNetwork);
    client.Client.ExclusiveAddressUse = true;
    client.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
    return client;
}

static int Port(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;

static void Send(UdpClient client, int port, byte[] packet) =>
    client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));

static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 0.0001f;

static IReadOnlyDictionary<string, float> AwaitFace(VrchatFaceSource source,
    Func<IReadOnlyDictionary<string, float>, bool> ready)
{
    long start = Stopwatch.GetTimestamp();
    IReadOnlyDictionary<string, float> face;
    do
    {
        face = source.Read();
        if (ready(face))
            return face;
        Thread.Sleep(1);
    } while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1);
    throw new TimeoutException("The private test datagram did not take effect within one second. " +
        string.Join(", ", face.Select(entry => $"{entry.Key}={entry.Value}")));
}

void Equal(IReadOnlyDictionary<string, float> face, string key, float value) =>
    Check(face.TryGetValue(key, out float found) && Near(found, value), $"{key} expected {value}, got {found}");

void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    checks++;
}
