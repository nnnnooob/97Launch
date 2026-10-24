using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Launcher.Core.Downloads;

public sealed record CurseForgeMissingFile(string ProjectId, string FileId, string Name);

/// <summary>CurseForge API with the public MCI route also used by PCL.</summary>
public sealed class CurseForgeResourceService(HttpClient http, Func<string?> apiKey)
{
    private const string Official = "https://api.curseforge.com";
    private const string Mirror = "https://mod.mcimirror.top/curseforge";
    private readonly Dictionary<string, ModrinthProject> _projects = new();

    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(string query, string type, string game,
        string loader, string sort, int offset, string? category, string? resolution, CancellationToken token)
    {
        var classId = type switch { "modpack" => 4471, "shader" => 6552, "resourcepack" => 12, "datapack" => 6945, _ => 6 };
        var path = $"/v1/mods/search?gameId=432&classId={classId}&pageSize=50&index={offset}&sortOrder=desc&sortField="
            + (sort == "最新" ? "3" : sort == "下载量" ? "6" : "2");
        if (!string.IsNullOrWhiteSpace(query)) path += "&searchFilter=" + Uri.EscapeDataString(query);
        if (game != "全部" && game.Length > 0) path += "&gameVersion=" + Uri.EscapeDataString(game);
        if (LoaderId(loader) is { } loaderId) path += "&modLoaderType=" + loaderId;
        if (category is not null || resolution is not null)
        {
            using var categories = await GetAsync("/v1/categories?gameId=432", token).ConfigureAwait(false);
            var aliases = category switch { "worldgen" => new[] { "world-gen", "world-generation" }, "optimization" => ["performance"],
                "decoration" => ["cosmetic"], "utility" => ["utility-qol", "miscellaneous"], "adventure" => ["adventure-rpg"], _ => new[] { category ?? "" } };
            var match = categories.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(item =>
                Int(item, "classId") == classId && (aliases.Contains(Str(item, "slug"))
                    || resolution is not null && Str(item, "name").Equals(resolution, StringComparison.OrdinalIgnoreCase)));
            if (match.ValueKind == JsonValueKind.Undefined) return [];
            path += "&categoryId=" + Int(match, "id");
        }
        using var doc = await GetAsync(path, token).ConfigureAwait(false);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(item => ParseProject(item, type)).ToArray();
    }

    public async Task<ModrinthProject> GetProjectAsync(string id, CancellationToken token)
    {
        lock (_projects) if (_projects.TryGetValue(id, out var cached)) return cached;
        using var doc = await GetAsync("/v1/mods/" + Id(id), token).ConfigureAwait(false);
        return ParseProject(doc.RootElement.GetProperty("data"));
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, CancellationToken token)
    {
        async Task<(ModrinthVersion[] Versions, int Count, int Total)> Page(int offset)
        {
            using var doc = await GetAsync($"/v1/mods/{Id(projectId)}/files?pageSize=50&index={offset}", token).ConfigureAwait(false);
            var data = doc.RootElement.GetProperty("data");
            return (data.EnumerateArray().Where(IsAvailable).Select(ParseVersion).ToArray(), data.GetArrayLength(),
                doc.RootElement.TryGetProperty("pagination", out var page) ? Math.Min(10000, Int(page, "totalCount")) : offset + data.GetArrayLength());
        }
        var first = await Page(0).ConfigureAwait(false);
        var result = first.Versions.ToList();
        foreach (var offsets in Enumerable.Range(1, Math.Max(0, (first.Total + 49) / 50 - 1)).Select(index => index * 50).Chunk(4))
            foreach (var page in await Task.WhenAll(offsets.Select(Page)).ConfigureAwait(false)) result.AddRange(page.Versions);
        return result.OrderByDescending(version => version.Published).ToArray();
    }

