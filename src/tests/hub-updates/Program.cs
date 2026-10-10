using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
void Reject(Action action, string message)
{
    try { action(); throw new Exception("Accepted " + message); }
    catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException) { passed++; }
}
async Task RejectAsync(Func<Task> action, string message)
{
    try { await action(); throw new Exception("Accepted " + message); }
    catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException) { passed++; }
}
var current = new Version(2, 1, 2);
const string assetName = "QproFaceTracking.V2.1.3.zip";
const string assetUrl = "https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/download/v2.1.3/" + assetName;
JsonObject Metadata(string? digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string tag = "v2.1.3") => new()
{
    ["tag_name"] = tag, ["name"] = "QproFaceTracking V2.1.3", ["body"] = "Release notes", ["draft"] = false, ["prerelease"] = false,
    ["assets"] = new JsonArray(new JsonObject { ["name"] = assetName, ["state"] = "uploaded", ["size"] = 200,
        ["browser_download_url"] = assetUrl, ["digest"] = digest })
};
HubUpdateCheck Parse(JsonObject metadata) => HubUpdateReleaseParser.Parse(metadata.ToJsonString(), current);
Check(Parse(Metadata()).State == HubUpdateCheckState.Available, "verified stable release");
Check(Parse(Metadata(null)).State == HubUpdateCheckState.ManualDownloadOnly, "missing digest fallback");
Check(Parse(Metadata("md5:abc")).State == HubUpdateCheckState.ManualDownloadOnly, "unknown digest fallback");
Check(Parse(Metadata(tag: "v2.1.2")).State == HubUpdateCheckState.UpToDate, "same version");
Check(Parse(Metadata(tag: "v2.1.1")).State == HubUpdateCheckState.UpToDate, "downgrade");
Check(Parse(Metadata(tag: "v2.2.0-rc1")).State == HubUpdateCheckState.UpToDate, "prerelease tag");
JsonObject prerelease = Metadata(); prerelease["prerelease"] = true;
Check(Parse(prerelease).State == HubUpdateCheckState.UpToDate, "prerelease flag");
JsonObject draft = Metadata(); draft["draft"] = true;
Check(Parse(draft).State == HubUpdateCheckState.UpToDate, "draft flag");
JsonObject ambiguous = Metadata(); ((JsonArray)ambiguous["assets"]!).Add(ambiguous["assets"]![0]!.DeepClone());
Check(Parse(ambiguous).State == HubUpdateCheckState.ManualDownloadOnly, "ambiguous assets");
JsonObject testAsset = Metadata(); testAsset["assets"]![0]!["name"] = "QproFaceTracking.V2.1.3.Test.zip";
Check(Parse(testAsset).State == HubUpdateCheckState.ManualDownloadOnly, "test asset excluded");
JsonObject foreign = Metadata(); foreign["assets"]![0]!["browser_download_url"] = "https://example.com/app.zip";
Check(Parse(foreign).State == HubUpdateCheckState.ManualDownloadOnly, "foreign download rejected");
JsonObject oversize = Metadata(); oversize["assets"]![0]!["size"] = HubUpdateReleaseParser.MaximumArchiveBytes + 1;
Check(Parse(oversize).State == HubUpdateCheckState.ManualDownloadOnly, "large archive rejected");
Reject(() => HubUpdateReleaseParser.Parse(Metadata().ToJsonString().Replace("\"draft\":false", "\"draft\":false,\"draft\":false"), current), "duplicate release fields");
Check(!HubUpdateReleaseParser.TryStableVersion("v02.1.3", out _), "leading zero version");
Check(!HubUpdateReleaseParser.TryStableVersion("2.1.3.4", out _), "four component version");
Check(!HubUpdateReleaseParser.IsAssetUrl(new Uri(assetUrl + "?token=secret"), "v2.1.3", assetName), "signed initial URL rejected");

