using System.Net.Http.Json;
using System.Text.Json;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;

namespace Launcher.App.Services;

public sealed record LocalModOnlineInfo(ModrinthProject Project, ModrinthVersion Current, ModrinthVersion? Update, bool IsStale = false);

public sealed class LocalModOnlineService(CommunityResourceService resources, ModInstallService mods)
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"97Launch/{AppInfo.Version.TrimStart('v')} (local-resource-manager)");
        return http;
    }

    public async Task<LocalModOnlineInfo?> LookupAsync(InstanceContext context, LocalContent file, CancellationToken token, bool refresh = false)
    {
        var hash = await InstanceModificationService.HashAsync(file.Path, token);
        var record = ModInstallService.ReadIndex(context).FirstOrDefault(m => m.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase));
        var cache = Path.Combine(context.Root, "97Launch", "cache", "local-mods", hash + "-" + context.Instance.MinecraftVersionId + "-" + context.Instance.Loader + ".json");
        LocalModOnlineInfo? cached = null;
        try { if (File.Exists(cache)) cached = JsonSerializer.Deserialize<LocalModOnlineInfo>(await File.ReadAllTextAsync(cache, token)); }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        if (!refresh && cached is not null && (record is null || cached.Project.Source == record.Source && cached.Project.Id == record.ProjectId)
            && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddHours(-6)) return cached;
        try
        {
            string source, projectId; string? id;
            if (record is not null) { source = record.Source; projectId = record.ProjectId; id = record.VersionId; }
            else
            {
                using var response = await Http.PostAsJsonAsync("https://api.modrinth.com/v2/version_files", new { hashes = new[] { hash }, algorithm = "sha1" }, token);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                if (!document.RootElement.TryGetProperty(hash, out var version)) return null;
                source = "Modrinth"; projectId = version.GetProperty("project_id").GetString()!; id = version.GetProperty("id").GetString();
            }
            var project = await resources.GetProjectAsync(source, projectId, token);
            var versions = await resources.GetVersionsAsync(project, token);
            var current = versions.FirstOrDefault(v => v.Id == id);
            if (current is null || !current.Files.Any(f => string.Equals(f.Sha1, hash, StringComparison.OrdinalIgnoreCase))) return null;
            var loader = context.Instance.Loader.ToLowerInvariant();
            var latest = versions.Where(v => v.GameVersions.Contains(context.Instance.MinecraftVersionId)
                && v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase) && v.Type == "release")
                .OrderByDescending(v => v.Published).FirstOrDefault();
            var result = new LocalModOnlineInfo(project, current, latest is not null && latest.Published > current.Published && latest.Id != current.Id ? latest : null);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(result), token);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            if (cached is not null) return cached with { IsStale = true };
            throw;
        }
    }

    public async Task UpdateBatchAsync(InstanceContext context, IReadOnlyList<(LocalContent File, LocalModOnlineInfo Online)> updates,
        IProgress<InstallProgress> progress, CancellationToken token)
    {
        var selections = new List<ModInstallSelection>();
        foreach (var item in updates)
        {
            if (item.Online.IsStale) throw new IOException("更新信息未能刷新，请联网后重新检查：" + item.File.Name);
            var hash = await InstanceModificationService.HashAsync(item.File.Path, token);
            if (!item.Online.Current.Files.Any(f => string.Equals(f.Sha1, hash, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("原 Mod 在检查更新后已发生变化，请刷新后重试：" + item.File.Name);
            selections.Add(new(item.Online.Project, item.Online.Update ?? throw new InvalidOperationException("没有适用的新版本。"), item.File, hash));
        }
        await mods.InstallAsync(context, selections, "{original}", progress, token);
    }
}
