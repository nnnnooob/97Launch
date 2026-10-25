using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.App;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Launcher.Core.Versions;
using Launcher.Core.Install;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program {
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object OutputGate = new();
    internal static TextWriter? LaunchOutput;
    internal static void Send(object value) { lock (OutputGate) {
        try { (LaunchOutput ?? Console.Out).WriteLine(JsonSerializer.Serialize(value, Json)); }
        catch (IOException) when (LaunchHost.Active) { }
        catch (ObjectDisposedException) when (LaunchHost.Active) { }
    } }
    private static string Text(JsonNode? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    private static bool Flag(JsonNode? node, string key) => node?[key]?.GetValue<bool>() ?? false;
    private static string[] Strings(JsonNode? node, string key) => node?[key]?.AsArray().Select(v => v!.GetValue<string>()).ToArray() ?? [];
    public static async Task<int> Main(string[] arguments) {
        if(UpdateBootstrap.HandleArguments(arguments)) return 0;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(new WorkerLog()).CreateLogger();
        if(arguments.Length == 2 && arguments[0] == "--launch-host") return await LaunchHost.RunAsync(arguments[1]);
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        try {
            var line = await Console.In.ReadLineAsync() ?? throw new IOException("缺少任务参数。");
            var request = JsonNode.Parse(line)!;
            _ = Task.Run(async () => { while (await Console.In.ReadLineAsync() is { } command) if (command == "cancel") cancel.Cancel(); else { DownloadInteraction.Answer(command); DropInteraction.Answer(command); } cancel.Cancel(); });
            var result = await RunAsync(request, cancel.Token);
            Send(new { type = "result", data = result }); return 0;
        } catch (OperationCanceledException) { Send(new { type = "error", error = "操作已取消", canceled = true }); return 2; }
        catch (Exception ex) { Serilog.Log.Error(ex, "实例任务执行失败"); Send(new { type = "error", error = ex.Message }); return 1; }
    }
    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress> { public void Report(InstallProgress value) => report(value); }
    private static async Task<object> RunAsync(JsonNode request, CancellationToken token) {
        if (Text(request, "action") == "dropImport") return await DropActions.RunAsync(request, token);
        if (Text(request,"action").StartsWith("settings",StringComparison.Ordinal)) return await SettingsActions.RunAsync(request, token);
        if (Text(request, "action").StartsWith("download", StringComparison.Ordinal)) return await DownloadActions.RunAsync(request, token);
        var entry = request["instance"]!;
        var path = Path.GetFullPath(Text(entry, "path")); var root = Path.GetFullPath(Text(entry, "root"));
        InstanceContentService.ValidatePath(root, path);
        if (!GameFolderService.PathsEqual(Path.GetDirectoryName(path)!, Path.Combine(root, "versions")) || !Directory.Exists(path)) throw new IOException("实例目录无效。");
        var loader = Text(entry, "loaderId", Text(entry, "loader").Split(' ')[0]);
        var moddable = loader is "Fabric" or "Forge" or "NeoForge" or "LiteLoader" or "Quilt";
        var instance = new InstanceItem(Path.GetFileName(path), path, Text(entry, "version"), loader, moddable, "", IsRelease: Text(entry, "releaseType") == "release", MinecraftVersionId: Text(entry, "version"));
        var settingsPath = Path.Combine(AppInfo.DataDirectory, "settings.json");
        var settings = new SettingsStore(settingsPath);
        var globals = settings.Load();
        if (!File.Exists(settingsPath)) {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "97Launch", "settings.json");
            globals = new SettingsStore(legacy).Load();
        }
        // The caller sends the effective defaults, so Qt and the worker resolve isolation identically.
        globals = DownloadActions.EffectiveSettings(request, settings);
        settings.RequestDefaults = globals;
        globals.SelectedGameFolderPath = root;
        if (!File.Exists(settingsPath)) settings.Save(globals);
        var local = new InstanceLaunchSettingsStore();
        var context = new InstanceContext(instance, root, MinecraftLaunchService.ResolveGameDirectory(instance, root, local.Load(path, globals), globals));
        var folders = new GameFolderService { Root = root };
        var downloader = new PclFileDownloadManager();
        var versions = new MinecraftVersionService(NullLogger<MinecraftVersionService>.Instance, Path.Combine(AppInfo.DataDirectory, "cache", "versions"));
        var java = new JavaRuntimeService(settings, folders, downloader, new JavaRuntimeChangeNotifier());
        var installer = new MinecraftInstallService(versions, folders, settings, local, downloader, java);
        var content = new InstanceContentService(settings, local, new GameSessionManager());
        var scanner = new InstanceScanner(NullLogger<InstanceScanner>.Instance);
        var modification = new InstanceModificationService(content, scanner, installer);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("97Launch/0.1 (Qt-instance-worker)");
        var resources = new CommunityResourceService(new ModrinthResourceService(http), new CurseForgeResourceService(http, new CommunityCredentials().ReadKey));
        var mods = new ModInstallService(resources, content, installer);
        var online = new LocalModOnlineService(resources, mods);
        var progress = new SyncProgress(p => Send(new { type = "progress", detail = p.Detail, progress = Math.Clamp((p.Stage + p.Progress) / 2, 0, 1), speed = p.BytesPerSecond, stage = p.Stage, fraction = p.Progress, preserve = p.PreserveOtherStages }));
        var args = request["args"] ?? new JsonObject();
        var action = Text(request, "action");
        var kind = Text(args, "kind", "mods") switch { "saves" => ContentKind.Saves, "screenshots" => ContentKind.Screenshots, "resourcepacks" => ContentKind.ResourcePacks, "shaderpacks" => ContentKind.ShaderPacks, _ => ContentKind.Mods };
        async Task<LocalContent[]> Selected() {
            var paths = Strings(args, "paths");
            var files = await content.ScanAsync(context, kind, token);
            var selected = files.Where(f => paths.Any(p => GameFolderService.PathsEqual(f.Path, p))).ToArray();
            if (paths.Length != selected.Length) throw new IOException("选中的文件发生变化，请刷新列表。");
            return selected;
        }
        switch (action) {
            case "scan": {
                var files = await content.ScanAsync(context, kind, token);
                var diagnostics = kind == ContentKind.Mods ? ModDiagnostics.Inspect(files, instance.MinecraftVersionId, loader) : new Dictionary<string, string[]>();
                var packs = ReadPacks(content, context);
                var iris = DecodeProperty(content.ReadOption(context, "config/iris.properties", "shaderPack="));
                var opti = content.ReadOption(context, "optionsshaders.txt", "shaderPack=");
                var shaderEnabled = content.ReadOption(context, "config/iris.properties", "enableShaders=") != "false";
                var format = kind == ContentKind.ResourcePacks ? await content.ResourceFormatAsync(context, token) : null;
                return new { rows = files.Select(f => new { file = f, diagnostics = diagnostics.TryGetValue(f.Path, out var issues) ? issues : Array.Empty<string>(), active = kind == ContentKind.ResourcePacks ? packs.Contains(f.FileName) || packs.Contains("file/" + f.FileName) : shaderEnabled && iris == f.FileName || opti == f.FileName,
                    compatible = format is null || (f.MinimumFormat is null || format >= f.MinimumFormat) && (f.MaximumFormat is null || format <= f.MaximumFormat) }), directory = context.DirectoryFor(kind), shared = context.Shared, interrupted = ModInstallService.HasInterruptedChange(context.GameDirectory) };
            }
            case "importPreview": {
                var conflicts = new List<string>();
                if (kind != ContentKind.Saves) foreach (var source in Strings(args, "sources")) {
                    var target = InstanceContentService.SafeChild(context.DirectoryFor(kind), LocalContentReader.EnabledName(Path.GetFileName(source)));
                    if ((File.Exists(target) || Directory.Exists(target)) && !GameFolderService.PathsEqual(source, target)) conflicts.Add(Path.GetFileName(target));
                }
                return new { conflicts };
            }
            case "import": await content.ImportAsync(context, kind, Strings(args, "sources"), Flag(args, "overwrite"), token); break;
            case "delete": await content.DeleteAsync(context, kind, await Selected(), token); break;
            case "toggle": await content.ToggleModsAsync(context, await Selected(), Flag(args, "enabled"), token); break;
            case "checkUpdates": {
                var selected = await Selected();
                var rows = new List<object>();
                foreach (var file in selected) {
                    token.ThrowIfCancellationRequested(); Send(new { type = "progress", detail = "检查更新 · " + file.Name });
                    try { rows.Add(new { path = file.Path, online = await online.LookupAsync(context, file, token, true), error = "" }); }
                    catch (Exception ex) when (!token.IsCancellationRequested) { rows.Add(new { path = file.Path, online = (LocalModOnlineInfo?)null, error = ex.Message }); }
                }
                return new { rows };
            }
            case "update": {
                var selected = await Selected(); var updates = new List<(LocalContent, LocalModOnlineInfo)>();
                foreach (var file in selected) {
                    var supplied = args["updates"]?.AsArray().FirstOrDefault(n => Text(n, "path") == file.Path)?["online"]?.Deserialize<LocalModOnlineInfo>(Json);
                    var info = supplied ?? await online.LookupAsync(context, file, token, true);
                    if (info?.Update is not null) updates.Add((file, info));
                }
                if (updates.Count == 0) throw new IOException("没有适用的更新。");
                await online.UpdateBatchAsync(context, updates, progress, token); break;
            }
            case "repair": {
                var file = (await Selected()).Single();
                var info = await online.LookupAsync(context, file, token, true);
                if (info is null || info.IsStale) throw new IOException("无法识别此 Mod 的来源，请先联网检查。");
                await mods.InstallAsync(context, [new(info.Project, info.Current, file, await InstanceModificationService.HashAsync(file.Path, token))], "{original}", progress, token); break;
            }
            case "restoreMods": await mods.RestoreLastAsync(context, token); break;
            case "setPack": {
                var file = (await Selected()).Single(); var enabled = Flag(args, "enabled");
                if (kind == ContentKind.ResourcePacks) {
                    var packs = ReadPacks(content, context); var key = packs.Contains(file.FileName) ? file.FileName : "file/" + file.FileName;
                    var move = args["move"]?.GetValue<int>() ?? 0;
                    if (move != 0) { var index = packs.IndexOf(key); if (index >= 0) { var target = Math.Clamp(index + move, 0, packs.Count - 1); packs.RemoveAt(index); packs.Insert(target, key); } }
                    else if (enabled) { if (!packs.Contains(key)) packs.Add(key); } else packs.Remove(key);
                    await content.WriteOptionAsync(context, "options.txt", "resourcePacks:", JsonSerializer.Serialize(packs), token);
                    var incompatible = ParseList(content.ReadOption(context, "options.txt", "incompatibleResourcePacks:"));
                    if (enabled && Flag(args, "incompatible")) { if (!incompatible.Contains(key)) incompatible.Add(key); } else incompatible.Remove(key);
                    await content.WriteOptionAsync(context, "options.txt", "incompatibleResourcePacks:", JsonSerializer.Serialize(incompatible), token);
                } else {
                    var installedMods = await content.ScanAsync(context, ContentKind.Mods, token);
                    var hasIris = installedMods.Any(m => m.Enabled && m.ModId is "iris" or "oculus");
                    var hasOpti = InstanceInstallManifest.TryLoad(path)?.OptiFineVersion is not null || loader == "OptiFine" || installedMods.Any(m => m.Enabled && m.FileName.StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase));
                    if (!hasIris && !hasOpti) throw new IOException("请先在修改页面安装 OptiFine，或下载兼容的 Iris / Oculus。");
                    if (hasIris) { await content.WriteOptionAsync(context, "config/iris.properties", "shaderPack=", enabled ? EncodeProperty(file.FileName) : "", token); await content.WriteOptionAsync(context, "config/iris.properties", "enableShaders=", enabled ? "true" : "false", token); }
                    if (hasOpti) await content.WriteOptionAsync(context, "optionsshaders.txt", "shaderPack=", enabled ? file.FileName : "OFF", token);
                }
                break;
            }
            case "exportFile": {
                var file = (await Selected()).Single(); var destination = Text(args, "destination");
                if (Flag(args, "backup")) { var backup = Path.Combine(root, "97Launch", "backups", "saves"); Directory.CreateDirectory(backup); destination = Path.Combine(backup, file.FileName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"); }
                if (string.IsNullOrWhiteSpace(destination)) throw new IOException("请选择保存位置。");
                if (GameFolderService.PathsEqual(file.Path, destination) || destination.StartsWith(file.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("请保存在原文件或文件夹之外。");
                var temp = destination + ".97launch-" + Guid.NewGuid().ToString("N");
                try {
                    if (Directory.Exists(file.Path)) {
                        using var archive = ZipFile.Open(temp, ZipArchiveMode.Create);
                        foreach (var source in InstanceContentService.Files(file.Path)) {
                            token.ThrowIfCancellationRequested();
                            var name = Path.GetRelativePath(file.Path, source).Replace('\\', '/');
                            await using var input = File.OpenRead(source); await using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                            await input.CopyToAsync(output, token);
                        }
                    }
                    else { await using var input = File.OpenRead(file.Path); await using var output = File.Create(temp); await input.CopyToAsync(output, token); }
                    token.ThrowIfCancellationRequested(); File.Move(temp, destination, true);
                } finally { if (File.Exists(temp)) File.Delete(temp); }
                return new { destination };
            }
            case "metadata": {
                var installed = await modification.ReadInstalledAsync(context, token);
                var selected = new Dictionary<string, LoaderVersionOption>();
                var caches = new Dictionary<string, IReadOnlyList<LoaderVersionOption>>();
                var warnings = new List<string>();
                var values = new Dictionary<string, string?> { [installed.Loader ?? ""] = installed.LoaderVersion, ["LiteLoader"] = installed.LiteLoaderVersion,
                    ["OptiFine"] = installed.OptiFineVersion, ["Fabric API"] = installed.FabricApiVersion, ["OptiFabric"] = installed.OptiFabricVersion };
                foreach (var pair in values.Where(p => p.Key.Length > 0 && p.Value is not null)) {
                    LoaderVersionOption? match = null;
                    try {
                        var list = await installer.GetLoaderVersionsAsync(pair.Key, installed.MinecraftVersion, token);
                        caches[installed.MinecraftVersion + "/" + pair.Key] = list;
                        match = list.FirstOrDefault(v => v.Id == pair.Value || v.Version == pair.Value)
                            ?? list.FirstOrDefault(v => pair.Key == "OptiFine" && v.Version.Replace(' ', '_').EndsWith(pair.Value!.Replace("HD_U_", "").Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
                    } catch (Exception ex) when (!token.IsCancellationRequested) { warnings.Add(pair.Key + "：" + ex.Message); }
                    selected[pair.Key] = (match ?? new(pair.Value!, "当前已安装，可保留或移除", false)) with { Id = pair.Value };
                }
                return new { installed, selected, caches, warnings, recovery = File.Exists(InstanceModificationService.JournalPath(path)) };
            }
            case "componentVersions": return new { component = Text(args, "component"), game = Text(args, "game"), versions = await installer.GetLoaderVersionsAsync(Text(args, "component"), Text(args, "game"), token) };
            case "modify": {
                var options = args["request"]!.Deserialize<InstallRequest>(Json) ?? throw new IOException("修改参数无效。");
                options = options with { Version = instance.MinecraftVersionId, GameDirectory = root, OutputDirectory = null };
                if (File.Exists(InstanceModificationService.JournalPath(path))) throw new IOException("上次实例修改未完成，请先恢复备份。");
                if (options.FabricApiVersion is not null && options.Loader != "Fabric") throw new IOException("Fabric API 需要同时选择 Fabric。");
                if (options.OptiFabricVersion is not null && (options.Loader != "Fabric" || options.OptiFineVersion is null || !PclLoaderRules.SupportsOptiFabric(options.Version))) throw new IOException("OptiFabric 与当前组件组合不兼容。");
                if (options.OptiFineVersion is not null) {
                    if (options.Loader == "NeoForge" || options.Loader == "Fabric" && !PclLoaderRules.SupportsOptiFineWithFabric(options.Version) || options.Loader == "Forge" && PclLoaderRules.IsForgeOptiFineRangeUnsupported(options.Version))
                        throw new IOException("OptiFine 与 " + options.Loader + " 不兼容。");
                    if (options.Loader == "Forge") {
                        var opti = (await installer.GetLoaderVersionsAsync("OptiFine", options.Version, token)).FirstOrDefault(v => v.Id == options.OptiFineVersion || v.Version == options.OptiFineVersion);
                        if (opti is null || !PclLoaderRules.IsOptiFineCompatibleWithForge(opti.Compatibility, options.Version, options.LoaderVersion ?? "")) throw new IOException("所选 OptiFine 与 Forge 不兼容。");
                    }
                }
                if (options.LiteLoaderVersion is not null && Version.TryParse(options.Version.Split('-', '+')[0], out var gameVersion) && gameVersion >= new Version(1, 13)) throw new IOException("此 Minecraft 版本不支持 LiteLoader。");
                var result = await modification.ModifyAsync(context, options, progress, token);
                return new { path = result };
            }
            case "recoverModification": await modification.RecoverAsync(context, token); break;
            case "exportPack": {
                var options = args["options"]!.Deserialize<InstanceExportOptions>(Json) ?? throw new IOException("导出参数无效。");
                ModrinthModpackInstaller.ValidateName(options.Name);
                if (string.IsNullOrWhiteSpace(options.Version)) throw new IOException("请填写整合包版本。");
                var destination = Text(args, "destination");
                await new InstanceExportService(content, modification).ExportAsync(context, options, destination,
                    _ => Task.FromResult(Flag(args, "allowUnrecognized")), progress, token);
                return new { destination };
            }
            case "searchResources": {
                var type = Text(args, "resourceType", "mod");
                return await resources.SearchAsync(Text(args, "source", "自动选择"), Text(args, "query"), type, instance.MinecraftVersionId,
                    type == "mod" ? loader.ToLowerInvariant() : "", "relevance", args["offset"]?.GetValue<int>() ?? 0, null, null, token);
            }
            case "resourceVersions": {
                var project = args["project"]!.Deserialize<ModrinthProject>(Json)!;
                var list = await resources.GetVersionsAsync(project, token);
                return new { versions = list.Where(v => v.GameVersions.Contains(instance.MinecraftVersionId) && (project.ProjectType != "mod" || v.Loaders.Contains(loader.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase))) };
            }
            case "installResource": {
                var project = args["project"]!.Deserialize<ModrinthProject>(Json)!; var version = args["version"]!.Deserialize<ModrinthVersion>(Json)!;
                if (project.ProjectType == "mod") await mods.DownloadAsync(context, project, version, "{original}", progress, token);
                else {
                    using var operation = await content.AcquireAsync(context, token);
                    var targetKind = project.ProjectType == "shader" ? ContentKind.ShaderPacks : ContentKind.ResourcePacks;
                    var folder = context.DirectoryFor(targetKind); Directory.CreateDirectory(folder);
                    var files = await resources.ResolveFilesAsync(project, instance.MinecraftVersionId, "", folder, version.Id, new HashSet<string>(), "{original}", token);
                    if (files.Any(f => File.Exists(f.Path))) throw new IOException("同名资源已存在，请先检查原文件。");
                    await installer.DownloadFilesAsync(root, "下载资源", files, progress, token);
                }
                break;
            }
            default: throw new InvalidOperationException("未知任务：" + action);
        }
        return new { success = true };
    }
    private static List<string> ParseList(string text) { try { return JsonSerializer.Deserialize<List<string>>(text) ?? []; } catch (JsonException) { return []; } }
    private static List<string> ReadPacks(InstanceContentService content, InstanceContext context) => ParseList(content.ReadOption(context, "options.txt", "resourcePacks:"));
    private static string DecodeProperty(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\\u([0-9a-fA-F]{4})|\\(.)", m => m.Groups[1].Success ? ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString() : m.Groups[2].Value);
    private static string EncodeProperty(string text) => string.Concat(text.Select(c => c > 127 ? "\\u" + ((int)c).ToString("X4") : c is '\\' or '=' or ':' or ' ' ? "\\" + c : c.ToString()));
}

internal sealed class WorkerLog : Serilog.Core.ILogEventSink {
    public void Emit(Serilog.Events.LogEvent value) {
        try {
            var directory=Path.Combine(AppInfo.DataDirectory,"logs");Directory.CreateDirectory(directory);
            using var guard=new Mutex(false,@"Local\97LaunchQt-worker-log");
            bool entered;
            try { entered=guard.WaitOne(TimeSpan.FromSeconds(1)); } catch(AbandonedMutexException){entered=true;}
            if(!entered)return;
            try {
                var path=Path.Combine(directory,"worker.log");
                if(File.Exists(path)&&new FileInfo(path).Length>8*1024*1024)File.Move(path,path+".previous",true);
                using var buffer=new StringWriter();new SafeLogFormatter().Format(value,buffer);
                File.AppendAllText(path,buffer.ToString());
            } finally {guard.ReleaseMutex();}
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
    }
}