JsonObject ChannelMetadata(string tag, bool isPrerelease = false)
{
    JsonObject metadata = Metadata(tag: tag);
    string version = tag.TrimStart('v', 'V');
    string name = "QproFaceTracking.V" + version + ".zip";
    metadata["name"] = "QproFaceTracking V" + version;
    metadata["prerelease"] = isPrerelease;
    metadata["assets"]![0]!["name"] = name;
    metadata["assets"]![0]!["browser_download_url"] =
        "https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/download/" + tag + "/" + name;
    return metadata;
}
HubUpdateCheck ParseList(bool includePrereleases, params JsonNode?[] releases) =>
    HubUpdateReleaseParser.ParseReleases(new JsonArray(releases).ToJsonString(), current, includePrereleases);
Check(ParseList(false, ChannelMetadata("v2.1.4", true), ChannelMetadata("v2.1.3")).Release?.Version == new Version(2, 1, 3),
    "list stable channel ignores prerelease");
HubUpdateCheck optedIn = ParseList(true, ChannelMetadata("v2.1.3"), ChannelMetadata("v2.1.4", true));
Check(optedIn.State == HubUpdateCheckState.Available && optedIn.Release?.IsPrerelease == true &&
    optedIn.Release.Version == new Version(2, 1, 4) && optedIn.Message.Contains("prerelease"), "opted-in newer prerelease");
Check(ParseList(true, ChannelMetadata("v2.9.0", true), ChannelMetadata("v2.11.0", true),
    ChannelMetadata("v2.3.0")).Release?.Version == new Version(2, 11, 0), "numeric ordering, not list or lexical order");
Check(ParseList(true, ChannelMetadata("v2.1.4", true), ChannelMetadata("v2.1.4")).Release?.IsPrerelease == false,
    "stable wins equal numeric version after prerelease");
Check(ParseList(true, ChannelMetadata("v2.1.4"), ChannelMetadata("v2.1.4", true)).Release?.IsPrerelease == false,
    "stable wins equal numeric version before prerelease");
JsonObject newestDraft = ChannelMetadata("v9.0.0", true); newestDraft["draft"] = true;
Check(ParseList(true, newestDraft, ChannelMetadata("v2.1.3")).Release?.Version == new Version(2, 1, 3), "draft never eligible");
JsonObject malformed = ChannelMetadata("v9.0.0", true); malformed["assets"] = null;
Check(ParseList(true, malformed, JsonValue.Create("invalid entry"), ChannelMetadata("v2.1.3")).Release?.Version == new Version(2, 1, 3),
    "malformed entries cannot hide valid stable release");
string duplicateList = "[" + Metadata().ToJsonString().Replace("\"draft\":false", "\"draft\":false,\"draft\":false") +
    "," + ChannelMetadata("v2.1.4").ToJsonString() + "]";
Check(HubUpdateReleaseParser.ParseReleases(duplicateList, current, true).Release?.Version == new Version(2, 1, 4),
    "duplicate-field entry skipped independently");
Check(ParseList(true, ChannelMetadata("v2.1.2", true), ChannelMetadata("v2.1.1")).State == HubUpdateCheckState.UpToDate,
    "same-version hotfix tag is not offered as a newer update");
HubUpdateCheck suffixRelease = ParseList(true, ChannelMetadata("v2.2.0-rc.1", true), ChannelMetadata("v2.1.3"));
Check(suffixRelease.State == HubUpdateCheckState.ManualDownloadOnly && suffixRelease.Release?.Tag == "v2.2.0-rc.1" &&
    suffixRelease.Release.CanInstall == false && suffixRelease.Release.AssetName is null && suffixRelease.Release.DownloadUrl is null &&
    suffixRelease.Release.Sha256 is null && suffixRelease.Release.ReleasePage.Host == "github.com",
    "newer suffix tag points to manual release without relaxing package identity");
Check(ParseList(false, ChannelMetadata("v2.2.0-rc.1", true), ChannelMetadata("v2.1.3")).Release?.Version == new Version(2, 1, 3),
    "suffix prerelease still excluded by default");
Check(ParseList(true, ChannelMetadata("v2.2.0-beta.2", true), ChannelMetadata("v2.2.0-beta.10", true)).Release?.Tag == "v2.2.0-beta.10",
    "suffix numeric identifiers compare numerically");
