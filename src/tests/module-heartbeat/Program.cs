using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qpro.GazeBridge;
using QproFaceTracking.Hub;
using VRCFaceTracking.Core.Library;

var checks = 0;
const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
HubModuleHeartbeatObservation Read(string json, bool steam = false, Func<int, long, bool>? matches = null)
    => HubModuleHeartbeat.Parse(json, steam, Hash, matches ?? ((pid, start) => pid == 123 && start == 456));
HubModuleHeartbeatView View(HubModuleHeartbeatObservation observation, long now, bool running = true, bool current = true)
    => HubModuleHeartbeat.Describe(observation, now, running, current);

// The real asynchronous writer coalesces queued observations and preserves a
// final closed state, even when teardown races a blocked write of active data.
using (var entered = new ManualResetEventSlim())
using (var release = new ManualResetEventSlim())
{
    var writes = new ConcurrentQueue<string>();
    var publisher = new ModuleHeartbeatPublisher("unused", false, Hash, 123, 456, (_, json) =>
    { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); writes.Enqueue(json); });
    publisher.Publish(1000, "active", "unavailable", -1, true, true, Channels(true));
    Check(entered.Wait(TimeSpan.FromSeconds(3)), "writer starts off the tracking thread");
    var watch = Stopwatch.StartNew();
    for (var i = 0; i < 1000; i++) publisher.Publish(1001 + i, "active", "unavailable", -1, true, true, Channels(true));
    publisher.Publish(2001, "closed", "unavailable", -1, true, true, Channels(false));
    publisher.Publish(2002, "active", "fresh", 0, true, true, Channels(true));
    Check(watch.ElapsedMilliseconds < 1000, "blocked status disk does not block producer updates");
    release.Set();
    await publisher.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(writes.Count <= 2, "writer queue is bounded and intermediate observations coalesce");
    var final = Read(writes.Last());
    Check(final.Snapshot?.State == "closed" && final.Snapshot.Sequence == 1002, "terminal close wins and seals later updates");
    Check(View(final, 2001).State == "closed", "closed module is not reported receiving");
    var history = new HubModuleHeartbeatHistory();
    Check(history.Accept(final).Snapshot is not null, "module history accepts the latest observation");
    var prior = new HubModuleHeartbeatObservation(final.Snapshot! with { Sequence = 1, CapturedTickMs = 1000, State = "active" }, "");
    Check(history.Accept(prior).Snapshot is null && history.Accept(prior).Snapshot is null,
        "repeated older snapshots stay rejected after an initial rejection");
    Check(history.Accept(HubModuleHeartbeatObservation.Missing("temporary read failure")).Snapshot is null &&
        history.Accept(prior).Snapshot is null, "read failure cannot erase ordering protection");
    var restart = new HubModuleHeartbeatObservation(final.Snapshot! with { SessionId = Guid.NewGuid().ToString("D"), Sequence = 1, CapturedTickMs = 3000, State = "active" }, "");
    Check(history.Accept(restart).Snapshot is not null && history.Accept(final).Snapshot is null,
        "newer session is accepted and late teardown from its predecessor rejected");
    history.Reset();
    Check(history.Accept(prior).Snapshot is not null, "source context reset permits its independent observation history");
}

