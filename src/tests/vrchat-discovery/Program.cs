using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QproFaceTracking.Hub;

const string name = "VRChat-Client-ABCDEF";
const string avatarId = "avtr_01234567-89ab-cdef-0123-456789abcdef";
const string host = """{"NAME":"VRChat-Client-ABCDEF","OSC_IP":"127.0.0.1","OSC_PORT":32001,"OSC_TRANSPORT":"UDP"}""";
const string tree = """
    {"FULL_PATH":"/","ACCESS":0,"CONTENTS":{
      "avatar":{"FULL_PATH":"/avatar","CONTENTS":{
        "change":{"FULL_PATH":"/avatar/change","ACCESS":1,"TYPE":"s","VALUE":["avtr_01234567-89ab-cdef-0123-456789abcdef"]},
        "avatarfullfacetracking":{"FULL_PATH":"/avatar/avatarfullfacetracking","ACCESS":1,"TYPE":"T","VALUE":[true]},
        "parameters":{"FULL_PATH":"/avatar/parameters","CONTENTS":{
          "JawOpen":{"FULL_PATH":"/avatar/parameters/JawOpen","ACCESS":3,"TYPE":"f"},
          "TongueOut":{"FULL_PATH":"/avatar/parameters/TongueOut","ACCESS":2,"TYPE":"T"},
          "EyeOut":{"FULL_PATH":"/avatar/parameters/EyeOut","ACCESS":1,"TYPE":"f"},
          "NoType":{"FULL_PATH":"/avatar/parameters/NoType","ACCESS":2},
          "Alias":{"FULL_PATH":"/avatar/parameters/Elsewhere","ACCESS":2,"TYPE":"f"},
          "WrongAccess":{"FULL_PATH":"/avatar/parameters/WrongAccess","ACCESS":"3","TYPE":"f"},
          "Bad*":{"FULL_PATH":"/avatar/parameters/Bad*","ACCESS":2,"TYPE":"f"}
        }}
      }},
      "tracking":{"FULL_PATH":"/tracking","CONTENTS":{
        "eye":{"FULL_PATH":"/tracking/eye","CONTENTS":{
          "CenterPitchYaw":{"FULL_PATH":"/tracking/eye/CenterPitchYaw","ACCESS":2,"TYPE":"ff"},
          "LeftRightPitchYaw":{"FULL_PATH":"/tracking/eye/LeftRightPitchYaw","ACCESS":1,"TYPE":"ffff"},
          "EyesClosedAmount":{"FULL_PATH":"/tracking/eye/EyesClosedAmount","ACCESS":2,"TYPE":"!"}
        }}
      }}
    }}
    """;

var service = new VrchatOscService(name, new IPEndPoint(IPAddress.Loopback, 31000));
int checks = 0;

void Check(bool success, string description)
{
    if (!success)
        throw new InvalidOperationException(description);
    checks++;
}

using (var http = new HttpClient(new FixtureHandler((uri, _) =>
    Task.FromResult(uri.PathAndQuery switch
    {
        "/?HOST_INFO" => Json(host),
        "/" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamedBody(tree) },
        _ => throw new InvalidOperationException("The root already provided the avatar's tracking status."),
    }))))
{
    var target = await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None);
    Check(target?.Endpoint.Equals(new IPEndPoint(IPAddress.Loopback, 32001)) == true, "Read UDP target from HOST_INFO.");
    Check(target?.Service == service, "Preserve the OSCQuery service for refresh.");
    Check(target?.AvatarId == avatarId, "Read the current avatar ID from an output.");
    Check(target?.NativeFace == true, "Read the native face status from its boolean OSCQuery value.");
    Check(target?.Inputs.Count == 3, "Select only concrete, typed writable addresses under their exact paths.");
    Check(target?.Inputs["/avatar/parameters/JawOpen"] == "f", "Retain writable avatar float type.");
    Check(target?.Inputs["/avatar/parameters/TongueOut"] == "T", "Retain writable avatar boolean type.");
    Check(target?.Inputs["/tracking/eye/CenterPitchYaw"] == "ff", "Retain built-in endpoint and arity.");
    Check(target?.BlockedInputs.SetEquals(["/tracking/eye/LeftRightPitchYaw", "/tracking/eye/EyesClosedAmount"]) == true,
        "Retain explicit read-only and invalid-type evidence for known built-in eye inputs.");
}

