using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal enum HubUpdateCheckState { UpToDate, Available, ManualDownloadOnly, Unavailable }

internal sealed record HubUpdateRelease(Version Version, string Tag, string Title, Uri ReleasePage,
    string Notes, string? AssetName, Uri? DownloadUrl, long AssetSize, string? Sha256)
{
    internal bool CanInstall => AssetName is not null && DownloadUrl is not null && Sha256 is not null;
}

internal sealed record HubUpdateCheck(HubUpdateCheckState State, string Message, HubUpdateRelease? Release = null);
internal sealed record HubUpdateProgress(string Phase, long CompletedBytes = 0, long TotalBytes = 0);
internal sealed record HubStagedUpdate(string ExecutablePath, string RuntimeRoot, string PreviousRoot,
    int ModelCount, IReadOnlyList<string> Warnings);

internal static class HubUpdateReleaseParser
{
    internal const long MaximumArchiveBytes = 1_610_612_736;
    internal static readonly Uri ReleasesPage = new("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases");
    internal static readonly Uri LatestApi = new("https://api.github.com/repos/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/latest");
    private const string RepositoryPath = "/Fwooffy/Qpro-Enhanced-FT-Wireless";

    internal static bool TryStableVersion(string text, out Version? version)
    {
        version = null;
        if (!Regex.IsMatch(text, @"^[vV]?(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$", RegexOptions.CultureInvariant))
            return false;
        return Version.TryParse(text.TrimStart('v', 'V'), out version) && version.Revision < 0;
    }

    internal static HubUpdateCheck Parse(string json, Version current)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = document.RootElement;
        UniqueObject(root);
        string tag = Text(root, "tag_name", 80);
        if (!TryStableVersion(tag, out Version? version) || Bool(root, "draft") || Bool(root, "prerelease") || version! <= current)
            return new(HubUpdateCheckState.UpToDate, "No newer stable release is available.");
        Uri releasePage = new($"https://github.com{RepositoryPath}/releases/tag/{Uri.EscapeDataString(tag)}");
        string title = OptionalText(root, "name", 256) ?? "QproFaceTracking V" + version;
        string notes = OptionalText(root, "body", 100_000) ?? string.Empty;
        if (notes.Length > 32_768) notes = notes[..32_768] + "\n\nRead the complete notes on GitHub.";
        var candidates = new List<(string Name, Uri Url, long Size, string? Hash)>();
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub did not provide a release asset list.");
        string versionPattern = Regex.Escape(version!.ToString(3));
        string assetPattern = @"^QproFaceTracking[ ._-][vV]?" + versionPattern + @"\.zip$";
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            UniqueObject(asset);
            string name = Text(asset, "name", 256);
            if (!Regex.IsMatch(name, assetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            if (Text(asset, "state", 32) != "uploaded" || !asset.TryGetProperty("size", out JsonElement sizeValue) ||
                !sizeValue.TryGetInt64(out long size) || size <= 0 || size > MaximumArchiveBytes) continue;
            if (!Uri.TryCreate(Text(asset, "browser_download_url", 2048), UriKind.Absolute, out Uri? url) ||
                !IsAssetUrl(url, tag, name)) continue;
            string? digest = OptionalText(asset, "digest", 128);
            string? hash = digest is not null && Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)
                ? digest[7..].ToLowerInvariant() : null;
            candidates.Add((name, url, size, hash));
        }
        if (candidates.Count != 1)
        {
            var manual = new HubUpdateRelease(version, tag, title, releasePage, notes, null, null, 0, null);
            return new(HubUpdateCheckState.ManualDownloadOnly,
                "A newer release is available, but it does not have one matching ready-to-run ZIP. Open its GitHub release page.", manual);
        }
        var candidate = candidates[0];
        var release = new HubUpdateRelease(version, tag, title, releasePage, notes, candidate.Name, candidate.Url, candidate.Size, candidate.Hash);
        return candidate.Hash is null
            ? new(HubUpdateCheckState.ManualDownloadOnly,
                "A newer release is available, but GitHub has not supplied its ZIP checksum. Open the release page to download it manually.", release)
            : new(HubUpdateCheckState.Available, "QproFaceTracking V" + version + " is available.", release);
    }

    internal static bool IsAssetUrl(Uri url, string tag, string assetName) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        url.IsDefaultPort && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0 &&
        Uri.UnescapeDataString(url.AbsolutePath).Equals(RepositoryPath + "/releases/download/" + tag + "/" + assetName, StringComparison.OrdinalIgnoreCase);

    private static void UniqueObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("GitHub release metadata is not an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("GitHub release metadata contains repeated fields.");
    }

    private static bool Bool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("GitHub release metadata is missing " + name + ".");
        return value.GetBoolean();
    }

    private static string Text(JsonElement parent, string name, int maximum) =>
        OptionalText(parent, name, maximum) ?? throw new InvalidDataException("GitHub release metadata is missing " + name + ".");

    private static string? OptionalText(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw new InvalidDataException("GitHub release metadata has an invalid " + name + ".");
        return text;
    }
}
