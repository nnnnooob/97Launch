using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Versions;

/// <summary>
/// One entry of Mojang's version manifest.
/// </summary>
public sealed record ManifestVersion(
    string Id,
    string Type,
    DateTime ReleaseTime,
    string Url,
    string? Sha1 = null)
{
    /// <summary>
    /// Whether this is a plain release, as opposed to a snapshot or an old
    /// alpha/beta build.
    /// </summary>
    public bool IsRelease => Type == "release";

    /// <summary>Snapshot ids look like <c>24w14a</c>.</summary>
    public bool IsSnapshot => Type == "snapshot";

    /// <summary>Alpha and beta builds carry their own types.</summary>
    public bool IsOld => Type is "old_alpha" or "old_beta";
}

/// <summary>
/// Fetches and caches Mojang's list of every published Minecraft version.
/// </summary>
/// <remarks>
/// <para>
/// PCL hits <c>piston-meta.mojang.com/mc/game/version_manifest_v2.json</c> for the
/// same data. The v2 manifest is used rather than the legacy
/// <c>version_manifest.json</c> because Mojang has been steering clients to it for
/// years and it carries per-version <c>sha1</c> alongside the URL, which a later
/// integrity check can use without a second design.
/// </para>
/// <para>
/// The result is cached on disk for twelve hours: it changes only when Mojang
/// ships a version, and a launcher that re-downloads it on every start is both
/// slow and rude to the CDN.
/// </para>
/// </remarks>
public sealed class MinecraftVersionService
{
    private const string ManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>
    /// Mirror used when the official host is unreachable. BMCLAPI is the same
    /// mirror PCL falls back to, and it matters here because Mojang's hosts are
    /// frequently blocked on mainland networks.
    /// </summary>
    private const string MirrorUrl =
        "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    /// <summary>
    /// One client for the lifetime of the process.
    /// </summary>
    /// <remarks>
    /// A fresh <c>HttpClient</c> per call is the classic socket-exhaustion bug:
    /// each one holds its own connection pool, and disposing it leaves the socket
    /// in TIME_WAIT. A single static instance is the documented fix; the DNS
    /// rotation that it would otherwise prevent does not matter for two fixed
    /// Mojang hosts.
    /// </remarks>
    private static readonly HttpClient Http = CreateClient();

    /// <summary>How long a cached manifest stays usable.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly ILogger<MinecraftVersionService> _log;
    private readonly string _cachePath;

    public MinecraftVersionService(ILogger<MinecraftVersionService> log, string cacheDirectory)
    {
        _log = log;
        _cachePath = Path.Combine(cacheDirectory, "version_manifest.json");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("97Launch/0.1 (+https://github.com/)");
        return client;
    }

    /// <summary>
    /// Returns every known version, newest first.
    /// </summary>
    /// <param name="forceRefresh">Ignore the on-disk cache.</param>
    public async Task<IReadOnlyList<ManifestVersion>> GetVersionsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryReadCache(out var cached))
        {
            return cached;
        }

        foreach (var url in new[] { ManifestUrl, MirrorUrl })
        {
            try
            {
                var json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var parsed = Parse(json);
                if (parsed.Count > 0)
                {
                    WriteCache(json);
                    _log.LogInformation(
                        "已获取版本清单，共 {Count} 个版本（来源 {Url}）", parsed.Count, url);
                    return parsed;
                }

                _log.LogWarning("版本清单解析结果为空：{Url}", url);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "获取版本清单失败：{Url}", url);
            }
        }

        // Both hosts failed. A stale cache is far better than an empty page.
        if (TryReadCache(out var stale, ignoreAge: true))
        {
            _log.LogWarning("无法联网，使用过期的版本清单缓存");
            return stale;
        }

        return [];
    }

    /// <summary>Downloads one version's launch metadata from Mojang or BMCLAPI.</summary>
    public async Task<(string Json, string? Sha1)> GetVersionDocumentAsync(
        string versionId,
        CancellationToken cancellationToken = default,
        string sourcePreference = "自动选择")
    {
        var entry = (await GetVersionsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false))
            .FirstOrDefault(version => version.Id == versionId)
            ?? throw new InvalidOperationException($"版本清单中不存在 Minecraft {versionId}");

        var urls = new[]
        {
            entry.Url,
            $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(versionId)}/json",
        }.Where(url => Uri.TryCreate(url, UriKind.Absolute, out _)).Distinct();
        if (sourcePreference == "BMCLAPI") urls = urls.Reverse();
        else if (sourcePreference == "官方源") urls = urls.Take(1);

        foreach (var url in urls)
        {
            try
            {
                return (await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false), entry.Sha1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "获取 Minecraft {Version} 元数据失败：{Url}", versionId, url);
            }
        }

        throw new HttpRequestException($"无法获取 Minecraft {versionId} 的版本数据");
    }

    /// <summary>
    /// Parses a manifest document.
    /// </summary>
    /// <remarks>
    /// <c>JsonNode</c> rather than a typed contract: the manifest carries fields
    /// this launcher ignores (<c>complianceLevel</c>, <c>sha1</c>, <c>time</c>), and
    /// a strict deserialiser would break on the next field Mojang adds.
    /// </remarks>
    public static List<ManifestVersion> Parse(string json)
    {
        var results = new List<ManifestVersion>();
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return results;
        }

        if (document is not JsonObject root
            || root["versions"] is not JsonArray versions)
        {
            return results;
        }

        foreach (var entry in versions)
        {
            var id = entry?["id"]?.GetValue<string>();
            var type = entry?["type"]?.GetValue<string>();
            var url = entry?["url"]?.GetValue<string>();
            var sha1 = entry?["sha1"]?.GetValue<string>();
            var releaseRaw = entry?["releaseTime"]?.GetValue<string>();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
            {
                continue;
            }

            _ = DateTime.TryParse(releaseRaw, out var release);

            results.Add(new ManifestVersion(id, type, release, url ?? "", sha1));
        }

        // The manifest is already newest-first, but sorting makes the page
        // independent of that guarantee.
        results.Sort((a, b) => b.ReleaseTime.CompareTo(a.ReleaseTime));
        return results;
    }

    private bool TryReadCache(out List<ManifestVersion> versions, bool ignoreAge = false)
    {
        versions = [];

        try
        {
            if (!File.Exists(_cachePath))
            {
                return false;
            }

            if (!ignoreAge
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) > CacheLifetime)
            {
                return false;
            }

            versions = Parse(File.ReadAllText(_cachePath));
            return versions.Count > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读取版本清单缓存失败");
            return false;
        }
    }

    /// <summary>
    /// Writes the manifest cache atomically.
    /// </summary>
    /// <remarks>
    /// Via a temporary file plus <see cref="File.Move(string, string, bool)"/>:
    /// a direct overwrite that is interrupted leaves a truncated JSON document,
    /// which then fails to parse on every later start until the user deletes it.
    /// The rename is the only step that changes the visible file.
    /// </remarks>
    private void WriteCache(string json)
    {
        var temp = _cachePath + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "写入版本清单缓存失败");

            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // A leftover temp file is harmless; it is overwritten next time.
            }
        }
    }
}