using (var http = new HttpClient(new FixtureHandler((uri, _) =>
    Task.FromResult(uri.PathAndQuery switch
    {
        "/?HOST_INFO" => Json(host),
        "/" => Json("""{"FULL_PATH":"/","ACCESS":0}"""),
        "/avatar/avatarfullfacetracking" => Json("""{"FULL_PATH":"/avatar/avatarfullfacetracking","ACCESS":1,"TYPE":"T","VALUE":[true]}"""),
        _ => throw new InvalidOperationException("Unexpected request."),
    }))))
{
    var target = await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None);
    Check(target is { NativeFace: true, AvatarId: null } && target.Inputs.Count == 0,
        "Query native availability directly when omitted from the root.");
}

using (var http = new HttpClient(new FixtureHandler((uri, _) => Task.FromResult(Json(
    uri.PathAndQuery == "/?HOST_INFO" ? host : tree.Replace("[true]", "true").Replace($"[\"{avatarId}\"]", $"\"{avatarId}\""))))))
{
    var target = await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None);
    Check(target is { NativeFace: true, AvatarId: avatarId }, "OSCQuery permits a scalar VALUE for a single argument.");
}

foreach ((string faceNode, bool expectedNative) in new[]
{
    ("""{"FULL_PATH":"/tracking/face/v1","ACCESS":2,"TYPE":"b"}""", true),
    ("""{"FULL_PATH":"/tracking/face/v1","ACCESS":1,"TYPE":"b"}""", false),
    ("""{"FULL_PATH":"/tracking/face/v1","ACCESS":2,"TYPE":"f"}""", false),
})
{
    var withFace = tree.Replace("\"eye\":", "\"face\":{\"FULL_PATH\":\"/tracking/face\",\"CONTENTS\":{\"v1\":" + faceNode + "}},\"eye\":");
    using var http = new HttpClient(new FixtureHandler((uri, _) => Task.FromResult(Json(uri.PathAndQuery == "/?HOST_INFO" ? host : withFace))));
    var target = await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None);
    Check(target?.NativeFace == expectedNative && target.Inputs.ContainsKey("/avatar/parameters/JawOpen"),
        "An advertised native face route must accept the documented blob to take priority over legacy output.");
}

using (var http = new HttpClient(new FixtureHandler((uri, _) => Task.FromResult(Json(uri.PathAndQuery == "/?HOST_INFO"
    ? host : tree.Replace("\"TYPE\":\"T\"", "\"TYPE\":\"F\""))))))
{
    var target = await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None);
    Check(target?.Inputs["/avatar/parameters/TongueOut"] == "T",
        "A boolean flipping between T and F keeps its writable signature stable across refreshes.");
}

foreach (string updatedTree in new[]
{
    tree.Replace("[true]", "[false]"),
    tree.Replace("[true]", "[\"true\"]"),
    tree.Replace("[true]", "[true,false]"),
    tree.Replace("[true]", "[1]"),
    tree.Replace("\"TYPE\":\"T\",\"VALUE\":[true]", "\"TYPE\":\"s\",\"VALUE\":[true]"),
})
{
    using var http = new HttpClient(new FixtureHandler((uri, _) => Task.FromResult(uri.PathAndQuery switch
    {
        "/?HOST_INFO" => Json(host),
        "/" => Json(updatedTree),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    })));
    var target = await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None);
    Check(target is { NativeFace: false }, "Require an actual true boolean from the current avatar.");
}