foreach (var steam in new[] { false, true })
{
    using var fixture = new ModuleFixture(steam);
    fixture.SendTongue();
    fixture.Module.Update();
    var json = await fixture.Latest();
    var observation = Read(json, steam);
    Check(observation.Snapshot is not null, "production module serializes a receiver-compatible " + (steam ? "Steam Link" : "Virtual Desktop") + " heartbeat");
    Check(observation.Snapshot!.Channels.Length == 5 && observation.Snapshot.Channels.All(channel => channel.Name != "camera-cheeks"),
        "production module and Hub agree on five active channels");
    var legacy = JsonNode.Parse(json)!.AsObject();
    legacy["Channels"]!.AsArray().Add(new JsonObject { ["Name"] = "camera-cheeks", ["AgeMs"] = 0, ["TimeoutMs"] = 500, ["Applied"] = true });
    var oldObservation = Read(legacy.ToJsonString(), steam);
    Check(oldObservation.Snapshot?.Channels.Length == 5 &&
        !View(oldObservation, fixture.Time).Summary.Contains("cheek") && !View(oldObservation, fixture.Time).Detail.Contains("camera-cheeks"),
        "one legacy camera cheek channel is ignored rather than presented as live");
    legacy["Channels"]!.AsArray().RemoveAt(2);
    Check(Read(legacy.ToJsonString(), steam).Snapshot is null,
        "retired legacy camera channel cannot replace a required tongue channel");
    Check(observation.Snapshot!.Channels.Single(channel => channel.Name == "tongue").Applied,
        "real received tongue packet was applied to owned output slots: " + json);
    Check(View(observation, fixture.Time).State == "receiving", "fresh applied output reports receiving");
    Check(View(observation, fixture.Time + 301).State == "observed", "expired captured packet lease becomes neutral last-observed status, without a false receiving or stalled-source claim");
    Check(View(observation, fixture.Time + 3501).State == "stale", "stalled module loop expires independently of process presence");
    Check(View(observation, fixture.Time - 1).State == "stale", "future heartbeat cannot be considered fresh");
    Check(Read(json, !steam).Snapshot is null, "other streaming source cannot prove selected source output");
    Check(HubModuleHeartbeat.Parse(json, steam, new string('B', 64), (_, _) => true).Snapshot is null, "old DLL hash cannot prove current module load");
    Check(Read(json, steam, (_, _) => false).Snapshot is null, "closed or reused PID cannot prove loaded module");
    Check(View(observation, fixture.Time, running: false).State == "closed", "closed VRCFT host overrides a cached fresh observation");
    Check(View(observation, fixture.Time, current: false).State == "unverified", "installation conflict overrides received output");
    Check(Read(json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"), steam).Snapshot is null, "duplicate protocol fields rejected");
    Check(Read(json.Replace("\"AgeMs\":0", "\"AgeMs\":-2"), steam).Snapshot is null, "invalid ages rejected");
    Check(Read(json.Replace("\"TimeoutMs\":300", "\"TimeoutMs\":999999"), steam).Snapshot is null, "untrusted timeout cannot extend output freshness");
    fixture.Time += 1000;
    fixture.Module.Update();
    observation = Read(await fixture.Latest(), steam);
    Check(!observation.Snapshot!.Channels.Single(channel => channel.Name == "tongue").Applied,
        "production module no longer advertises applied custom tongue after timeout");
    Check(View(observation, fixture.Time).State == "stale", "new production observation of expired received packets reports stale output");
    fixture.Time += 1000;
    fixture.Module.Status = ModuleState.Idle;
    fixture.Module.Update();
    observation = Read(await fixture.Latest(), steam);
    Check(View(observation, fixture.Time).State == "loaded" && observation.Snapshot!.Channels.All(channel => !channel.Applied),
        "inactive module remains loaded but cannot prove live output");
    fixture.Module.Teardown();
    observation = Read(await fixture.Latest(), steam);
    Check(View(observation, fixture.Time).State == "closed", "production teardown publishes closed for both sources");
}

using (var fixture = new ModuleFixture(false))
{
    fixture.Set("_needsExpression", false);
    fixture.SendTongue();
    fixture.Module.Update();
    var observation = Read(await fixture.Latest());
    Check(observation.Snapshot!.Channels.Single(channel => channel.Name == "tongue").AgeMs == 0 &&
        !observation.Snapshot.Channels.Single(channel => channel.Name == "tongue").Applied,
        "receiving a packet without owning its expression slot does not prove output");
    fixture.Time += 1000;
    fixture.Native();
    fixture.Module.Update();
    observation = Read(await fixture.Latest());
    Check(observation.Snapshot!.NativeState == "untimed" && View(observation, fixture.Time).State == "untimed",
        "VD native availability is explicit and never mistaken for timestamped freshness");
}
using (var fixture = new ModuleFixture(true))
{
    fixture.Steam!.ParseDatagram(Message("/sl/xrfb/facew/CheekPuffL", .3f), fixture.Time);
    fixture.Steam.ParseDatagram(Message("/sl/xrfb/facec/LowerFace", 1), fixture.Time);
    fixture.Module.Update();
    var observation = Read(await fixture.Latest(), true);
    Check(observation.Snapshot!.NativeState == "fresh" && View(observation, fixture.Time).State == "receiving",
        "Steam native face data with fresh capability proves local receiving");
    Check(View(observation, fixture.Time + 501).State == "observed", "Steam native output becomes last observed when current freshness is unknown between snapshots");
    fixture.Time += 1000;
    fixture.Module.Update();
    observation = Read(await fixture.Latest(), true);
    Check(observation.Snapshot!.NativeState == "stale" && View(observation, fixture.Time).State == "stale", "real Steam module reports stale native output after source stalls");
}
Check(View(HubModuleHeartbeatObservation.Missing("old module"), 1000).State == "unverified",
    "backwards-compatible module without heartbeat is unverified, not receiving");
Check(Read("{}", false).Snapshot is null && Read(new string('x', 17000)).Snapshot is null,
    "missing and oversize records fail closed");
using (var process = Process.GetCurrentProcess())
{
    var start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
    Check(HubModuleHeartbeat.ProcessMatches(process.Id, start) && !HubModuleHeartbeat.ProcessMatches(process.Id, start + 1),
        "real process probe matches start identity and rejects PID reuse");
    Check(!HubModuleHeartbeat.ProcessMatches(int.MaxValue, start), "nonexistent process is closed");
    var path = Path.Combine(Path.GetTempPath(), "qpro-heartbeat-test-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var dll = typeof(TrackingModule).Assembly.Location;
        using var stream = File.OpenRead(dll);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var publisher = new ModuleHeartbeatPublisher(path, false, hash, process.Id, start);
        publisher.Publish(Environment.TickCount64, "active", "untimed", -1, true, true, Channels(false));
        await publisher.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var observation = HubModuleHeartbeat.Read(path, false, dll);
        Check(observation.Snapshot?.State == "active", "atomic disk writer and actual bounded Hub reader agree on the complete record");
        Check(HubModuleHeartbeat.Read(path, true, dll).Snapshot is null, "disk observation from the other source stays unverified");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Any(),
            "successful atomic publishing leaves no temporary status files");
        var failing = new ModuleHeartbeatPublisher(path, false, hash, process.Id, start, (_, _) => throw new IOException("offline fixture disk failure"));
        failing.Publish(Environment.TickCount64, "active", "unavailable", -1, true, true, Channels(false));
        await failing.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(View(observation, observation.Snapshot!.CapturedTickMs + 3501).State == "stale", "failed writes cannot renew the prior snapshot's freshness");
    }
    finally { File.Delete(path); }
}
Console.WriteLine($"PASS: {checks} module-heartbeat protocol, stale/nonmatching/closed, bounded-writer and production-source checks.");

static HeartbeatChannel[] Channels(bool applied) =>
 [new("gaze-left", -1, 250, false), new("gaze-right", -1, 250, false), new("tongue", applied ? 0 : -1, 300, applied),
  new("pupil-left", -1, 350, false), new("pupil-right", -1, 350, false)];
static byte[] Message(string address, float value)
{
    using var output = new MemoryStream();
    foreach (var text in new[] { address, ",f" })
    {
        var chars = Encoding.ASCII.GetBytes(text); output.Write(chars); output.WriteByte(0);
        for (var length = chars.Length + 1; length % 4 != 0; length++) output.WriteByte(0);
    }
    Span<byte> bits = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(bits, BitConverter.SingleToInt32Bits(value)); output.Write(bits);
    return output.ToArray();
}

sealed class ModuleFixture : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly UdpClient _tongue = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _sender = new(AddressFamily.InterNetwork);
    private readonly ConcurrentQueue<string> _writes = new();
    private readonly ModuleHeartbeatPublisher _publisher;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    internal long Time = Environment.TickCount64;
    internal TrackingModule Module { get; }
    internal SteamOscSource? Steam { get; }
    internal ModuleFixture(bool steam)
    {
        _publisher = new("unused", steam, new string('A', 64), 123, 456, (_, json) => _writes.Enqueue(json));
        Module = new(() => Time, "QproHeartbeatTest." + Guid.NewGuid().ToString("N"));
        for (Type? type = Module.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (typeof(ILogger).IsAssignableFrom(field.FieldType)) field.SetValue(Module, NullLogger.Instance);
            foreach (var property in type.GetProperties(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (property.CanWrite && typeof(ILogger).IsAssignableFrom(property.PropertyType)) property.SetValue(Module, NullLogger.Instance);
        }
        _tongue.Client.Blocking = false;
        Set("_heartbeat", _publisher); Set("_tongueSocket", _tongue); Set("_wasActive", true);
        Set("_needsEye", true); Set("_needsExpression", true); Set("_useSteamLink", steam);
        foreach (var field in new[] { "_nextCheekSessionCheckTick", "_nextCheekPuffModeCheckTick", "_nextCheekSuckModeCheckTick", "_nextEyebrowSettingsCheckTick" }) Set(field, long.MaxValue);
        if (steam) { Steam = new(0); Set("_steamSource", Steam); }
        Module.Status = ModuleState.Active;
    }
    internal void Set(string name, object value) => typeof(TrackingModule).GetField(name, Private)!.SetValue(Module, value);
    internal async Task<string> Latest() { await _publisher.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)); return _writes.Last(); }
    internal void SendTongue()
    {
        var packet = new byte[56]; "QPTO"u8.CopyTo(packet); packet[4] = packet[5] = 1;
        for (var index = 0; index < 12; index++) BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8 + index * 4, 4), BitConverter.SingleToInt32Bits(.5f));
        _sender.Send(packet, packet.Length, (IPEndPoint)_tongue.Client.LocalEndPoint!);
        if (!_tongue.Client.Poll(1_000_000, SelectMode.SelectRead)) throw new Exception("Offline loopback packet did not arrive.");
    }
    internal void Native()
    {
        _map = MemoryMappedFile.CreateNew(null, 360); _view = _map.CreateViewAccessor();
        var bytes = new byte[360]; bytes[0] = 3; bytes[1] = bytes[292] = bytes[293] = 1;
        _view.WriteArray(0, bytes, 0, bytes.Length); Set("_map", _map); Set("_view", _view);
    }
    public void Dispose() { Module.Teardown(); _sender.Dispose(); _view?.Dispose(); _map?.Dispose(); }
}