Check(ParseList(true, ChannelMetadata("v2.2.0-beta.10", true), ChannelMetadata("v2.2.0-rc.1", true)).Release?.Tag == "v2.2.0-rc.1",
    "suffix text identifiers compare by semantic version precedence");
Check(ParseList(true, ChannelMetadata("v2.2.0-beta.1+build.20", true)).Release?.PrereleaseSuffix == "beta.1",
    "suffix build metadata does not alter semantic precedence");
Check(ParseList(true, ChannelMetadata("v2.2.0-beta.01", true), ChannelMetadata("v2.1.3")).Release?.Version == new Version(2, 1, 3),
    "invalid leading-zero suffix skipped");
Check(ParseList(true, ChannelMetadata("v2.2.0-beta.1", true), ChannelMetadata("v2.2.0")).Release?.IsPrerelease == false,
    "stable same-base release preferred over manual beta");
Check(ParseList(true, ChannelMetadata("v2.1.2-beta.1", true)).State == HubUpdateCheckState.UpToDate,
    "same-base suffix is not offered as a newer version");
Check(ParseList(true).Message.Contains("compatible") && ParseList(true).Message.Contains("manual download"),
    "empty compatible channel does not claim suffix releases are absent");
Check(ParseList(true).State == HubUpdateCheckState.UpToDate, "empty release list");
Check(ParseList(true, malformed.DeepClone()).State == HubUpdateCheckState.Unavailable, "invalid-only response is not called up to date");
JsonObject uncheckedBeta = ChannelMetadata("v2.1.4", true); uncheckedBeta["assets"]![0]!["digest"] = null;
HubUpdateCheck uncheckedResult = ParseList(true, uncheckedBeta, ChannelMetadata("v2.1.3"));
Check(uncheckedResult.State == HubUpdateCheckState.ManualDownloadOnly && uncheckedResult.Release?.CanInstall == false,
    "prerelease still requires GitHub checksum");
JsonObject foreignBeta = ChannelMetadata("v2.1.4", true); foreignBeta["assets"]![0]!["browser_download_url"] = "https://example.com/beta.zip";
Check(ParseList(true, foreignBeta).Release?.CanInstall == false, "prerelease foreign URL cannot install");
Reject(() => HubUpdateReleaseParser.ParseReleases("{}", current, true), "non-array release list");
Reject(() => HubUpdateReleaseParser.ParseReleases(new JsonArray(Enumerable.Range(0,
    HubUpdateReleaseParser.MaximumReleaseListEntries + 1).Select(_ => (JsonNode?)ChannelMetadata("v2.1.3")).ToArray()).ToJsonString(), current, true),
    "release count limit");