foreach (string wrongHost in new[]
{
    host.Replace(name, "VRChat-Client-FFFFAA"),
    host.Replace("127.0.0.1", "192.0.2.1"),
    host.Replace("127.0.0.1", "0.0.0.0"),
    host.Replace("\"127.0.0.1\"", "null"),
    host.Replace("UDP", "TCP"),
    host.Replace("\"UDP\"", "null"),
    host.Replace("32001", "65536"),
    host.Replace("32001", "\"32001\""),
    host.Replace(",\"OSC_PORT\":32001", ""),
    "[1]",
})
{
    using var http = new HttpClient(new FixtureHandler((uri, _) =>
    {
        Check(uri.PathAndQuery == "/?HOST_INFO", "Validate destination before reading namespace.");
        return Task.FromResult(Json(wrongHost));
    }));
    Check(await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None) is null, "Reject invalid host identity or UDP destination.");
}

const string defaultHost = """{"NAME":"VRChat-Client-ABCDEF","OSC_PORT":32002}""";
using (var http = new HttpClient(new FixtureHandler((uri, _) =>
{
    Check(uri.IsLoopback && uri.Port == 9001, "Fallback only queries the documented loopback TCP port.");
    return Task.FromResult(Json(uri.PathAndQuery == "/?HOST_INFO" ? defaultHost : tree));
})))
{
    var target = await VrchatOscQuery.DiscoverAsync(http, [], CancellationToken.None);
    Check(target?.Endpoint.Equals(new IPEndPoint(IPAddress.Loopback, 32002)) == true,
        "Resolve the advertised UDP port and OSCQuery IP/transport defaults over the TCP fallback.");
    Check(target is { NativeFace: true, AvatarId: avatarId, Service.Name: name, Service.QueryEndpoint.Port: 9001 },
        "Retain the verified fallback identity and live avatar metadata for refresh.");
}

foreach (string unrelated in new[]
{
    defaultHost.Replace(name, "OtherLocalOscService"),
    defaultHost.Replace(",\"OSC_PORT\":32002", ""),
    "{}",
})
{
    using var http = new HttpClient(new FixtureHandler((uri, _) =>
    {
        Check(uri.Port == 9001 && uri.PathAndQuery == "/?HOST_INFO", "Reject unrelated TCP services before namespace query.");
        return Task.FromResult(Json(unrelated));
    }));
    Check(await VrchatOscQuery.DiscoverAsync(http, [], CancellationToken.None) is null,
        "Fallback needs VRChat HOST_INFO with an explicit UDP port.");
}

using (var http = new HttpClient(new FixtureHandler((uri, _) => Task.FromResult(uri.Port == 31000
    ? Json("{}")
    : Json(uri.PathAndQuery == "/?HOST_INFO" ? defaultHost : tree)))))
{
    var target = await VrchatOscQuery.DiscoverAsync(http, [service], CancellationToken.None);
    Check(target?.Service?.QueryEndpoint.Port == 9001, "Try the TCP fallback when the mDNS candidate fails validation.");
}

int mdnsRequests = 0;
using (var http = new HttpClient(new FixtureHandler((uri, _) =>
{
    Check(uri.Port == 31000, "Skip TCP fallback when mDNS finds a verified VRChat target.");
    mdnsRequests++;
    return Task.FromResult(Json(uri.PathAndQuery == "/?HOST_INFO" ? host : tree));
})))
{
    var staleName = service with { Name = "VRChat-Client-FFFFAA" };
    var target = await VrchatOscQuery.DiscoverAsync(http, [staleName, service], CancellationToken.None);
    Check(target?.Service == service && mdnsRequests == 2,
        "Check each mDNS HTTP endpoint once and match its identity against all advertised names.");
}

