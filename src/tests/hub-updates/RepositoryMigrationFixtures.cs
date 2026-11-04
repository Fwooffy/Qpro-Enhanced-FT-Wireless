using System.Net;
using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

internal static class RepositoryMigrationFixtures
{
    internal static async Task RunAsync(Version current, Func<JsonObject> metadata,
        Action<bool, string> check, string archive, HubUpdateRelease download, string oldRoot, string storage)
    {
        check(HubUpdateReleaseParser.LatestApi.AbsolutePath == "/repos/Fwooffy/Qpro-Companion/releases/latest",
            "canonical release checks use Qpro-Companion");
        check(HubUpdateReleaseParser.ReleasesPage.AbsolutePath == "/Fwooffy/Qpro-Enhanced-FT-Wireless/releases",
            "generic browser link stays usable before rename and through GitHub's web redirect afterward");

        foreach (bool prereleases in new[] { false, true })
        {
            Uri canonical = prereleases ? HubUpdateReleaseParser.ReleasesApi : HubUpdateReleaseParser.LatestApi;
            Uri legacy = prereleases ? HubUpdateReleaseParser.LegacyReleasesApi : HubUpdateReleaseParser.LegacyLatestApi;
            JsonObject release = metadata();
            release["prerelease"] = prereleases;
            string json = prereleases ? new JsonArray(release).ToJsonString() : release.ToJsonString();
            var requests = new List<Uri>();
            using (var client = new HubUpdateClient(new FixtureHandler(request =>
            {
                requests.Add(request.RequestUri!);
                check(request.Headers.Authorization is null, "migration checks remain unauthenticated");
                return request.RequestUri == canonical ? new(HttpStatusCode.NotFound) : Json(json);
            })))
            {
                HubUpdateCheck result = await client.CheckAsync(current, default, prereleases);
                check(result.State == HubUpdateCheckState.Available && result.Release?.IsPrerelease == prereleases,
                    "legacy fallback preserves the selected release channel");
                check(result.Release?.ReleasePage.AbsolutePath.StartsWith("/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/", StringComparison.Ordinal) == true,
                    "legacy fallback keeps manual release links usable before rename");
            }
            check(requests.SequenceEqual(new[] { canonical, legacy }), "legacy metadata is tried only after canonical 404");

            requests.Clear();
            using (var client = new HubUpdateClient(new FixtureHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return Json(json);
            })))
                check((await client.CheckAsync(current, default, prereleases)).State == HubUpdateCheckState.Available,
                    "renamed repository is checked directly");
            check(requests.SequenceEqual(new[] { canonical }), "successful canonical check never asks legacy repository");

            requests.Clear();
            using (var client = new HubUpdateClient(new FixtureHandler(request =>
            {
                requests.Add(request.RequestUri!);
                if (requests.Count == 1) return new(HttpStatusCode.NotFound);
                return request.RequestUri == legacy ? Redirect(canonical, HttpStatusCode.MovedPermanently) : Json(json);
            })))
                check((await client.CheckAsync(current, default, prereleases)).State == HubUpdateCheckState.Available,
                    "legacy-to-canonical metadata redirect works during rename propagation");
            check(requests.SequenceEqual(new[] { canonical, legacy, canonical }), "metadata redirect uses only the same channel's exact repository endpoints");

