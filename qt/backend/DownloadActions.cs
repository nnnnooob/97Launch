using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.App;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Install;
using Launcher.Core.Instances;
using Launcher.Core.Versions;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DownloadInteraction {
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> Pending = new();
    private static int _id;
    public static async Task<bool> ConfirmAsync(string title, string message, string confirm, CancellationToken token) {
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = completion;
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        Program.Send(new { type = "question", id, title, message, confirm });
        try { if (!await completion.Task) throw new OperationCanceledException("用户取消了操作。", token); return true; }
        finally { Pending.TryRemove(id, out _); }
    }
    public static void Answer(string command) {
        try { var value = JsonNode.Parse(command)!; if (Pending.TryGetValue(value["id"]!.GetValue<int>(), out var completion)) completion.TrySetResult(value["answer"]!.GetValue<bool>()); }
        catch (JsonException) { }
    }
}

internal static class DownloadActions {
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Text(JsonNode? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    internal static LauncherSettings EffectiveSettings(JsonNode request, SettingsStore settings) {
        var global = settings.Load();
        if (!File.Exists(settings.FilePath)) global = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "97Launch", "settings.json")).Load();
        var merged = JsonSerializer.SerializeToNode(global)!.AsObject();
        if (request["global"] is JsonObject effective) foreach (var pair in effective) merged[pair.Key] = pair.Value?.DeepClone();
        return merged.Deserialize<LauncherSettings>(Json)!;
    }
    private sealed class ProgressSink : IProgress<InstallProgress> {
        public void Report(InstallProgress p) => Program.Send(new { type = "progress", detail = p.Detail, stage = p.Stage, fraction = p.Progress, preserve = p.PreserveOtherStages, speed = p.BytesPerSecond });
    }
    private sealed class PartialSearch : IProgress<ResourceSearchResult> {
        public void Report(ResourceSearchResult result) => Program.Send(new { type = "partial", data = result });
    }
    public static async Task<object> RunAsync(JsonNode request, CancellationToken token) {
        var args = request["args"] ?? new JsonObject();
        var action = Text(request, "action");
        var root = Path.GetFullPath(Text(args, "root"));
        var settings = new SettingsStore(Path.Combine(AppInfo.DataDirectory, "settings.json"));
        var global = EffectiveSettings(request, settings);
        global.SelectedGameFolderPath = root;
        settings.RequestDefaults = global;
        // Each worker uses a request-local settings snapshot; it never rewrites the user's defaults.
        var folders = new GameFolderService { Root = root };
        var local = new InstanceLaunchSettingsStore();
        var downloader = new PclFileDownloadManager();
        var versions = new MinecraftVersionService(NullLogger<MinecraftVersionService>.Instance, Path.Combine(AppInfo.DataDirectory, "cache", "versions"));
        var java = new JavaRuntimeService(settings, folders, downloader, new JavaRuntimeChangeNotifier());
        var installer = new MinecraftInstallService(versions, folders, settings, local, downloader, java);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("97Launch/0.1 (Qt-download-worker)");
        var curseForge = new CurseForgeResourceService(http, new CommunityCredentials().ReadKey);
        var resources = new CommunityResourceService(new ModrinthResourceService(http), curseForge);
        var content = new InstanceContentService(settings, local, new GameSessionManager());
        var mods = new ModInstallService(resources, content, installer);
        var progress = new ProgressSink();
        if(action is "downloadServerList" or "downloadServerPack" or "downloadServerInstall")
            return await ServerActions.RunAsync(request,new LocalModpackService(installer,curseForge,local,settings),progress,token);
        InstanceContext Context() {
            var entry = request["instance"] ?? throw new IOException("请先选择安装目标。");
            var path = Path.GetFullPath(Text(entry, "path"));
            var gameRoot = Path.GetFullPath(Text(entry, "root"));
            InstanceContentService.ValidatePath(gameRoot, path);
            if (!GameFolderService.PathsEqual(Path.GetDirectoryName(path)!, Path.Combine(gameRoot, "versions")) || !Directory.Exists(path)) throw new IOException("目标实例已不存在，请刷新实例列表。");
            var loader = Text(entry, "loaderId");
            var item = new InstanceItem(Text(entry, "name"), path, Text(entry, "version"), loader, loader is "Fabric" or "Forge" or "NeoForge" or "LiteLoader" or "Quilt", "",
                IsRelease: Text(entry, "releaseType") == "release", MinecraftVersionId: Text(entry, "version"));
            return new(item, gameRoot, MinecraftLaunchService.ResolveGameDirectory(item, gameRoot, local.Load(path, global), global));
        }
        ModrinthProject Project() => args["project"]!.Deserialize<ModrinthProject>(Json)!;
        ModrinthVersion Version() => args["version"]!.Deserialize<ModrinthVersion>(Json)!;
        switch (action) {
            case "downloadManifest": {
                var list = await versions.GetVersionsAsync(args["force"]?.GetValue<bool>() ?? false, token);
                if (list.Count == 0) throw new IOException("未获取到版本列表，请重试。");
                var releases = list.Where(v => v.IsRelease).ToArray();
                var latest = releases.Take(2).ToList();
                var fools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2.0", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite", "20w14a", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine" };
                if (list.FirstOrDefault() is { } newest && !newest.IsRelease && !fools.Contains(newest.Id)) latest.Add(newest);
                object Row(ManifestVersion v, string? label = null) => new { id = v.Id, type = label ?? (v.Type == "release" ? "正式版" : v.Id.Contains("-pre") ? "预发布版" : v.Id.Contains("-rc") ? "发布候选" : "快照"), date = v.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd"), icon = fools.Contains(v.Id) ? "fools" : v.IsOld ? "cobblestone" : v.IsRelease ? "vanilla" : "snapshot" };
                return new { groups = new[] {
                    new { title = "最新版本", expanded = true, rows = latest.Select(v => Row(v)).ToArray() },
                    new { title = "正式版", expanded = false, rows = releases.Skip(2).Select(v => Row(v)).ToArray() },
                    new { title = "预览版", expanded = false, rows = list.Where(v => v.IsSnapshot && !fools.Contains(v.Id)).Select(v => Row(v)).ToArray() },
                    new { title = "远古版", expanded = false, rows = list.Where(v => v.IsOld && !fools.Contains(v.Id)).Select(v => Row(v, "远古版")).ToArray() },
                    new { title = "愚人节版", expanded = false, rows = list.Where(v => fools.Contains(v.Id)).Select(v => Row(v, "愚人节版")).ToArray() }
                }, games = releases.Select(v => v.Id).Distinct() };
            }
            case "downloadComponents": return new { component = Text(args, "component"), game = Text(args, "game"), versions = await installer.GetLoaderVersionsAsync(Text(args, "component"), Text(args, "game"), token) };
            case "downloadTarget": {
                var context=Context();
                var type=Text(args,"resourceType","mod");
                var directory=context.DirectoryFor(type=="mod"?ContentKind.Mods:type=="shader"?ContentKind.ShaderPacks:ContentKind.ResourcePacks);
                if(type=="datapack"){
                    var world=Path.GetFullPath(Text(args,"world"));
                    InstanceContentService.ValidatePath(context.DirectoryFor(ContentKind.Saves),world);
                    if(!GameFolderService.PathsEqual(Path.GetDirectoryName(world)!,context.DirectoryFor(ContentKind.Saves))||!File.Exists(Path.Combine(world,"level.dat")))throw new IOException("请先选择有效的存档世界。");
                    directory=Path.Combine(world,"datapacks");
                }
                return new { directory, isolated=GameFolderService.PathsEqual(context.GameDirectory,context.Instance.Path) };
            }
            case "downloadSearch": {
                var type = Text(args, "resourceType", "mod");
                return await resources.SearchAsync(Text(args, "source", "全部"), Text(args, "query"), type, Text(args, "game", "全部"),
                    type == "datapack" ? "datapack" : type == "resourcepack" ? "任意加载器" : Text(args, "loader", "任意加载器"),
                    Text(args, "sort", "热度"), args["offset"]?.GetValue<int>() ?? 0, Text(args, "category") is { Length: > 0 } category ? category : null,
                    Text(args, "resolution") is { Length: > 0 } resolution ? resolution : null, token, new PartialSearch());
            }
            case "downloadWorlds": {
                var context = Context(); var saves = context.DirectoryFor(ContentKind.Saves);
                return new { worlds = Directory.Exists(saves) ? Directory.GetDirectories(saves).Where(p => File.Exists(Path.Combine(p, "level.dat")) && !new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint)).Select(p => new { name = Path.GetFileName(p), path = p }).ToArray() : [] };
            }
            case "downloadDetails": {
                var project = Project();
                var all = (await resources.GetVersionsAsync(project, token)).Where(v => project.Source == "CurseForge" || project.ProjectType switch { "datapack" => v.Loaders.Contains("datapack"), "mod" => !v.Loaders.All(l => l is "datapack" or "minecraft"), _ => true }).ToArray();
                return new { versions = all, fileNames = all.Where(v => v.PrimaryFile is not null).ToDictionary(v => v.Id, v => ResourceFileName.Format(project.Title, v.PrimaryFile!.Name, global.ResourceFileNameFormat)), games = ResourceVersionSelection.SortGames(all.SelectMany(v => v.GameVersions).Select(ResourceVersionSelection.GameFilterName)), loaders = all.SelectMany(v => v.Loaders).Where(l => l is not ("minecraft" or "datapack")).Distinct(StringComparer.OrdinalIgnoreCase).Select(ResourceVersionSelection.LoaderName).OrderBy(l => l) };
            }
            case "downloadGroups": {
                var all = args["versions"]!.Deserialize<ModrinthVersion[]>(Json)!;
                return new { groups = ResourceVersionSelection.Group(all, Text(args, "game"), Text(args, "loader"),
                    Text(args, "gameFilter") is { Length: > 0 } gf ? gf : null, Text(args, "loaderFilter") is { Length: > 0 } lf ? lf.ToLowerInvariant() : null,
                    args["stable"]?.GetValue<bool>() ?? false, Text(args, "resourceType") == "modpack") };
            }
            case "downloadDependencies": {
                var rows = new List<object>();
                foreach (var dependency in Version().Dependencies.Where(d => d.Type == "required").DistinctBy(d => d.ProjectId ?? d.VersionId)) {
                    try { rows.Add(new { project = await resources.GetDependencyProjectAsync(Project().Source, dependency, token), preferred = dependency.VersionId, error = "" }); }
                    catch (Exception ex) when (!token.IsCancellationRequested) { rows.Add(new { project = (ModrinthProject?)null, preferred = dependency.VersionId, error = ex.Message, dependency }); }
                }
                return new { rows };
            }
            case "downloadInstalled": {
                var context = Context(); var project = Project();
                if (project.ProjectType != "mod") return new { rows = Array.Empty<object>() };
                var all = args["versions"] is JsonArray known?known.Deserialize<ModrinthVersion[]>(Json)!:await resources.GetVersionsAsync(project, token);
                var installed = await mods.FindInstalledAsync(context, project, all, token);
                return new { rows = installed.Select(f => new { id = f.Version?.Id, enabled = f.File.Enabled, name = f.File.Name }) };
            }
            case "downloadStates": {
                var context = Context();
                var files = await content.ScanAsync(context, ContentKind.Mods, token);
                var online = new LocalModOnlineService(resources, mods);
                var identified = new ConcurrentBag<(LocalContent File, LocalModOnlineInfo Online)>();
                using var gate = new SemaphoreSlim(4);
                await Task.WhenAll(files.Select(async file => {
                    await gate.WaitAsync(token);
                    try { var info = await online.LookupAsync(context, file, token); if (info is not null) identified.Add((file, info)); }
                    catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or InvalidDataException or OperationCanceledException) { }
                    finally { gate.Release(); }
                }));
                return new { rows = identified.GroupBy(f => (f.Online.Project.Source, f.Online.Project.Id)).Select(g => new { source = g.Key.Source, id = g.Key.Id,
                    state = (g.Any(f => f.Online.Update is not null && !f.Online.IsStale) ? "可更新" : "已安装") + (g.Any(f => f.Online.IsStale) ? " · 更新信息未能刷新" : "") + (g.All(f => !f.File.Enabled) ? " · 已禁用" : "") + (g.Count() > 1 ? " · 存在重复文件" : "") }) };
            }
            case "downloadInstall": {
                var options = args["request"]!.Deserialize<InstallRequest>(Json)!;
                ModrinthModpackInstaller.ValidateName(options.Name);
                options = options with { GameDirectory = root, OutputDirectory = null };
                await ValidateComponents(installer, options, token);
                var destination = InstanceContentService.SafeChild(Path.Combine(root, "versions"), options.Name);
                await installer.InstallAsync(options, progress, token);
                return new { path = destination };
            }
            case "downloadLocalPack": {
                var name = Text(args, "name");
                await new LocalModpackService(installer, curseForge, local, settings).InstallAsync(Text(args, "archive"), root, name, progress, token);
                return new { path = Path.Combine(root, "versions", name) };
            }
            case "downloadResource": {
                var project = Project(); var version = Version();
                var destination = Text(args, "destination");
                if (destination.Length == 0 && project.ProjectType == "mod") {
                    await mods.DownloadAsync(Context(), project, version, global.ResourceFileNameFormat, progress, token);
                    return new { success = true };
                }
                // Resolve again at execution time: queued selections must still refer to an actual release.
                version = (await resources.GetVersionsAsync(project, token)).FirstOrDefault(v => v.Id == version.Id) ?? throw new IOException("所选版本已不存在，请刷新发布列表。");
                var file = version.PrimaryFile ?? throw new IOException("此版本需要前往项目主页下载。");
                if (destination.Length > 0) {
                    await installer.DownloadFilesAsync(root, "下载资源", [new(destination, "资源", file.Sha1, file.Size, CommunityResourceService.DownloadSources(file.Url).ToArray(), file.Md5)], progress, token, destination);
                    return new { destination };
                }
                if (project.ProjectType == "modpack") {
                    var name = Text(args, "name");
                    await new ModrinthModpackInstaller(installer, settings, local, curseForge).InstallAsync(file, version.Id, root, name, progress, token, project.Source);
                    return new { path = Path.Combine(root, "versions", name) };
                }
                var context = Context();
                var loader = project.ProjectType == "datapack" ? "datapack" : project.Source == "CurseForge" ? "" : Text(args, "loader");
                if (!ResourceVersionSelection.IsCompatible(version, context.Instance.MinecraftVersionId, loader)) throw new IOException("所选发布版本与目标实例不兼容，仅可另存为。");
                content.EnsureWritable(context);
                {
                    using var operation = await content.AcquireAsync(context, token);
                    var folder = context.DirectoryFor(project.ProjectType == "shader" ? ContentKind.ShaderPacks : ContentKind.ResourcePacks);
                    if (project.ProjectType == "datapack") {
                        var world = Path.GetFullPath(Text(args, "world"));
                        InstanceContentService.ValidatePath(context.DirectoryFor(ContentKind.Saves), world);
                        if (!GameFolderService.PathsEqual(Path.GetDirectoryName(world)!, context.DirectoryFor(ContentKind.Saves)) || !File.Exists(Path.Combine(world, "level.dat"))) throw new IOException("请先选择有效的存档世界。");
                        var lockPath = Path.Combine(world, "session.lock");
                        if (File.Exists(lockPath)) { try { using var worldLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); } catch (IOException) { throw new IOException("这个世界正在使用，请退出世界后再安装数据包。"); } }
                        folder = Path.Combine(world, "datapacks");
                    }
                    var fileLoader = project.ProjectType == "datapack" ? "datapack" : version.Loaders.FirstOrDefault(value => value is "iris" or "optifine") ?? "";
                    var files = await resources.ResolveFilesAsync(project, context.Instance.MinecraftVersionId, fileLoader, folder, version.Id, new HashSet<string>(), global.ResourceFileNameFormat, token);
                    await installer.DownloadFilesAsync(root, "下载 " + project.Title, files, progress, token);
                }
                return new { ok = true };
            }
            default: throw new IOException("未知下载操作。");
        }
    }
    private static async Task ValidateComponents(MinecraftInstallService installer, InstallRequest options, CancellationToken token) {
        if (options.FabricApiVersion is not null && options.Loader != "Fabric") throw new IOException("Fabric API 需要同时选择 Fabric。");
        if (options.OptiFabricVersion is not null && (options.Loader != "Fabric" || options.OptiFineVersion is null || !PclLoaderRules.SupportsOptiFabric(options.Version))) throw new IOException("OptiFabric 与当前组件组合不兼容。");
        if (options.OptiFineVersion is not null && (options.Loader == "NeoForge" || options.Loader == "Fabric" && !PclLoaderRules.SupportsOptiFineWithFabric(options.Version) || options.Loader == "Forge" && PclLoaderRules.IsForgeOptiFineRangeUnsupported(options.Version))) throw new IOException("OptiFine 与 " + options.Loader + " 不兼容。");
        if (options.OptiFineVersion is not null && options.Loader == "Forge") {
            var opti = (await installer.GetLoaderVersionsAsync("OptiFine", options.Version, token)).FirstOrDefault(v => v.Id == options.OptiFineVersion || v.Version == options.OptiFineVersion);
            if (opti is null || !PclLoaderRules.IsOptiFineCompatibleWithForge(opti.Compatibility, options.Version, options.LoaderVersion ?? "")) throw new IOException("所选 OptiFine 与 Forge 不兼容。");
        }
        if (options.LiteLoaderVersion is not null && Version.TryParse(options.Version.Split('-', '+')[0], out var game) && game >= new Version(1, 13)) throw new IOException("此 Minecraft 版本不支持 LiteLoader。");
    }
}
