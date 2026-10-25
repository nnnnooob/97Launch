namespace Launcher.Core.Downloads;

public sealed record ResourceSearchResult(IReadOnlyList<ModrinthProject> Projects, string Warning, bool HasMore);

public sealed class CommunityResourceService(ModrinthResourceService modrinth, CurseForgeResourceService curseForge)
{
    public async Task<ResourceSearchResult> SearchAsync(string source, string query, string type, string game,
        string loader, string sort, int offset, string? category, string? resolution, CancellationToken token,
        IProgress<ResourceSearchResult>? progress = null)
    {
        var completed = new Dictionary<string, (IReadOnlyList<ModrinthProject> Projects, string Error)>();
        async Task<(IReadOnlyList<ModrinthProject> Projects, string Error)> Fetch(string platform)
        {
            (IReadOnlyList<ModrinthProject> Projects, string Error) result;
            try
            {
                var projects = platform == "CurseForge"
                    ? await curseForge.SearchAsync(query, type, game, loader, sort, offset, category, resolution, token).ConfigureAwait(false)
                    : await modrinth.SearchAsync(query, type, game, loader, sort, token, category, offset, resolution).ConfigureAwait(false);
                result = (projects, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
            { result = ([], platform + "：" + ex.Message); }
            lock (completed)
            {
                completed[platform] = result;
                progress?.Report(new(completed.Values.SelectMany(value => value.Projects).ToArray(),
                    string.Join("；", completed.Values.Select(value => value.Error).Where(error => error.Length > 0)),
                    completed.Values.Any(value => value.Projects.Count == 50)));
            }
            return result;
        }
        var sources = source is "Modrinth" or "CurseForge" ? new[] { source } : ["Modrinth", "CurseForge"];
        var results = await Task.WhenAll(sources.Select(Fetch)).ConfigureAwait(false);
        var projects = new List<ModrinthProject>();
        // Interleave source rankings; names alone are insufficient proof of project identity.
        for (var row = 0; row < results.Max(result => result.Projects.Count); row++)
            foreach (var result in results)
                if (row < result.Projects.Count) projects.Add(result.Projects[row]);
        return new(projects.DistinctBy(project => (project.Source, project.Id)).ToArray(),
            string.Join("；", results.Select(result => result.Error).Where(error => error.Length > 0)),
            results.Any(result => result.Projects.Count == 50));
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(ModrinthProject project, CancellationToken token)
    {
        var versions = project.Source == "CurseForge" ? await curseForge.GetVersionsAsync(project.Id, token).ConfigureAwait(false)
            : await modrinth.GetVersionsAsync(project.Id, token).ConfigureAwait(false);
        return project.Source == "CurseForge" && project.ProjectType == "datapack"
            ? versions.Select(version => version with { Loaders = ["datapack"] }).ToArray() : versions;
    }

    public Task<ModrinthProject> GetProjectAsync(string source, string id, CancellationToken token) =>
        source == "CurseForge" ? curseForge.GetProjectAsync(id, token) : modrinth.GetProjectAsync(id, token);

    public async Task<ModrinthProject> GetDependencyProjectAsync(string source, ModrinthDependency dependency, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(dependency.ProjectId)) return await GetProjectAsync(source, dependency.ProjectId, token).ConfigureAwait(false);
        return await modrinth.GetVersionProjectAsync(dependency.VersionId ?? "", token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadFileRequest>> ResolveFilesAsync(ModrinthProject project, string game,
        string loader, string directory, string versionId, IReadOnlySet<string> optional, string nameFormat, CancellationToken token)
    {
        var files = project.Source == "CurseForge"
            ? await curseForge.ResolveFilesAsync(project, versionId, game, loader, directory, optional, nameFormat, token).ConfigureAwait(false)
            : await modrinth.ResolveDownloadFilesAsync(project, game, loader, directory, token, versionId, optional, nameFormat).ConfigureAwait(false);
        return files.Select(file => file with { Sources = file.Sources.SelectMany(DownloadSources).Distinct().ToArray() }).ToArray();
    }

    public static IEnumerable<Uri> DownloadSources(Uri original)
    {
        var canonical = original.Host.Replace("-service.overwolf.wtf", ".forgecdn.net", StringComparison.OrdinalIgnoreCase);
        if (canonical is "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "media.forgecdn.net")
        {
            var variants = new[]
            {
                new UriBuilder(original) { Host = "mediafilez.forgecdn.net" }.Uri,
                new UriBuilder(original) { Host = original.Host.Replace("edge.", "mediafilez.").Replace("media.", "mediafilez.") }.Uri,
                new UriBuilder(original) { Host = canonical }.Uri,
                new UriBuilder(original) { Host = original.Host.Replace("media.", "edge.") }.Uri,
                original,
                new Uri("https://mod.mcimirror.top" + original.AbsolutePath.Replace("%2B", "+", StringComparison.OrdinalIgnoreCase) + original.Query),
            };
            foreach (var uri in variants.Distinct()) yield return uri;
            yield break;
        }
        yield return original;
        if (original.Host == "cdn.modrinth.com")
            yield return new Uri("https://mod.mcimirror.top" + original.PathAndQuery);
    }

    public static string Website(ModrinthProject project) => !string.IsNullOrEmpty(project.WebsiteUrl) ? project.WebsiteUrl
        : project.Source == "CurseForge" ? "https://www.curseforge.com/minecraft/" : "https://modrinth.com/project/" + Uri.EscapeDataString(project.Id);
}