    public async Task<IReadOnlyList<DownloadFileRequest>> ResolveFilesAsync(ModrinthProject project,
        string versionId, string game, string loader, string directory, IReadOnlySet<string> optional,
        string nameFormat, CancellationToken token)
    {
        var queue = new Queue<(ModrinthProject Project, string? File)>();
        var visited = new HashSet<string>();
        var result = new List<DownloadFileRequest>();
        queue.Enqueue((project, versionId));
        while (queue.TryDequeue(out var item))
        {
            if (!visited.Add(item.Project.Id)) continue;
            if (visited.Count > 80) throw new InvalidDataException("前置资源数量超过支持范围。");
            ModrinthVersion version;
            if (item.File is { } fileId)
            {
                using var doc = await GetAsync($"/v1/mods/{Id(item.Project.Id)}/files/{Id(fileId)}", token).ConfigureAwait(false);
                version = ParseVersion(doc.RootElement.GetProperty("data"));
            }
            else
            {
                var versions = (await GetVersionsAsync(item.Project.Id, token).ConfigureAwait(false))
                    .Where(v => ResourceVersionSelection.IsCompatible(v, game, item.Project.ProjectType == "mod" ? loader : "")).ToArray();
                version = versions.FirstOrDefault(v => v.Type == "release") ?? versions.FirstOrDefault()
                    ?? throw new InvalidDataException($"{item.Project.Title} 没有适合 {loader} {game} 的前置版本。");
            }
            var file = version.PrimaryFile ?? throw new InvalidDataException($"{item.Project.Title} 未提供第三方下载地址，请前往项目主页下载。");
            if (!ResourceVersionSelection.IsCompatible(version, game, item.Project.ProjectType == "mod" ? loader : ""))
                throw new InvalidDataException($"{item.Project.Title} 的所选版本不兼容 {loader} {game}。");
            var name = ResourceFileName.Format(item.Project.Title, file.Name, nameFormat);
            result.Add(new(Path.Combine(directory, name), item.Project.ProjectType, file.Sha1, file.Size > 0 ? file.Size : null, [file.Url], file.Md5,
                "CurseForge", item.Project.Id, version.Id, version.Dependencies.Where(d => d.Type == "incompatible").ToArray()));
            foreach (var dependency in version.Dependencies.Where(d => d.Type == "required"
                         || d.Type == "optional" && optional.Contains(d.ProjectId ?? "")))
                if (dependency.ProjectId is { } id && !visited.Contains(id))
                    queue.Enqueue((await GetProjectAsync(id, token).ConfigureAwait(false), null));
        }
        if (result.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("资源文件名发生冲突，请使用原始文件名。");
        return result;
    }

    public async Task<IReadOnlyList<DownloadFileRequest>> ResolvePackFilesAsync(IReadOnlyList<CurseForgePackFile> files,
        string instanceDirectory, CancellationToken token, Func<IReadOnlyList<CurseForgeMissingFile>, Task<bool>>? confirmMissing = null)
    {
        var result = new List<DownloadFileRequest>();
        var missing = new List<CurseForgeMissingFile>();
        foreach (var batch in files.Chunk(50))
        {
            using var projectDoc = await GetAsync("/v1/mods", token, new { modIds = batch.Select(file => long.Parse(Id(file.ProjectId))).Distinct().ToArray() }).ConfigureAwait(false);
            var projects = projectDoc.RootElement.GetProperty("data").EnumerateArray().Select(item => ParseProject(item)).ToDictionary(project => project.Id);
            using var fileDoc = await GetAsync("/v1/mods/files", token, new { fileIds = batch.Select(file => long.Parse(Id(file.FileId))).ToArray() }).ConfigureAwait(false);
            var available = fileDoc.RootElement.GetProperty("data").EnumerateArray().ToDictionary(item => Int(item, "id").ToString(CultureInfo.InvariantCulture), item => item);
            foreach (var reference in batch)
            {
                if (!projects.TryGetValue(reference.ProjectId, out var project))
                    throw new InvalidDataException($"整合包项目信息缺失：项目 {reference.ProjectId}，文件 {reference.FileId}。");
                if (!available.TryGetValue(reference.FileId, out var data))
                {
                    missing.Add(new(reference.ProjectId, reference.FileId, project.Title));
                    continue;
                }
                if (Int(data, "modId").ToString(CultureInfo.InvariantCulture) != reference.ProjectId)
                    throw new InvalidDataException($"整合包组件与项目不一致：{project.Title}，文件 {reference.FileId}。");
                var file = ParseVersion(data).PrimaryFile ?? throw new InvalidDataException($"{project.Title} 未提供第三方下载地址，请前往 CurseForge 项目主页下载。");
                var folder = project.ProjectType switch { "resourcepack" => "resourcepacks", "shader" => "shaderpacks", _ => "mods" };
                var destination = ModrinthPackReader.ResolvePath(instanceDirectory, folder + "/" + file.Name);
                result.Add(new(destination, "整合包组件", file.Sha1, file.Size > 0 ? file.Size : null, CommunityResourceService.DownloadSources(file.Url).ToArray(), file.Md5));
            }
        }
        if (result.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("整合包包含文件路径冲突。");
        if (missing.Count > 0 && (confirmMissing is null || !await confirmMissing(missing).ConfigureAwait(false)))
            throw new InvalidDataException("整合包缺少文件信息：" + string.Join("、", missing.Select(file => $"{file.Name}（{file.FileId}）")));
        token.ThrowIfCancellationRequested();
        return result;
    }

    private ModrinthProject ParseProject(JsonElement item, string? type = null)
    {
        type ??= Int(item, "classId") switch { 4471 => "modpack", 6552 => "shader", 12 => "resourcepack", 6945 => "datapack", _ => "mod" };
        var versions = item.TryGetProperty("latestFilesIndexes", out var indexes) ? indexes.EnumerateArray().ToArray() : [];
        var categories = item.TryGetProperty("categories", out var cats) ? cats.EnumerateArray().Select(c => Str(c, "slug")).ToArray() : [];
        var loaders = versions.Select(v => LoaderString(Int(v, "modLoader"))).Where(l => l.Length > 0);
        var project = new ModrinthProject(Int(item, "id").ToString(CultureInfo.InvariantCulture), Str(item, "slug"), Str(item, "name"),
            Str(item, "summary"), type, item.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object ? Str(logo, "url") : "",
            item.TryGetProperty("downloadCount", out var count) ? (long)count.GetDouble() : 0, categories.Concat(loaders).Distinct().ToArray(),
            versions.Select(v => Str(v, "gameVersion")).Distinct().ToArray(), "CurseForge",
            item.TryGetProperty("links", out var links) ? Str(links, "websiteUrl") : "",
            DateTimeOffset.TryParse(Str(item, "dateModified"), out var updated) ? updated : default);
        lock (_projects) _projects[project.Id] = project;
        return project;
    }

    private static ModrinthVersion ParseVersion(JsonElement item)
    {
        var gameTags = item.GetProperty("gameVersions").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        var loaderNames = new[] { "forge", "fabric", "neoforge", "quilt", "liteloader", "iris", "optifine" };
        var loaders = gameTags.Select(s => s.ToLowerInvariant()).Where(loaderNames.Contains).ToArray();
        var games = gameTags.Where(s => s.Length > 0 && (char.IsDigit(s[0]) || s.StartsWith("b1.") || s.StartsWith("a1."))).ToArray();
        var dependencies = item.TryGetProperty("dependencies", out var deps) ? deps.EnumerateArray()
            .Where(d => Int(d, "relationType") is 2 or 3 or 5)
            .Select(d => new ModrinthDependency(Int(d, "modId").ToString(CultureInfo.InvariantCulture), null,
                Int(d, "relationType") == 3 ? "required" : Int(d, "relationType") == 5 ? "incompatible" : "optional")).ToArray() : [];
        var hashes = item.TryGetProperty("hashes", out var hash) ? hash.EnumerateArray().ToArray() : [];
        string? Hash(int algo) => hashes.Where(h => Int(h, "algo") == algo).Select(h => Str(h, "value")).FirstOrDefault();
        var url = FileUrl(item);
        var files = url is not null
            ? new[] { new ModrinthFile(Path.GetFileName(Str(item, "fileName")), url, Hash(1), item.GetProperty("fileLength").GetInt64(), true, Hash(2)) } : [];
        return new(Int(item, "id").ToString(CultureInfo.InvariantCulture), Str(item, "displayName"), Path.GetFileNameWithoutExtension(Str(item, "fileName")),
            Int(item, "releaseType") switch { 1 => "release", 2 => "beta", _ => "alpha" }, DateTimeOffset.TryParse(Str(item, "fileDate"), out var date) ? date : default,
            games, loaders.Length > 0 ? loaders : ["minecraft"], files, dependencies,
            item.TryGetProperty("downloadCount", out var downloads) && downloads.TryGetInt64(out var count) ? count : null);
    }

    private static Uri? FileUrl(JsonElement item)
    {
        var name = Str(item, "fileName");
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')) return null;
        var encoded = Uri.EscapeDataString(name);
        var download = Str(item, "downloadUrl");
        if (download.Length > 0)
        {
            if (!Uri.TryCreate(download, UriKind.Absolute, out var direct) || direct.Scheme != "https") return null;
            var prefix = direct.GetLeftPart(UriPartial.Path);
            return new Uri(prefix[..(prefix.LastIndexOf('/') + 1)] + encoded + direct.Query);
        }
        // PCL also fills missing API URLs from the public ForgeCDN file layout.
        var id = Int(item, "id");
        return id > 0 ? new Uri($"https://edge.forgecdn.net/files/{id / 1000}/{id % 1000}/{encoded}") : null;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken token, object? body = null)
    {
        var key = apiKey();
        var bases = string.IsNullOrWhiteSpace(key) ? new[] { Mirror } : new[] { Official, Mirror };
        Exception? last = null;
        int? authorizationFailure = null;
        foreach (var endpoint in bases)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, endpoint + path);
                if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
                request.Headers.UserAgent.ParseAdd("97Launch/0.1");
                if (endpoint == Official && !string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("x-api-key", key);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (endpoint == Official) authorizationFailure = (int)response.StatusCode;
                    throw new HttpRequestException("CurseForge 官方接口未接受当前凭据。", null, response.StatusCode);
                }
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { last = ex; }
        }
        if (authorizationFailure is { } status)
            throw new HttpRequestException($"CurseForge 官方接口鉴权失败（HTTP {status}），镜像也暂不可用。", last);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(key)
            ? "CurseForge 服务暂时不可用，请稍后重试。" : "CurseForge 官方与镜像接口均不可用，请检查网络连接或稍后重试。", last);
    }

    private static string Id(string value) => long.TryParse(value, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new InvalidDataException("CurseForge 项目或文件 ID 无效。");
    private static bool IsAvailable(JsonElement item) => !item.TryGetProperty("isAvailable", out var available) || available.ValueKind != JsonValueKind.False;
    private static int Int(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
    private static string Str(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int? LoaderId(string value) => value.ToLowerInvariant() switch { "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => null };
    private static string LoaderString(int value) => value switch { 1 => "forge", 4 => "fabric", 5 => "quilt", 6 => "neoforge", _ => "" };
}
