using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal enum HubUpdateCheckState { UpToDate, Available, ManualDownloadOnly, Unavailable }

internal sealed record HubUpdateRelease(Version Version, string Tag, string Title, Uri ReleasePage,
    string Notes, string? AssetName, Uri? DownloadUrl, long AssetSize, string? Sha256,
    bool IsPrerelease = false, string? PrereleaseSuffix = null)
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
    internal const int MaximumReleaseListEntries = 30;
    internal const int MaximumMetadataBytes = 2_097_152;
    internal static readonly Uri ReleasesPage = new("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases");
    internal static readonly Uri LatestApi = new("https://api.github.com/repos/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/latest");
    internal static readonly Uri ReleasesApi = new("https://api.github.com/repos/Fwooffy/Qpro-Enhanced-FT-Wireless/releases?per_page=30");
    private const string RepositoryPath = "/Fwooffy/Qpro-Enhanced-FT-Wireless";

    internal static bool TryStableVersion(string text, out Version? version)
    {
        version = null;
        if (!Regex.IsMatch(text, @"^[vV]?(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$", RegexOptions.CultureInvariant))
            return false;
        return Version.TryParse(text.TrimStart('v', 'V'), out version) && version.Revision < 0;
    }

    internal static HubUpdateCheck Parse(string json, Version current, bool includePrereleases = false)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        return ParseRelease(document.RootElement, current, includePrereleases);
    }

    internal static HubUpdateCheck ParseReleases(string json, Version current, bool includePrereleases = false)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement releases = document.RootElement;
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > MaximumReleaseListEntries)
            throw new InvalidDataException("GitHub did not provide a bounded release list.");
        HubUpdateCheck? newest = null;
        int invalidEntries = 0;
        foreach (JsonElement release in releases.EnumerateArray())
        {
            HubUpdateCheck check;
            try { check = ParseRelease(release, current, includePrereleases); }
            catch (Exception error) when (error is InvalidDataException or JsonException or UriFormatException)
            {
                // A broken release entry must not hide a valid release elsewhere
                // in the response, and list order is not version order.
                invalidEntries++;
                continue;
            }
            if (check.Release is not { } candidate) continue;
            if (newest?.Release is not { } previous ||
                IsPreferred(candidate, check.State, previous, newest.State))
                newest = check;
        }
        if (newest is not null) return newest;
        return invalidEntries == 0
            ? NoNewRelease(includePrereleases)
            : new(HubUpdateCheckState.Unavailable,
                "GitHub returned incomplete release details. Try again later, or open the release page.");
    }

    private static HubUpdateCheck NoNewRelease(bool includePrereleases) => new(HubUpdateCheckState.UpToDate,
        includePrereleases
            ? "No newer compatible update is available. Prerelease suffix tags require manual download."
            : "No newer stable release is available.");

    private static bool IsPreferred(HubUpdateRelease candidate, HubUpdateCheckState candidateState,
        HubUpdateRelease previous, HubUpdateCheckState previousState)
    {
        int version = candidate.Version.CompareTo(previous.Version);
        if (version != 0) return version > 0;
        if (candidate.IsPrerelease != previous.IsPrerelease) return !candidate.IsPrerelease;
        if (candidate.PrereleaseSuffix != previous.PrereleaseSuffix)
        {
            if (candidate.PrereleaseSuffix is null) return true;
            if (previous.PrereleaseSuffix is null) return false;
            int suffix = ComparePrereleaseSuffix(candidate.PrereleaseSuffix, previous.PrereleaseSuffix);
            if (suffix != 0) return suffix > 0;
        }
        return previousState == HubUpdateCheckState.ManualDownloadOnly && candidateState == HubUpdateCheckState.Available;
    }

    private static int ComparePrereleaseSuffix(string first, string second)
    {
        string[] left = first.Split('.'), right = second.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = left[index].All(char.IsAsciiDigit), rightNumeric = right[index].All(char.IsAsciiDigit);
            int compared;
            if (leftNumeric && rightNumeric)
                compared = left[index].Length == right[index].Length
                    ? StringComparer.Ordinal.Compare(left[index], right[index])
                    : left[index].Length.CompareTo(right[index].Length);
            else if (leftNumeric != rightNumeric) compared = leftNumeric ? -1 : 1;
            else compared = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (compared != 0) return compared;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static bool TryPrereleaseVersion(string text, out Version? version, out string? suffix)
    {
        version = null;
        suffix = null;
        Match match = Regex.Match(text,
            @"^[vV]?((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !TryStableVersion(match.Groups[1].Value, out version)) return false;
        string value = match.Groups[2].Value;
        if (value.Split('.').Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))) return false;
        suffix = value;
        return true;
    }

    private static HubUpdateCheck ParseRelease(JsonElement root, Version current, bool includePrereleases)
    {
        UniqueObject(root);
        if (Bool(root, "draft")) return NoNewRelease(includePrereleases);
        bool isPrerelease = Bool(root, "prerelease");
        if (isPrerelease && !includePrereleases) return NoNewRelease(false);
        string tag = Text(root, "tag_name", 80);
        bool numericTag = TryStableVersion(tag, out Version? version);
        string? suffix = null;
        if ((!numericTag && (!includePrereleases || !isPrerelease ||
                !TryPrereleaseVersion(tag, out version, out suffix))) || version! <= current)
            return NoNewRelease(includePrereleases);
        Uri releasePage = new($"https://github.com{RepositoryPath}/releases/tag/{Uri.EscapeDataString(tag)}");
        string title = OptionalText(root, "name", 256) ?? "QproFaceTracking V" + version;
        string notes = OptionalText(root, "body", 100_000) ?? string.Empty;
        if (notes.Length > 32_768) notes = notes[..32_768] + "\n\nRead the complete notes on GitHub.";
        if (!numericTag)
        {
            // Show genuine newer betas without pretending the numeric package
            // manifest can distinguish their installed prerelease identity.
            var manual = new HubUpdateRelease(version!, tag, title, releasePage, notes,
                null, null, 0, null, IsPrerelease: true, PrereleaseSuffix: suffix);
            return new(HubUpdateCheckState.ManualDownloadOnly,
                "A newer prerelease is available. Its version suffix requires manual download; open its GitHub release page.", manual);
        }
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
            var manual = new HubUpdateRelease(version, tag, title, releasePage, notes, null, null, 0, null, isPrerelease);
            return new(HubUpdateCheckState.ManualDownloadOnly,
                "A newer release is available, but it does not have one matching ready-to-run ZIP. Open its GitHub release page.", manual);
        }
        var candidate = candidates[0];
        var release = new HubUpdateRelease(version, tag, title, releasePage, notes, candidate.Name, candidate.Url, candidate.Size, candidate.Hash, isPrerelease);
        return candidate.Hash is null
            ? new(HubUpdateCheckState.ManualDownloadOnly,
                "A newer release is available, but GitHub has not supplied its ZIP checksum. Open the release page to download it manually.", release)
            : new(HubUpdateCheckState.Available, "QproFaceTracking V" + version +
                (isPrerelease ? " prerelease is available." : " is available."), release);
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