string fixtureParent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "hub-update-fixtures"));
string fixture = Path.Combine(fixtureParent, "qpro-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    string oldRoot = Path.Combine(fixture, "old", "QproRuntime");
    Directory.CreateDirectory(Path.Combine(oldRoot, "models"));
    File.WriteAllText(Path.Combine(oldRoot, "models", "personal.pt"), "Keep this model");
    Directory.CreateDirectory(Path.Combine(oldRoot, "captures"));
    File.WriteAllText(Path.Combine(oldRoot, "captures", "private.qpcap"), "Keep this recording");
    string storage = Path.Combine(fixture, "updates");
    var version = new Version(2, 1, 3);
    Dictionary<string, byte[]> ValidFiles(int? format = 1, string manifestVersion = "2.1.3")
    {
        var manifest = new JsonObject { ["name"] = "QproFaceTracking", ["version"] = manifestVersion };
        if (format is not null) manifest["updateFormat"] = format;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["QproFaceTracking.exe"] = Encoding.UTF8.GetBytes("Offline fixture; never executable"),
            ["QproRuntime/release-manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
        };
        foreach (string path in new[] { "setup-runtime.ps1", "runtime-python.ps1", "receiver.py", "tongue_model_preview.py",
                     "platform-tools/adb.exe", "vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll",
                     "models/qpro-stereo-tongue-v8-gate.pt", "models/qpro-stereo-tongue-v8-direction.pt" })
            files["QproRuntime/" + path] = Encoding.UTF8.GetBytes("Fixture " + path);
        return files;
    }
    string Archive(Dictionary<string, byte[]> files, string? extra = null, bool bom = false, Action<ZipArchive>? modify = null)
    {
        string path = Path.Combine(fixture, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        const string folder = "QproFaceTracking V2.1.3/";
        foreach ((string name, byte[] bytes) in files)
        {
            using Stream output = archive.CreateEntry(folder + name).Open();
            output.Write(bytes);
        }
        string sums = string.Join('\n', files.Select(file => Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant() + "  " + file.Key));
        using (var writer = new StreamWriter(archive.CreateEntry(folder + "SHA256SUMS.txt").Open(), new UTF8Encoding(bom))) writer.Write(sums);
        if (extra is not null) { using Stream output = archive.CreateEntry(extra).Open(); output.WriteByte(1); }
        modify?.Invoke(archive);
        return path;
    }
    HubUpdateRelease Release(string archive) => new(version, "v2.1.3", "QproFaceTracking V2.1.3", new Uri("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/v2.1.3"),
        "Notes", assetName, new Uri(assetUrl), new FileInfo(archive).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant());
    HubStagedUpdate Stage(string archive, CancellationToken cancellation = default) => HubUpdatePackage.Stage(archive, Release(archive), oldRoot, storage, null, cancellation);
    string good = Archive(ValidFiles(), bom: true);
    HubStagedUpdate staged = Stage(good);
    Check(File.Exists(staged.ExecutablePath), "staged executable exists");
    Check(File.Exists(Path.Combine(staged.RuntimeRoot, "release-manifest.json")), "staged manifest exists");
    Check(!Directory.Exists(Path.Combine(staged.RuntimeRoot, "captures")), "private captures not copied");
    Check(!File.Exists(Path.Combine(staged.RuntimeRoot, "models", "personal.pt")), "personal model unchanged in old copy");
    Check(File.ReadAllText(Path.Combine(oldRoot, "captures", "private.qpcap")) == "Keep this recording", "old recording unchanged");
    Check(File.ReadAllText(Path.Combine(oldRoot, "models", "personal.pt")) == "Keep this model", "old model unchanged");
    Check(staged.ModelCount == 0 && staged.PreviousRoot == oldRoot, "staging performs no migration");
    HubUpdatePackage.ValidateStagedRelease(staged.RuntimeRoot, version);
    Check(true, "helper can revalidate a staged package");
    File.AppendAllText(staged.ExecutablePath, "tampering");
    Reject(() => HubUpdatePackage.ValidateStagedRelease(staged.RuntimeRoot, version), "staged executable tampering");
    HubUpdatePackage.Discard(staged, storage);
    Check(!File.Exists(staged.ExecutablePath), "discard private stage");
    Reject(() => HubUpdatePackage.Discard(staged with { RuntimeRoot = oldRoot }, storage), "discard old app folder");
    foreach (string bad in new[] { "../escape.txt", "QproFaceTracking V2.1.3/../../escape.txt", "QproFaceTracking V2.1.3/C:/escape.txt",
        "QproFaceTracking V2.1.3/CON.txt", "QproFaceTracking V2.1.3/file.txt:stream", "QproFaceTracking V2.1.3/name. ",
        "QproFaceTracking V2.1.3/QproRuntime\\escape.txt", "OtherApp/file.txt", "QproFaceTracking V2.1.3/QPROFACETRACKING.EXE" })
        Reject(() => Stage(Archive(ValidFiles(), extra: bad)), "unsafe ZIP entry " + bad);
    Reject(() => Stage(Archive(ValidFiles(), modify: archive =>
    { ZipArchiveEntry link = archive.CreateEntry("QproFaceTracking V2.1.3/link"); link.ExternalAttributes = 0xA000 << 16; })), "symlink");
    Reject(() => Stage(Archive(ValidFiles(), modify: archive =>
    { ZipArchiveEntry link = archive.CreateEntry("QproFaceTracking V2.1.3/link"); link.ExternalAttributes = (int)FileAttributes.ReparsePoint; })), "reparse entry");
    Reject(() => Stage(Archive(ValidFiles(format: null))), "missing updater format");
    Reject(() => Stage(Archive(ValidFiles(format: 2))), "unknown updater format");
    Reject(() => Stage(Archive(ValidFiles(manifestVersion: "2.1.4"))), "manifest release mismatch");
    Dictionary<string, byte[]> missing = ValidFiles(); missing.Remove("QproRuntime/receiver.py");
    Reject(() => Stage(Archive(missing)), "runtime incomplete");
    Reject(() => Stage(Archive(ValidFiles(), extra: "QproFaceTracking V2.1.3/unlisted.txt")), "unlisted file");
    Reject(() => Stage(Archive(ValidFiles()), new CancellationToken(true)), "canceled extraction");
    Check(!Directory.GetDirectories(storage, ".qpro-stage-*").Any(), "rejected stages cleaned");
    Check(!File.Exists(Path.Combine(fixture, "escape.txt")), "no traversal file created");

    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Metadata().ToJsonString()) })))
        Check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Available, "fake HTTP release check");
    using (var client = new HubUpdateClient(new FixtureHandler(request =>
    {
        Check(request.RequestUri == HubUpdateReleaseParser.LatestApi, "default check still uses stable latest endpoint");
        Check(request.Headers.Authorization is null, "public update check sends no credentials");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Metadata().ToJsonString()) };
    })))
        Check((await client.CheckAsync(current, default)).Release?.IsPrerelease == false, "default HTTP channel remains stable");
    using (var client = new HubUpdateClient(new FixtureHandler(request =>
    {
        Check(request.RequestUri == HubUpdateReleaseParser.ReleasesApi, "opted-in check uses bounded release-list endpoint");
        Check(request.Headers.Authorization is null, "prerelease check sends no credentials");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonArray(
            ChannelMetadata("v2.1.3"), ChannelMetadata("v2.1.4", true)).ToJsonString()) };
    })))
        Check((await client.CheckAsync(current, default, includePrereleases: true)).Release?.IsPrerelease == true,
            "fake HTTP opted-in release check");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[HubUpdateReleaseParser.MaximumMetadataBytes + 1]) })))
        Check((await client.CheckAsync(current, default, true)).State == HubUpdateCheckState.Unavailable, "prerelease metadata size limit");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{}") })))
        Check((await client.CheckAsync(current, default, true)).State == HubUpdateCheckState.Unavailable, "malformed list result");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
        Check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable, "rate limit result");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        Check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.UpToDate, "no release yet");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        Check((await client.CheckAsync(current, default, true)).Message.Contains("prerelease"), "no prerelease yet channel guidance");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(File.ReadAllBytes(good)) })))
    {
        HubStagedUpdate download = await client.StageAsync(Release(good), oldRoot, storage, null, default);
        Check(File.Exists(download.ExecutablePath), "verified fake HTTP download and extraction");
        HubUpdatePackage.Discard(download, storage);
    }
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(File.ReadAllBytes(good)) })))
        await RejectAsync(async () => await client.StageAsync(Release(good) with { Sha256 = new string('0', 64) }, oldRoot, storage, null, default), "digest mismatch");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(File.ReadAllBytes(good)[..100]) })))
        await RejectAsync(async () => await client.StageAsync(Release(good), oldRoot, storage, null, default), "short download");
    using (var client = new HubUpdateClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(File.ReadAllBytes(good)) })))
        await RejectAsync(async () => await client.StageAsync(Release(good), oldRoot, storage, null, new CancellationToken(true)), "canceled download");
    using (var client = new HubUpdateClient(new FixtureHandler(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri("https://example.com/update.zip");
        return response;
    })))
        await RejectAsync(async () => await client.StageAsync(Release(good), oldRoot, storage, null, default), "foreign redirect");
    Check(!Directory.GetFiles(storage, ".qpro-download-*").Any(), "download cleanup");
    Console.WriteLine($"Hub updater tests passed: {passed}. No network, installed apps or real updates were used.");
}
finally
{
    string path = Path.GetFullPath(fixture);
    if (!path.StartsWith(fixtureParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(path).StartsWith("qpro-update-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup.");
    Directory.Delete(path, true);
}

sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
