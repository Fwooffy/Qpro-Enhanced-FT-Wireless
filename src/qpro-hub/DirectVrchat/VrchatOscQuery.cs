using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed record VrchatOscTarget(IPEndPoint Endpoint, IReadOnlyDictionary<string, string> Inputs)
{
    internal bool NativeFace { get; init; }
    internal string? AvatarId { get; init; }
    internal VrchatOscService? Service { get; init; }
    internal IReadOnlySet<string> BlockedInputs { get; init; } = FrozenSet<string>.Empty;
}

internal sealed record VrchatOscService(string Name, IPEndPoint QueryEndpoint);

internal static class VrchatOscQuery
{
    private const string ClientPrefix = "VRChat-Client-";
    private const string NativeFaceStatus = "/avatar/avatarfullfacetracking";
    private const string NativeFaceInput = "/tracking/face/v1";
    private const string AvatarChange = "/avatar/change";
    private const int HostInfoLimit = 16 * 1024;
    private const int NamespaceLimit = 2 * 1024 * 1024;
    private static readonly IPEndPoint FallbackQueryEndpoint = new(IPAddress.Loopback, 9001);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(1200);
    private static VrchatOscService? _lastService;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        MaxResponseHeadersLength = 16,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
    });

    internal static Task<VrchatOscTarget?> DiscoverAsync(CancellationToken token) =>
        DiscoverAsync(Client, VrchatOscMdns.BrowseAsync, token);

    internal static async Task<VrchatOscTarget?> DiscoverAsync(HttpClient client,
        Func<CancellationToken, Task<IReadOnlyList<VrchatOscService>>> browse, CancellationToken token)
    {
        // A handover can reuse the HTTP service location. Always reread HOST_INFO and
        // the current namespace before sending to any UDP port or avatar parameter.
        var hint = Volatile.Read(ref _lastService);
        if (hint is not null)
        {
            var current = await ReadAsync(client, hint, token).ConfigureAwait(false);
            if (current is not null) return current;
            Interlocked.CompareExchange(ref _lastService, null, hint);
        }
        var services = await browse(token).ConfigureAwait(false);
        var target = await DiscoverAsync(client, services, token).ConfigureAwait(false);
        if (target?.Service is { } verified) Volatile.Write(ref _lastService, verified);
        return target;
    }

    internal static async Task<VrchatOscTarget?> DiscoverAsync(HttpClient client, IReadOnlyList<VrchatOscService> services,
        CancellationToken token)
    {
        var candidates = services.Where(service => IsVrchatClientName(service.Name))
            .GroupBy(service => service.QueryEndpoint).ToArray();
        var results = await Task.WhenAll(candidates.Select(group =>
            ReadAsync(client, group.Key, group.Select(service => service.Name).ToArray(), token))).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var target = results.FirstOrDefault(result => result is not null);
        if (target is not null || candidates.Any(group => group.Key.Equals(FallbackQueryEndpoint)))
            return target;

        // Some VRChat versions expose OSCQuery on this documented TCP port even when mDNS is unavailable.
        return await ReadAsync(client, FallbackQueryEndpoint, expectedNames: null, token).ConfigureAwait(false);
    }

    internal static Task<VrchatOscTarget?> RefreshAsync(VrchatOscTarget target, CancellationToken token) =>
        target.Service is { } service ? ReadAsync(Client, service, token) : Task.FromResult<VrchatOscTarget?>(null);

    internal static bool IsVrchatClientName(string name) =>
        name.Length > ClientPrefix.Length && name.Length <= 63
        && name.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase)
        && name[ClientPrefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    internal static Task<VrchatOscTarget?> ReadAsync(HttpClient client, VrchatOscService service, CancellationToken token) =>
        ReadAsync(client, service.QueryEndpoint, [service.Name], token);

    private static async Task<VrchatOscTarget?> ReadAsync(HttpClient client, IPEndPoint queryEndpoint,
        IReadOnlyList<string>? expectedNames, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsLoopback(queryEndpoint.Address) || queryEndpoint.Port == 0
            || expectedNames is not null && !expectedNames.Any(IsVrchatClientName))
            return null;

        var origin = new UriBuilder(Uri.UriSchemeHttp, queryEndpoint.Address.ToString(), queryEndpoint.Port).Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            using var info = await GetJsonAsync(client, new Uri(origin, "/?HOST_INFO"), HostInfoLimit, timeout.Token)
                .ConfigureAwait(false);
            if (info is null || ReadHostInfo(info.RootElement, queryEndpoint.Address, expectedNames) is not { } host)
                return null;

            using var tree = await GetJsonAsync(client, origin, NamespaceLimit, timeout.Token).ConfigureAwait(false);
            if (tree is null || !HasFullPath(tree.RootElement, "/"))
                return null;

            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            var blockedInputs = new HashSet<string>(StringComparer.Ordinal);
            bool? native = null;
            string? avatarId = null;
            ReadNode(tree.RootElement, "/", inputs, blockedInputs, ref native, ref avatarId);
            if (native is null)
                native = await ReadNativeFaceStatusAsync(client, origin, timeout.Token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            return new VrchatOscTarget(host.Endpoint, new ReadOnlyDictionary<string, string>(inputs))
            {
                NativeFace = native == true && !blockedInputs.Contains(NativeFaceInput)
                    && (!inputs.TryGetValue(NativeFaceInput, out string? faceType) || faceType == "b"),
                AvatarId = avatarId,
                Service = new VrchatOscService(host.Name, queryEndpoint),
                BlockedInputs = blockedInputs.ToFrozenSet(StringComparer.Ordinal),
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<bool?> ReadNativeFaceStatusAsync(HttpClient client, Uri origin, CancellationToken token)
    {
        // VRChat also exposes this status as an individual OSCQuery node. Older clients return 404.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            using var response = await GetJsonAsync(client, new Uri(origin, NativeFaceStatus), HostInfoLimit, timeout.Token)
                .ConfigureAwait(false);
            return response is not null && HasFullPath(response.RootElement, NativeFaceStatus)
                ? ReadBooleanValue(response.RootElement)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, Uri uri, int limit, CancellationToken token)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
            return null;

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)body.Length)), token)
                .ConfigureAwait(false);
            if (count == 0)
                return JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 48 });
            if (body.Length + count > limit)
                return null;
            body.Write(buffer, 0, count);
        }
    }

    private static (string Name, IPEndPoint Endpoint)? ReadHostInfo(JsonElement root, IPAddress queryAddress,
        IReadOnlyList<string>? expectedNames)
    {
        if (root.ValueKind != JsonValueKind.Object
            || GetString(root, "NAME") is not { } name || !IsVrchatClientName(name)
            || expectedNames is not null && !expectedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || !TryGetInt32(root, "OSC_PORT", out int port) || port is <= 0 or > 65535)
            return null;

        // OSCQuery defaults missing transport to UDP and missing IP to the query host.
        if (root.TryGetProperty("OSC_TRANSPORT", out _)
            && !string.Equals(GetString(root, "OSC_TRANSPORT"), "UDP", StringComparison.OrdinalIgnoreCase))
            return null;

        IPAddress? address = queryAddress;
        if (root.TryGetProperty("OSC_IP", out _)
            && (!IPAddress.TryParse(GetString(root, "OSC_IP"), out address) || !IsLoopback(address)))
            return null;

        return (name, new IPEndPoint(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, port));
    }

    private static void ReadNode(JsonElement node, string path, Dictionary<string, string> inputs,
        HashSet<string> blockedInputs, ref bool? native, ref string? avatarId)
    {
        bool builtInFallback = path is NativeFaceInput or "/tracking/eye/LeftRightPitchYaw" or "/tracking/eye/EyesClosedAmount";
        if (!HasFullPath(node, path))
        {
            if (builtInFallback)
                blockedInputs.Add(path);
            return;
        }

        if (path == NativeFaceStatus)
            native = ReadBooleanValue(node);
        if (path == AvatarChange && GetString(node, "TYPE") == "s" && ReadValue(node) is { ValueKind: JsonValueKind.String } avatarValue)
        {
            string? value = avatarValue.GetString();
            if (value is { Length: > 5 and <= 100 } && value.StartsWith("avtr_", StringComparison.Ordinal)
                && value.AsSpan(5).ContainsAnyExcept("0123456789abcdefABCDEF-".AsSpan()) == false)
                avatarId = value;
        }

        if (path != "/" && TryGetInt32(node, "ACCESS", out int access) && access is 2 or 3
            && GetString(node, "TYPE") is { Length: > 0 and <= 32 } type
            && type.All(c => char.IsAsciiLetter(c) || c is '[' or ']'))
            // OSCQuery treats the boolean tags as interchangeable. A value change does not change the route.
            inputs.TryAdd(path, type == "F" ? "T" : type);
        else if (builtInFallback)
            blockedInputs.Add(path);

        if (!node.TryGetProperty("CONTENTS", out var contents) || contents.ValueKind != JsonValueKind.Object)
            return;
        foreach (var child in contents.EnumerateObject())
        {
            string label = child.Name;
            if (label.Length == 0 || path.Length + label.Length > 512
                || label.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '/' or '#' or '*' or ',' or '?' or '[' or ']' or '{' or '}'))
                continue;
            string childPath = path == "/" ? path + label : path + "/" + label;
            ReadNode(child.Value, childPath, inputs, blockedInputs, ref native, ref avatarId);
        }
    }

    private static bool? ReadBooleanValue(JsonElement node)
    {
        if (GetString(node, "TYPE") is not ("T" or "F"))
            return null;
        return ReadValue(node)?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static JsonElement? ReadValue(JsonElement node)
    {
        if (!TryGetInt32(node, "ACCESS", out int access) || access is not (1 or 3)
            || !node.TryGetProperty("VALUE", out var value))
            return null;

        // OSCQuery permits a scalar shorthand for a single argument.
        if (value.ValueKind != JsonValueKind.Array)
            return value;
        return value.GetArrayLength() == 1 ? value[0] : null;
    }

    private static bool HasFullPath(JsonElement node, string path) =>
        node.ValueKind == JsonValueKind.Object && string.Equals(GetString(node, "FULL_PATH"), path, StringComparison.Ordinal);

    private static string? GetString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetInt32(JsonElement node, string name, out int result)
    {
        result = 0;
        return node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
