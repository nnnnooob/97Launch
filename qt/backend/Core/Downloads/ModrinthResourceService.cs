using System.Text.Json;

namespace Launcher.Core.Downloads;

public sealed record ModrinthProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string ProjectType,
    string IconUrl,
    long Downloads,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> GameVersions,
    string Source = "Modrinth",
    string WebsiteUrl = "",
    DateTimeOffset Updated = default);

public sealed record ModrinthFile(string Name, Uri Url, string? Sha1, long Size, bool Primary, string? Md5 = null);

public sealed record ModrinthDependency(string? ProjectId, string? VersionId, string Type);

public sealed record ModrinthVersion(
    string Id,
    string Name,
    string Number,
    string Type,
    DateTimeOffset Published,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependency> Dependencies,
    long? Downloads = null)
{
    public ModrinthFile? PrimaryFile => Files.FirstOrDefault(file => file.Primary) ?? Files.FirstOrDefault();
}

/// <summary>Searches Modrinth and resolves compatible files plus required dependencies.</summary>
public sealed class ModrinthResourceService
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly HttpClient _http;

    public ModrinthResourceService() : this(SharedHttp) { }

    public ModrinthResourceService(HttpClient httpClient) => _http = httpClient;

    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string query,
        string projectType,
        string gameVersion,
        string loader,
        string sort,
        CancellationToken cancellationToken = default,
        string? category = null,
        int offset = 0,
        string? extraCategory = null)
    {
        var facets = new List<string[]> { new[] { $"project_type:{projectType}" } };
        if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "全部")
            facets.Add(new[] { $"versions:{gameVersion}" });
        if (!string.IsNullOrWhiteSpace(loader) && loader != "任意加载器")
            facets.Add(new[] { $"categories:{loader.ToLowerInvariant()}" });
        if (!string.IsNullOrWhiteSpace(category))
            facets.Add(new[] { $"categories:{category.ToLowerInvariant()}" });
        if (!string.IsNullOrWhiteSpace(extraCategory))
            facets.Add(new[] { $"categories:{extraCategory.ToLowerInvariant()}" });

        var index = sort switch
        {
            "最新" => "updated",
            "下载量" => "downloads",
            "热度" => "relevance",
            _ => "relevance",
        };
        var uri = new Uri("https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query.Trim())
            + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets))
            + "&index=" + Uri.EscapeDataString(index)
            + "&limit=50&offset=" + Math.Max(0, offset));

        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return [];

        return hits.EnumerateArray().Select(hit => new ModrinthProject(
                GetString(hit, "project_id"),
                GetString(hit, "slug"),
                GetString(hit, "title"),
                GetString(hit, "description"),
                GetString(hit, "project_type"),
                GetString(hit, "icon_url"),
                GetInt64(hit, "downloads"),
                GetStrings(hit, "categories"),
                GetStrings(hit, "versions"),
                Updated: DateTimeOffset.TryParse(GetString(hit, "date_modified"), out var updated) ? updated : default))
            .Where(project => project.Id.Length > 0)
            .ToArray();
    }

    public async Task<ModrinthProject> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new Uri("https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId)), cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new(GetString(root, "id"), GetString(root, "slug"), GetString(root, "title"),
            GetString(root, "description"), GetString(root, "project_type"), GetString(root, "icon_url"),
            GetInt64(root, "downloads"), GetStrings(root, "categories"), GetStrings(root, "game_versions"),
            Updated: DateTimeOffset.TryParse(GetString(root, "updated"), out var updated) ? updated : default);
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var uri = new Uri("https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId) + "/version?include_changelog=false");
        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.EnumerateArray().Select(ParseVersion)
            .Where(version => version.PrimaryFile is not null)
            .OrderByDescending(version => version.Published).ToArray();
    }

    private static ModrinthVersion ParseVersion(JsonElement root)
    {
        var files = root.TryGetProperty("files", out var fileArray)
            ? fileArray.EnumerateArray().Select(file =>
            {
                if (!Uri.TryCreate(GetString(file, "url"), UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("https" or "http"))
                    throw new InvalidDataException("资源版本包含无效的文件地址。");
                var sha1 = file.TryGetProperty("hashes", out var hashes) ? GetString(hashes, "sha1") : string.Empty;
                return new ModrinthFile(Path.GetFileName(GetString(file, "filename")), uri,
                    string.IsNullOrEmpty(sha1) ? null : sha1, GetInt64(file, "size"),
                    file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
            }).ToArray() : [];
        var dependencies = root.TryGetProperty("dependencies", out var array)
            ? array.EnumerateArray().Select(dependency => new ModrinthDependency(
                GetString(dependency, "project_id"), GetString(dependency, "version_id"),
                GetString(dependency, "dependency_type"))).ToArray() : [];
        DateTimeOffset.TryParse(GetString(root, "date_published"), out var published);
        return new(GetString(root, "id"), GetString(root, "name"), GetString(root, "version_number"),
            GetString(root, "version_type"), published, GetStrings(root, "game_versions"),
            GetStrings(root, "loaders"), files, dependencies,
            root.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : null);
    }

    public async Task<IReadOnlyList<DownloadFileRequest>> ResolveDownloadFilesAsync(
        ModrinthProject project,
        string gameVersion,
        string loader,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        string? versionId = null,
        IReadOnlySet<string>? optionalDependencies = null,
        string? fileNameFormat = null)
    {
        var requests = new List<DownloadFileRequest>();
        var pending = new Queue<(string ProjectId, string? VersionId)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedProjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((project.Id, versionId));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            if (!visited.Add(current.VersionId ?? current.ProjectId)) continue;
            if (visited.Count > 40)
                throw new InvalidDataException("Mod 依赖数量超出安全上限。");

            using var versionDocument = await GetVersionAsync(
                current.ProjectId, current.VersionId, gameVersion, loader, cancellationToken).ConfigureAwait(false);
            var version = versionDocument.RootElement;
            var resolved = ParseVersion(version);
            var resolvedProject = GetString(version, "project_id");
            if (current.ProjectId.Length > 0 && current.ProjectId != resolvedProject)
                throw new InvalidDataException("必要前置返回的版本不属于所请求的项目。");
            if (!ResourceVersionSelection.IsCompatible(resolved, gameVersion, loader))
                throw new InvalidDataException($"{resolved.Name} 不兼容 {gameVersion} / {loader}，无法安装必要前置。");
            if (resolvedProjects.TryGetValue(resolvedProject, out var previousVersion))
            {
                if (previousVersion != resolved.Id)
                    throw new InvalidDataException($"必要前置版本冲突：项目 {resolvedProject} 同时要求两个不同版本。");
                continue;
            }
            resolvedProjects[resolvedProject] = resolved.Id;
            var files = version.TryGetProperty("files", out var fileArray)
                ? fileArray.EnumerateArray().ToArray()
                : [];
            var file = files.FirstOrDefault(item => item.TryGetProperty("primary", out var primary)
                && primary.ValueKind == JsonValueKind.True);
            if (file.ValueKind == JsonValueKind.Undefined && files.Length > 0) file = files[0];
            if (file.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException($"Modrinth 项目 {current.ProjectId} 没有可下载文件。");

            var fileName = Path.GetFileName(GetString(file, "filename"));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException("Modrinth 返回的文件名无效。");
            if (fileNameFormat is not null && fileNameFormat != ResourceFileName.Default)
            {
                var title = current.ProjectId == project.Id ? project.Title
                    : (await GetVersionProjectAsync(GetString(version, "id"), cancellationToken).ConfigureAwait(false)).Title;
                fileName = ResourceFileName.Format(title, fileName, fileNameFormat);
            }
            if (!Uri.TryCreate(GetString(file, "url"), UriKind.Absolute, out var url))
                throw new InvalidDataException($"Modrinth 文件地址无效：{fileName}");

            var hashes = file.TryGetProperty("hashes", out var hashObject) ? hashObject : default;
            var sha1 = hashes.ValueKind == JsonValueKind.Object ? GetString(hashes, "sha1") : string.Empty;
            var size = file.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                ? value
                : (long?)null;
            requests.Add(new DownloadFileRequest(
                Path.Combine(outputDirectory, fileName),
                project.ProjectType,
                string.IsNullOrWhiteSpace(sha1) ? null : sha1,
                size,
                [url], ResourceSource: "Modrinth", ProjectId: resolvedProject, VersionId: resolved.Id,
                Conflicts: resolved.Dependencies.Where(d => d.Type == "incompatible").ToArray()));

            if (!version.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var dependency in dependencies.EnumerateArray())
            {
                var dependencyProject = GetString(dependency, "project_id");
                var dependencyVersion = GetString(dependency, "version_id");
                var dependencyType = GetString(dependency, "dependency_type");
                if (dependencyType != "required" && !(dependencyType == "optional"
                    && optionalDependencies is not null
                    && (optionalDependencies.Contains(dependencyProject) || optionalDependencies.Contains(dependencyVersion)))) continue;
                if (dependencyProject.Length == 0 && dependencyVersion.Length == 0)
                    throw new InvalidDataException($"{resolved.Name} 声明的必要前置没有可解析的项目或版本，请查看项目主页。");
                pending.Enqueue((dependencyProject, string.IsNullOrWhiteSpace(dependencyVersion) ? null : dependencyVersion));
            }
        }

        if (requests.Select(request => Path.GetFullPath(request.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Count)
            throw new InvalidDataException("资源文件名发生冲突，请使用原始文件名。");
        return requests
            .GroupBy(request => Path.GetFullPath(request.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<ModrinthProject> GetVersionProjectAsync(string versionId, CancellationToken token)
    {
        using var response = await SendAsync(new Uri("https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(versionId)), token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return await GetProjectAsync(GetString(doc.RootElement, "project_id"), token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetVersionAsync(
        string projectId,
        string? versionId,
        string gameVersion,
        string loader,
        CancellationToken cancellationToken)
    {
        string uri;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            uri = $"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(versionId)}";
        }
        else
        {
            var parameters = new List<string> { "include_changelog=false" };
            if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "全部")
                parameters.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
            if (!string.IsNullOrWhiteSpace(loader) && loader != "任意加载器")
                parameters.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() })));
            uri = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version?{string.Join('&', parameters)}";
        }

        using var response = await SendAsync(new Uri(uri), cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(versionId)) return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        using var versions = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (versions.RootElement.ValueKind != JsonValueKind.Array || versions.RootElement.GetArrayLength() == 0)
            throw new InvalidDataException($"项目 {projectId} 没有适用于 {gameVersion} / {loader} 的版本。");
        var selected = versions.RootElement.EnumerateArray().FirstOrDefault(version => GetString(version, "version_type") == "release");
        if (selected.ValueKind == JsonValueKind.Undefined) selected = versions.RootElement[0];
        return JsonDocument.Parse(selected.GetRawText());
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("97launch/0.1.0");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"Modrinth 请求失败（{(int)status} {status}）：{uri}", null, status);
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long GetInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
}
