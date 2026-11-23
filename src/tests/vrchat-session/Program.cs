using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QproFaceTracking.Hub;

await SessionRecoveryFixtures.Run();

// Only discovery of the fixture's service address is injected. HTTP, Steam's
// UDP input, expression mapping and the game's UDP receiver are real sockets.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = timeout.Token;
using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
using var streamer = new UdpClient(AddressFamily.InterNetwork);
using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
using var web = new TcpListener(IPAddress.Loopback, 0);
web.Start();
const string clientName = "VRChat-Client-ABCD";
var service = new VrchatOscService(clientName, (IPEndPoint)web.LocalEndpoint);
int gamePort = ((IPEndPoint)game.Client.LocalEndPoint!).Port;
int cameraPort;
using (var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
    cameraPort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
bool visible = false, native = false;
int waitingTicks = 0, fullReads = 0;
var acceptingInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var host = JsonSerializer.Serialize(new { NAME = clientName, OSC_IP = "127.0.0.1", OSC_PORT = gamePort, OSC_TRANSPORT = "UDP" });
var serving = Serve();
var ports = new VrchatFacePorts(cameraPort, 0, 0, 0, null);
var destination = new IPEndPoint(IPAddress.Loopback, cameraPort);
var selection = new VrchatFaceSelection(false, false, false, false, false, false);
var source = new VrchatFaceSource(true, "unused", ports, () => Environment.TickCount64, false,
    enableGaze: false, enableTongue: false, enablePupils: false);
source.SetFaceAdjustments(selection);
var session = new VrchatFaceSession(() => { Interlocked.Increment(ref fullReads); return source.Read(); },
    source, status =>
    {
        if (status.Text.Contains("Waiting for matching face data")) acceptingInput.TrySetResult();
    }, Discover, (target, ct) => VrchatOscQuery.ReadAsync(http, service, ct),
    TimeSpan.FromMilliseconds(70), source.SetFaceAdjustments, readWithoutTarget: () =>
    {
        source.ReadWithoutTarget();
        Interlocked.Increment(ref waitingTicks);
    });
try
{
    await WaitUntil(() => Volatile.Read(ref waitingTicks) >= 3);
    Check(Volatile.Read(ref fullReads) == 0, "Waiting for a verified target skips full face composition.");
    Check(game.Available == 0, "Waiting for VRChat sends no OSC.");
    Volatile.Write(ref visible, true);
    await acceptingInput.Task.WaitAsync(token);
    SendJaw(.75f);
    byte[] jaw = await Receive("/avatar/parameters/v2/JawOpen");
    Check(Math.Abs(BinaryPrimitives.ReadSingleBigEndian(jaw.AsSpan(^4)) - .75f) < .001f, "Steam Link jaw arrived at the advertised port.");
    session.UpdateSelection(selection with { CheekPuff = true });
    SendJaw(.5f);
    await Receive("/avatar/parameters/v2/JawOpen", packet => BinaryPrimitives.ReadSingleBigEndian(packet.AsSpan(^4)) == .5f);
    session.UpdateSelection(selection);
    SendJaw(.75f);
    await Receive("/avatar/parameters/v2/JawOpen", packet => BinaryPrimitives.ReadSingleBigEndian(packet.AsSpan(^4)) == .75f);
    Volatile.Write(ref native, true);
    SendJaw(.6f);
    byte[] blob = await Receive("/tracking/face/v1");
    Check(BlobJaw(blob) == 153, "An avatar switch changes the output format without restarting.");
    blob = await Receive("/tracking/face/v1", p => BlobJaw(p) == 0);
    Check(BlobJaw(blob) == 0, "A silent source releases the old expression.");
    SendJaw(.8f);
    await Receive("/tracking/face/v1", p => BlobJaw(p) == 204);
    await Task.WhenAll(session.StopAsync(), session.StopAsync()).WaitAsync(token);
    await Receive("/tracking/face/v1", p => BlobJaw(p) == 0);
    using var reopened = new UdpClient(new IPEndPoint(IPAddress.Loopback, cameraPort));
    Check(true, "Stop resets the avatar and releases Steam's local port for a new session.");

    Console.WriteLine("Direct session integration passed: late startup, Steam input, HTTP OSCQuery, UDP output, avatar change, source loss and Stop.");
}
finally
{
    await session.StopAsync();
    timeout.Cancel();
    try { await serving; } catch (OperationCanceledException) { }
}

Task<VrchatOscTarget?> Discover(CancellationToken ct) => Volatile.Read(ref visible)
    ? VrchatOscQuery.ReadAsync(http, service, ct) : Task.FromResult<VrchatOscTarget?>(null);

async Task Serve()
{
    while (true)
    {
        using var client = await web.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        string? request = await reader.ReadLineAsync(token);
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        bool full = Volatile.Read(ref native);
        var tree = """{"FULL_PATH":"/","CONTENTS":{"avatar":{"FULL_PATH":"/avatar","CONTENTS":{"avatarfullfacetracking":{"FULL_PATH":"/avatar/avatarfullfacetracking","ACCESS":1,"TYPE":"T","VALUE":[FLAG]},"parameters":{"FULL_PATH":"/avatar/parameters","CONTENTS":{"v2":{"FULL_PATH":"/avatar/parameters/v2","CONTENTS":{"JawOpen":{"FULL_PATH":"/avatar/parameters/v2/JawOpen","ACCESS":3,"TYPE":"f"}}}}}}}}}"""
            .Replace("FLAG", full ? "true" : "false");
        string body = request?.Contains("HOST_INFO", StringComparison.Ordinal) == true ? host : tree;
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(response, token);
    }
}

void SendJaw(float strength)
{
    foreach (var (path, value) in new[] { ("/sl/xrfb/facec/LowerFace", 1f), ("/sl/xrfb/facew/JawDrop", strength) })
    {
        int offset = (path.Length + 4) & ~3;
        byte[] data = new byte[offset + 8];
        Encoding.ASCII.GetBytes(path, data);
        data[offset] = (byte)',';
        data[offset + 1] = (byte)'f';
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset + 4), value);
        streamer.Send(data, destination);
    }
}

async Task<byte[]> Receive(string address, Func<byte[], bool>? accepts = null)
{
    while (true)
    {
        var packet = await game.ReceiveAsync(token);
        int end = Array.IndexOf(packet.Buffer, (byte)0);
        if (end >= 0 && Encoding.ASCII.GetString(packet.Buffer, 0, end) == address && (accepts is null || accepts(packet.Buffer)))
            return packet.Buffer;
    }
}

static byte BlobJaw(byte[] packet) => packet[20 + 4 + 4 + 36];

async Task WaitUntil(Func<bool> condition)
{
    while (!condition()) await Task.Delay(10, token);
}

static void Check(bool result, string description)
{
    if (!result) throw new InvalidOperationException(description);
}
