using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.App.Services;

internal static class GitHubUpdateSource
{
    private const string Repository = "nnnnooob/97Launch";
    private const string Api = "https://api.github.com/repos/" + Repository + "/releases";
    private const string Downloads = "https://github.com/" + Repository + "/releases/download/";
    private const string Executable = "97Launch.exe";
    private const string Manifest = "manifest.json";
    private const int PageSize = 100;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    private sealed record Release(long Id, string TagName, bool Draft, bool Prerelease, DateTimeOffset? PublishedAt, string? Body, Asset[] Assets);
    private sealed record Asset(string Name, string State, long Size, string? Digest);

    internal static async Task<UpdateRelease?> LatestAsync(string channel, CancellationToken token)
    {
        var releases = new List<(Release Release, int Build)>();
        for (var page = 1; ; page++)
        {
            using var response = await GetApiAsync($"{Api}?per_page={PageSize}&page={page}", token);
            response.EnsureSuccessStatusCode();
            var entries = await ReadAsync<Release[]>(response, 8 * 1024 * 1024, token);
            foreach (var entry in entries)
            {
                if (entry.Draft || entry.PublishedAt is null || (channel == "stable" && entry.Prerelease)) continue;
                var match = Regex.Match(entry.TagName, @"\Av[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?\+build\.([0-9]{1,5})\z");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var build) && build > 0
                    && FindAsset(entry, Executable) is not null && FindAsset(entry, Manifest) is not null)
                    releases.Add((entry, build));
            }
            if (entries.Length < PageSize) break;
        }

        foreach (var entry in releases.OrderByDescending(candidate => candidate.Build))
        {
            var (artifact, release) = await ReadReleaseAsync(entry.Release, token);
            if (artifact.Architecture == UpdateTrust.Architecture && (channel == "preview" || artifact.Channel == "stable"))
                return release;
        }
        return null;
    }

    internal static async Task<UpdateRelease> PublishedAsync(UpdateRelease release, CancellationToken token)
    {
        var artifact = UpdateTrust.Verify(release);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await GetApiAsync(Api + "/" + release.Id, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub 上的版本已撤回，请重新检查更新。");
        response.EnsureSuccessStatusCode();
        var entry = await ReadAsync<Release>(response, 1024 * 1024, timeout.Token);
        if (entry.TagName != Tag(artifact)) throw new InvalidDataException("GitHub 发布标签已变更，请重新检查更新。");
        var (_, current) = await ReadReleaseAsync(entry, timeout.Token);
        if (current.Id != release.Id || current.Manifest != release.Manifest)
            throw new InvalidDataException("发布清单发生变化，请重新检查更新。");
        return current;
    }

    internal static Task<HttpResponseMessage> OpenExecutableAsync(UpdateRelease release, long offset, CancellationToken token)
    {
        var artifact = UpdateTrust.Verify(release);
        return OpenAssetAsync(Tag(artifact), Executable, offset, token);
    }

    private static async Task<(UpdateArtifact Artifact, UpdateRelease Release)> ReadReleaseAsync(Release entry, CancellationToken token)
    {
        var manifestAsset = FindAsset(entry, Manifest);
        var exe = FindAsset(entry, Executable);
        if (entry.Id <= 0 || entry.Draft || entry.PublishedAt is null || manifestAsset is null || exe is null)
            throw new InvalidDataException("GitHub 发布缺少可下载的更新文件。");

        using var response = await OpenAssetAsync(entry.TagName, Manifest, 0, token);
        response.EnsureSuccessStatusCode();
        var manifest = await ReadAsync<SignedUpdateManifest>(response, 16384, token);
        var artifact = UpdateTrust.VerifyManifest(manifest);
        if (entry.TagName != Tag(artifact) || entry.Prerelease != (artifact.Channel == "preview") || exe.Size != artifact.Size
            || (exe.Digest is { Length: > 0 } digest && !digest.Equals("sha256:" + artifact.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("GitHub 发布标签或安装包与签名清单不符。");

        var release = new UpdateRelease(entry.Id.ToString(CultureInfo.InvariantCulture), artifact.Version, artifact.Build,
            artifact.Channel, artifact.Architecture, artifact.Size, entry.Body ?? "", entry.PublishedAt.Value.ToUnixTimeSeconds(), manifest);
        return (artifact, release);
    }

    private static string Tag(UpdateArtifact artifact) => FormattableString.Invariant($"v{artifact.Version}+build.{artifact.Build}");
    private static Asset? FindAsset(Release entry, string name) => entry.Assets.FirstOrDefault(asset => asset.Name == name && asset.State == "uploaded");

    private static async Task<HttpResponseMessage> GetApiAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("97Launch/" + AppInfo.Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static async Task<HttpResponseMessage> OpenAssetAsync(string tag, string name, long offset, CancellationToken token)
    {
        var uri = new Uri(Downloads + Uri.EscapeDataString(tag) + "/" + name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("97Launch/" + AppInfo.Version);
            if (offset > 0) request.Headers.Range = new(offset, null);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || !Uri.TryCreate(uri, location, out var next) || next.Scheme != Uri.UriSchemeHttps
                || !next.IsDefaultPort || next.UserInfo.Length != 0 || next.Fragment.Length != 0
                || next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("GitHub 返回了无效的下载地址。");
            uri = next;
        }
        throw new InvalidDataException("GitHub 下载跳转次数过多。");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("GitHub 更新元数据过大。");
        using var body = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (body.Length + count > limit) throw new InvalidDataException("GitHub 更新元数据过大。");
            body.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(body.ToArray(), UpdateTrust.Json) ?? throw new InvalidDataException("GitHub 更新元数据为空。");
    }
}