{
    string nextId = "avtr_11111111-89ab-cdef-0123-456789abcdef";
    var replacement = new VrchatOscService("VRChat-Client-BBBB", new(IPAddress.Loopback, 31003));
    int stage = 0, browses = 0;
    var requests = new List<string>();
    Task<IReadOnlyList<VrchatOscService>> Browse(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        browses++;
        return Task.FromResult<IReadOnlyList<VrchatOscService>>(stage < 2 ? [service] : [replacement]);
    }
    using var http = new HttpClient(new FixtureHandler((uri, _) =>
    {
        requests.Add($"{uri.Port}{uri.PathAndQuery}");
        string liveHost = stage switch
        {
            0 => host,
            1 => host.Replace("32001", "32002"),
            _ when uri.Port == 31000 => host.Replace(name, "UnrelatedOSC"),
            _ => host.Replace(name, replacement.Name).Replace("32001", "32003"),
        };
        string liveTree = stage == 0 ? tree : tree.Replace(avatarId, nextId).Replace("[true]", "[false]").Replace("JawOpen", "JawLeft");
        return Task.FromResult(Json(uri.PathAndQuery == "/?HOST_INFO" ? liveHost : liveTree));
    }));
    var initial = await VrchatOscQuery.DiscoverAsync(http, Browse, CancellationToken.None);
    Check(initial is { AvatarId: avatarId, NativeFace: true } && browses == 1, "First discovery browses for a current service.");
    stage = 1;
    requests.Clear();
    var moved = await VrchatOscQuery.DiscoverAsync(http, Browse, CancellationToken.None);
    Check(moved is { NativeFace: false, Endpoint.Port: 32002 } && moved.AvatarId == nextId &&
        moved.Inputs.ContainsKey("/avatar/parameters/JawLeft") && !moved.Inputs.ContainsKey("/avatar/parameters/JawOpen"),
        "A remembered HTTP service must supply its current avatar, mode, parameters and UDP port.");
    Check(browses == 1 && requests.SequenceEqual(["31000/?HOST_INFO", "31000/"]),
        "A verified handover rereads HOST_INFO and the namespace without waiting for mDNS.");
    stage = 2;
    requests.Clear();
    var recovered = await VrchatOscQuery.DiscoverAsync(http, Browse, CancellationToken.None);
    Check(browses == 2 && recovered?.Service == replacement && recovered.Endpoint.Port == 32003 &&
        requests.SequenceEqual(["31000/?HOST_INFO", "31003/?HOST_INFO", "31003/"]),
        "A reused HTTP port with a different identity requires fresh service discovery.");
    requests.Clear();
    await VrchatOscQuery.DiscoverAsync(http, Browse, CancellationToken.None);
    Check(browses == 2 && requests.SequenceEqual(["31003/?HOST_INFO", "31003/"]),
        "Successful rediscovery replaces the stale service hint.");
}

int fallbackPortRequests = 0;
using (var http = new HttpClient(new FixtureHandler((uri, _) =>
{
    fallbackPortRequests++;
    Check(uri.Port == 9001 && uri.PathAndQuery == "/?HOST_INFO", "Probe the advertised default TCP port once.");
    return Task.FromResult(Json("{}"));
})))
{
    var defaultService = service with { QueryEndpoint = new IPEndPoint(IPAddress.Loopback, 9001) };
    Check(await VrchatOscQuery.DiscoverAsync(http, [defaultService], CancellationToken.None) is null
        && fallbackPortRequests == 1, "Avoid contacting the same HTTP endpoint a second time as the fallback.");
}

using (var http = new HttpClient(new FixtureHandler((_, _) => throw new InvalidOperationException("Must not contact a non-VRChat or remote service."))))
{
    Check(await VrchatOscQuery.ReadAsync(http, service with { Name = "GenericOSCApp" }, CancellationToken.None) is null,
        "Skip non-VRChat advertisements.");
    Check(await VrchatOscQuery.ReadAsync(http, service with { QueryEndpoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 31000) }, CancellationToken.None) is null,
        "Skip remote HTTP endpoints.");
}

using (var http = new HttpClient(new FixtureHandler((uri, _) => Task.FromResult(uri.PathAndQuery == "/?HOST_INFO"
    ? Json(host)
    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamedBody(new string(' ', 2 * 1024 * 1024 + 1)) }))))
{
    Check(await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None) is null,
        "Bound the actual response size when a namespace sends no length header.");
}