            Uri pinned = prereleases ? HubUpdateReleaseParser.RepositoryReleasesApi : HubUpdateReleaseParser.RepositoryLatestApi;
            requests.Clear();
            using (var client = new HubUpdateClient(new FixtureHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return request.RequestUri == canonical ? Redirect(pinned, HttpStatusCode.MovedPermanently) : Json(json);
            })))
            {
                HubUpdateCheck result = await client.CheckAsync(current, default, prereleases);
                check(result.State == HubUpdateCheckState.Available && result.Release?.IsPrerelease == prereleases,
                    "GitHub's pinned numeric repository redirect preserves the selected release channel");
                check(result.Release?.ReleasePage.AbsolutePath.StartsWith("/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/", StringComparison.Ordinal) == true,
                    "numeric repository response without a page URL keeps the transition browser fallback");
            }
            check(requests.SequenceEqual(new[] { canonical, pinned }), "only the verified immutable repository ID is followed");

            int crossChannelRequests = 0;
            using (var client = new HubUpdateClient(new FixtureHandler(_ =>
            {
                crossChannelRequests++;
                return Redirect(prereleases ? HubUpdateReleaseParser.RepositoryLatestApi : HubUpdateReleaseParser.RepositoryReleasesApi);
            })))
                check((await client.CheckAsync(current, default, prereleases)).State == HubUpdateCheckState.Unavailable && crossChannelRequests == 1,
                    "pinned numeric repository redirects cannot switch the selected channel");
        }

        foreach (HttpStatusCode failure in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests,
                     HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(_ =>
            { requests++; return new(failure); }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable && requests == 1,
                "HTTP failure does not silently fall back: " + failure);
        }
        foreach (string body in new[] { "not json", "{}" })
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(_ =>
            { requests++; return Json(body); }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable && requests == 1,
                "malformed canonical metadata does not silently use an older feed");
        }
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(_ =>
            { requests++; throw new HttpRequestException("offline fixture"); }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable && requests == 1,
                "network failure is reported without fallback");
        }
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(_ =>
            { requests++; return new(HttpStatusCode.NotFound); }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.UpToDate && requests == 2,
                "no release on either known repository remains a bounded no-release result");
        }

        foreach (Uri destination in new[]
        {
            new Uri("https://api.github.com/repos/OtherOwner/Qpro-Companion/releases/latest"),
            new Uri("https://api.github.com/repos/Fwooffy/OtherRepo/releases/latest"),
            new Uri("https://api.github.com/repos/Fwooffy/Qpro-Companion-copy/releases/latest"),
            new Uri("https://api.github.com/repos/Fwooffy/Qpro-Companion/releases?per_page=30"),
            new Uri("https://api.github.com/repos/Fwooffy/Qpro-Companion/releases/latest?token=secret"),
            new Uri("https://api.github.com/repos/Fwooffy/Qpro-Companion/releases/latest#fragment"),
            new Uri("http://api.github.com/repos/Fwooffy/Qpro-Companion/releases/latest"),
            new Uri("https://api.github.com:444/repos/Fwooffy/Qpro-Companion/releases/latest"),
            new Uri("https://attacker@api.github.com/repos/Fwooffy/Qpro-Companion/releases/latest"),
            new Uri("https://api.github.com.evil.example/repos/Fwooffy/Qpro-Companion/releases/latest"),
            new Uri("https://api.github.com/repositories/123/releases/latest"),
            new Uri("https://api.github.com/repositories/1386190012/releases/latest"),
            new Uri("https://api.github.com/repositories/1386190011/releases/latest?token=secret"),
        })
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(_ =>
            { requests++; return Redirect(destination); }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable && requests == 1,
                "untrusted or cross-channel metadata redirect is not requested: " + destination);
        }
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(request =>
            {
                requests++;
                return request.RequestUri == HubUpdateReleaseParser.LatestApi
                    ? Redirect(new Uri("/repos/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/latest", UriKind.Relative))
                    : Json(metadata().ToJsonString());
            }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Available && requests == 2,
                "relative metadata redirect is resolved and validated");
        }
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(request =>
            {
                requests++;
                return Redirect(request.RequestUri == HubUpdateReleaseParser.LatestApi
                    ? HubUpdateReleaseParser.LegacyLatestApi : HubUpdateReleaseParser.LatestApi);
            }));
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable && requests == 2,
                "metadata redirect cycle stops before making repeated requests");
        }
        using (var client = new HubUpdateClient(new FixtureHandler(_ => new(HttpStatusCode.Redirect))))
            check((await client.CheckAsync(current, default)).State == HubUpdateCheckState.Unavailable,
                "metadata redirect without a location is rejected");

        JsonObject page = metadata();
        string tag = page["tag_name"]!.GetValue<string>();
        page["html_url"] = "https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/" + tag;
        check(HubUpdateReleaseParser.Parse(page.ToJsonString(), current).Release?.ReleasePage.AbsolutePath.StartsWith(
            "/Fwooffy/Qpro-Enhanced-FT-Wireless/", StringComparison.Ordinal) == true,
            "trusted published release-page URL is retained");
        page["html_url"] = "https://github.com/OtherOwner/Qpro-Companion/releases/tag/" + tag;
        check(HubUpdateReleaseParser.Parse(page.ToJsonString(), current).Release?.ReleasePage.AbsolutePath ==
            "/Fwooffy/Qpro-Companion/releases/tag/" + tag, "foreign release-page URL cannot replace canonical fallback");
        check(HubUpdateReleaseParser.Parse(metadata().ToJsonString(), current,
            metadataEndpoint: new Uri("https://api.github.com/repos/FWOOFFY/QPRO-ENHANCED-FT-WIRELESS/releases/latest"))
                .Release?.ReleasePage.AbsolutePath == "/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/" + tag,
            "trusted repository casing does not break the legacy release-page fallback");

        Uri renamedAsset = new(download.DownloadUrl!.AbsoluteUri.Replace("Qpro-Enhanced-FT-Wireless", "Qpro-Companion"));
        Uri cdn = new("https://release-assets.githubusercontent.com/github-production-release-asset/fixture?signature=fixture");
        var downloadRequests = new List<Uri>();
        using (var client = new HubUpdateClient(new FixtureHandler(request =>
        {
            downloadRequests.Add(request.RequestUri!);
            check(request.Headers.Authorization is null, "migration download sends no credentials");
            if (request.RequestUri == download.DownloadUrl) return Redirect(renamedAsset, HttpStatusCode.MovedPermanently);
            if (request.RequestUri == renamedAsset) return Redirect(cdn);
            return Bytes(archive);
        })))
        {
            HubStagedUpdate staged = await client.StageAsync(download, oldRoot, storage, null, default);
            check(File.Exists(staged.ExecutablePath), "rename and signed-CDN redirects stage a checksum-verified package");
            HubUpdatePackage.Discard(staged, storage);
        }
        check(downloadRequests.SequenceEqual(new[] { download.DownloadUrl, renamedAsset, cdn }), "asset migration follows only intended redirect hops");

        foreach (Uri destination in new[]
        {
            new Uri(renamedAsset.AbsoluteUri.Replace("/Fwooffy/", "/OtherOwner/")),
            new Uri(renamedAsset.AbsoluteUri.Replace("/Qpro-Companion/", "/OtherRepo/")),
            new Uri(renamedAsset.AbsoluteUri.Replace("/releases/download/" + download.Tag + "/", "/releases/download/v99.0.0/")),
            new Uri(renamedAsset.AbsoluteUri + "?unexpected=1"),
            new Uri(renamedAsset.AbsoluteUri + "#fragment"),
            new Uri("http://release-assets.githubusercontent.com/fixture"),
        })
        {
            int requests = 0;
            using var client = new HubUpdateClient(new FixtureHandler(_ =>
            { requests++; return Redirect(destination); }));
            bool rejected = false;
            try { await client.StageAsync(download, oldRoot, storage, null, default); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && requests == 1, "asset rename redirects retain exact repository, release and asset identity: " + destination);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage Bytes(string archive) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(archive)) };
    private static HttpResponseMessage Redirect(Uri target, HttpStatusCode status = HttpStatusCode.Redirect)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = target;
        return response;
    }
}
