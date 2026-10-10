using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QproFaceTracking.Hub;

// Public release metadata is enough; the updater never asks for a GitHub login
// or reads the user's Git credentials. Downloads begin only after Update.
internal sealed class HubUpdateClient : IDisposable
{
    private readonly HttpClient _http;

    internal HubUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QproFaceTracking-Updater/3.0.0");
    }

    internal async Task<HubUpdateCheck> CheckAsync(Version current, CancellationToken cancellationToken,
        bool includePrereleases = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Uri endpoint = includePrereleases ? HubUpdateReleaseParser.ReleasesApi : HubUpdateReleaseParser.LatestApi;
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(HubUpdateCheckState.UpToDate, includePrereleases
                    ? "No GitHub release or prerelease is available yet."
                    : "No stable GitHub release is available yet.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(HubUpdateCheckState.Unavailable, "GitHub's update check limit was reached. Try later, or open the release page.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > HubUpdateReleaseParser.MaximumMetadataBytes)
                throw new InvalidDataException("GitHub release metadata is unexpectedly large.");
            using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16_384];
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + read > HubUpdateReleaseParser.MaximumMetadataBytes)
                    throw new InvalidDataException("GitHub release metadata is unexpectedly large.");
                bytes.Write(buffer, 0, read);
            }
            string json = Encoding.UTF8.GetString(bytes.ToArray());
            return includePrereleases
                ? HubUpdateReleaseParser.ParseReleases(json, current, includePrereleases: true)
                : HubUpdateReleaseParser.Parse(json, current);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(HubUpdateCheckState.Unavailable, "The update check timed out. Your current version is ready to use; try later."); }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidDataException)
        { return new(HubUpdateCheckState.Unavailable, "Updates could not be checked: " + error.Message); }
    }

    internal async Task<HubStagedUpdate> StageAsync(HubUpdateRelease release, string existingRuntimeRoot,
        string updateStorageRoot, IProgress<HubUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (!release.CanInstall || !HubUpdateReleaseParser.IsAssetUrl(release.DownloadUrl!, release.Tag, release.AssetName!) ||
            release.AssetSize <= 0 || release.AssetSize > HubUpdateReleaseParser.MaximumArchiveBytes)
            throw new InvalidDataException("This release does not have a verified Qpro ZIP.");
        string storage = HubUpdatePackage.PrepareStorage(updateStorageRoot);
        string download = Path.Combine(storage, ".qpro-download-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            progress?.Report(new("Downloading update", 0, release.AssetSize));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using HttpResponseMessage response = await OpenDownloadAsync(release.DownloadUrl!, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.AssetSize)
                throw new InvalidDataException("The downloaded ZIP size differs from GitHub's release metadata.");
            using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using (var target = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, useAsync: true))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[131_072];
                long completed = 0;
                long lastReport = 0;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    int count = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    completed += count;
                    if (completed > release.AssetSize) throw new InvalidDataException("The downloaded ZIP is larger than expected.");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    if (completed - lastReport >= 524_288 || completed == release.AssetSize)
                    { progress?.Report(new("Downloading update", completed, release.AssetSize)); lastReport = completed; }
                }
                if (completed != release.AssetSize) throw new InvalidDataException("The update download ended before the complete ZIP arrived.");
                string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!actualHash.Equals(release.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The downloaded update failed GitHub's SHA-256 checksum. It was not installed.");
            }
            timeout.Token.ThrowIfCancellationRequested();
            return await Task.Run(() => HubUpdatePackage.Stage(download, release, existingRuntimeRoot, storage,
                progress, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The update download or file check timed out. Your current app is unchanged; try the update again.");
        }
        finally
        {
            if (File.Exists(download)) File.Delete(download);
        }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri original, CancellationToken cancellationToken)
    {
        Uri current = original;
        for (int hop = 0; hop <= 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("GitHub's update download redirect has no address.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            bool hostAllowed = current.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                current.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                current.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            if (!hostAllowed || current.Scheme != Uri.UriSchemeHttps || !current.IsDefaultPort || current.UserInfo.Length != 0)
                throw new InvalidDataException("GitHub redirected the update outside its release download hosts.");
        }
        throw new InvalidDataException("GitHub's update download redirected too many times.");
    }

    public void Dispose() => _http.Dispose();
}