using (var http = new HttpClient(new FixtureHandler(async (_, token) =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    throw new InvalidOperationException("Expected cancellation.");
})))
{
    using var cancel = new CancellationTokenSource(50);
    try
    {
        await VrchatOscQuery.ReadAsync(http, service, cancel.Token);
        throw new InvalidOperationException("Discovery must propagate caller cancellation.");
    }
    catch (OperationCanceledException)
    {
        Check(cancel.IsCancellationRequested, "Propagate external cancellation.");
    }

    Check(await VrchatOscQuery.ReadAsync(http, service, CancellationToken.None) is null, "Expire an unresponsive HTTP service.");
}

using (var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    var dnsEndpoint = (IPEndPoint)dns.Client.LocalEndPoint!;
    var browse = VrchatOscMdns.BrowseAsync(dnsEndpoint, [IPAddress.Loopback], timeout.Token);
    var ptr = await dns.ReceiveAsync(timeout.Token);
    Check(Question(ptr.Buffer) == ("_oscjson._tcp.local", 12), "Browse the OSCQuery mDNS service type by PTR.");
    byte[] unrelated = Reply(ptr.Buffer, 12, [.. DnsName("Generic OSC App")[..^1], 0xC0, 12]);
    await dns.SendAsync(unrelated, ptr.RemoteEndPoint, timeout.Token);
    byte[] loop = Reply(ptr.Buffer, 12, [0xC0, (byte)(ptr.Buffer.Length + 12)]);
    await dns.SendAsync(loop, ptr.RemoteEndPoint, timeout.Token);
    byte[] response = Reply(ptr.Buffer, 12, [.. DnsName(name)[..^1], 0xC0, 12]);
    await dns.SendAsync(response, ptr.RemoteEndPoint, timeout.Token);

    var srv = await dns.ReceiveAsync(timeout.Token);
    Check(Question(srv.Buffer) == ($"{name}._oscjson._tcp.local", 33), "Resolve the specific VRChat instance by SRV.");
    byte[] srvData = [0, 0, 0, 0, 0x79, 0x18, .. DnsName("test-pc.local")];
    await dns.SendAsync(Reply(srv.Buffer, 33, srvData), srv.RemoteEndPoint, timeout.Token);

    var address = await dns.ReceiveAsync(timeout.Token);
    Check(Question(address.Buffer) == ("test-pc.local", 1), "Resolve the advertised SRV hostname by A.");
    await dns.SendAsync(Reply(address.Buffer, 1, [192, 0, 2, 1]), address.RemoteEndPoint, timeout.Token);
    byte[] wrongId = Reply(address.Buffer, 1, [127, 0, 0, 2]);
    wrongId[0] ^= 1;
    await dns.SendAsync(wrongId, address.RemoteEndPoint, timeout.Token);
    await dns.SendAsync(Reply(address.Buffer, 1, [127, 0, 0, 1]), address.RemoteEndPoint, timeout.Token);

    var candidates = await browse;
    Check(candidates is [var candidate] && candidate == service, "Resolve only the matching local VRChat service and dynamic TCP port.");
}

Console.WriteLine($"VRChat OSCQuery discovery passed {checks} checks.");

static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json"),
};

static byte[] DnsName(string name) => name.Split('.')
    .SelectMany(label => new[] { (byte)label.Length }.Concat(Encoding.ASCII.GetBytes(label)))
    .Append((byte)0).ToArray();

static (string Name, int Type) Question(byte[] packet)
{
    int offset = 12;
    var labels = new List<string>();
    while (packet[offset] != 0)
    {
        int length = packet[offset++];
        labels.Add(Encoding.ASCII.GetString(packet, offset, length));
        offset += length;
    }
    return (string.Join('.', labels), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 1)));
}

static byte[] Reply(byte[] question, byte type, byte[] rdata)
{
    byte[] response = [.. question, 0xC0, 12, 0, type, 0, 1, 0, 0, 0, 10, 0, (byte)rdata.Length, .. rdata];
    response[2] = 0x84;
    response[7] = 1;
    return response;
}

sealed class FixtureHandler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        reply(request.RequestUri!, cancellationToken);
}

sealed class StreamedBody(string content) : ByteArrayContent(Encoding.UTF8.GetBytes(content))
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
