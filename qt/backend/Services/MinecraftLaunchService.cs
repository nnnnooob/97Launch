using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.Extensions.Logging;

namespace Launcher.App.Services;

public sealed record LaunchProgress(string Stage, double? Progress, long BytesPerSecond = 0, int RemainingFiles = 0);
public sealed record LaunchResult(int ProcessId, string JavaVersion);

/// <summary>Prepares Java and Minecraft files, then starts the selected instance.</summary>
public sealed class MinecraftLaunchService
{
    private readonly JavaRuntimeService _java;
    private readonly AccountManager _accounts;
    private readonly SettingsStore _settings;
    private readonly InstanceLaunchSettingsStore _instanceSettings;
    private readonly GameFolderService _folders;
    private readonly PclFileDownloadManager _downloads;
    private readonly GameSessionManager _sessions;
    private readonly ILogger<MinecraftLaunchService> _log;
    private readonly LauncherAuthorizationService _authorization;

    public MinecraftLaunchService(
        JavaRuntimeService java,
        AccountManager accounts,
        SettingsStore settings,
        InstanceLaunchSettingsStore instanceSettings,
        GameFolderService folders,
        PclFileDownloadManager downloads,
        GameSessionManager sessions,
        ILogger<MinecraftLaunchService> log,
        LauncherAuthorizationService authorization)
    {
        _java = java;
        _accounts = accounts;
        _settings = settings;
        _instanceSettings = instanceSettings;
        _folders = folders;
        _downloads = downloads;
        _sessions = sessions;
        _log = log;
        _authorization = authorization;
    }

    public async Task<JavaRuntimeCheck> CheckInstalledJavaAsync(
        InstanceItem instance,
        CancellationToken cancellationToken = default)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(instance.Path))!)!;
        var profiles = await ReadProfileChainAsync(instance.Path, gameRoot, cancellationToken);
        var global = _settings.Load();
        var options = _instanceSettings.Load(instance.Path, global);
        InstanceContentService.ThrowIfBusy(ResolveGameDirectory(instance, gameRoot, options, global));
        var loginType = _accounts.Current?.Type ?? LoginType.Legacy;
        return await _java.CheckInstalledAsync(
            instance, profiles.Select(profile => profile.Data).ToArray(), options, loginType, cancellationToken);
    }

    public async Task<LaunchResult> StartAsync(
        InstanceItem instance,
        IProgress<LaunchProgress> progress,
        Guid sessionId,
        CancellationToken cancellationToken,
        CatalogServer? server = null)
    {
        _authorization.RequireAuthorization();
        if (instance.HasError) throw new InvalidOperationException(instance.Error ?? "实例文件异常。");
        if (!Directory.Exists(instance.Path)) throw new DirectoryNotFoundException("实例文件夹不存在。");
        if (File.Exists(InstanceModificationService.JournalPath(instance.Path)))
            throw new IOException("此实例有未完成的修改，请进入实例设置的修改页面恢复备份。");

        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(instance.Path))!)!;
        var profiles = await ReadProfileChainAsync(instance.Path, gameRoot, cancellationToken);
        var root = profiles[0].Data;
        var global = _settings.Load();
        var options = _instanceSettings.Load(instance.Path, global);
        var account = _accounts.Current ?? throw new InvalidOperationException("请先选择一个游戏账号。");
        var checkedDirectory = ResolveGameDirectory(instance, gameRoot, options, global);
        InstanceContentService.ThrowIfBusy(checkedDirectory);
        if (ModInstallService.HasInterruptedChange(checkedDirectory))
            throw new IOException("上次 Mod 变更未完成，请进入实例设置的 Mod 页面回退上次变更后再启动。");
        if (!account.IsUsable) throw new InvalidOperationException("当前账号不可用，请重新登录后再启动。");
        if (account.Type == LoginType.Ms && !account.HasValidToken)
        {
            progress.Report(new("刷新微软账号", null));
            if (!await _accounts.TryRefreshMicrosoftAsync(account, cancellationToken))
                throw new InvalidOperationException("微软账号登录已过期，请重新登录。");
        }

        var java = await _java.SelectAsync(instance, profiles.Select(profile => profile.Data).ToArray(),
            options, account.Type, progress, cancellationToken);
        var javaPath = ChooseJavaExecutable(java.Path, options.UseJavaExe);

        progress.Report(new("检查游戏文件", null));
        await PrepareGameFilesAsync(instance, gameRoot, profiles, global, progress, cancellationToken);
        var nativeDirectory = ExtractNatives(profiles, gameRoot, cancellationToken);
        var gameDirectory = ResolveGameDirectory(instance, gameRoot, options, global);
        var classpath = BuildClasspath(instance, profiles, gameRoot);
        var mainClass = FindValue(profiles, "mainClass")
            ?? throw new InvalidDataException("版本 JSON 中没有 mainClass。");

        if (!string.IsNullOrWhiteSpace(options.PreLaunchCommand))
        {
            progress.Report(new("执行启动前命令", null));
            await RunPreLaunchCommandAsync(options.PreLaunchCommand, gameDirectory, cancellationToken);
        }

        progress.Report(new("检查可用内存", null));
        var memory = MemoryAllocationService.Calculate(instance, options, global);
        if (memory.IsLow)
        {
            _log.LogWarning("启动前内存紧张：{Instance}，可用物理内存 {Available:0.0} GB，可用提交空间 {Commit:0.0} GB，计划堆 {Heap:0.0} GB",
                instance.Name, memory.AvailableGb, memory.AvailableCommitGb, memory.HeapGb);
            await DownloadInteraction.ConfirmAsync("可用内存不足",
                $"当前系统可用物理内存 {memory.AvailableGb:0.0} GB，计划为游戏分配最大堆 {memory.HeapGb:0.0} GB。"
                    + "\nJava 堆外内存和图形资源还需要额外空间，启动可能失败。建议先关闭其他程序或正在运行的游戏。"
                    + (memory.HeapGb < memory.MinimumGb ? $"\n当前分配低于此实例估算的最低需求 {memory.MinimumGb:0.0} GB。" : string.Empty),
                "继续启动", cancellationToken);
            memory = MemoryAllocationService.Calculate(instance, options, global);
        }
        _log.LogInformation("内存分配：{Instance}，自动={Auto}，物理总量 {Total:0.0} GB，可用 {Available:0.0} GB，可用提交空间 {Commit:0.0} GB，Mod 文件 {Mods}，-Xms{Initial}M -Xmx{Maximum}M",
            instance.Name, options.IsAutoMemory, memory.TotalGb, memory.AvailableGb, memory.AvailableCommitGb,
            memory.ModCount, Math.Min(512, memory.HeapMb), memory.HeapMb);
        progress.Report(new("准备启动参数", null));
        var start = BuildStartInfo(javaPath, instance, profiles, gameRoot, gameDirectory, nativeDirectory,
            string.Join(Path.PathSeparator, classpath), mainClass, account, options, global, memory, server);
        _sessions.PrepareLogs(sessionId, start, gameDirectory,
            [account.AccessToken, account.RefreshToken, account.ClientToken, account.Password], memory);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.RequireHighPerformanceGpu) ApplyHighPerformanceGpuPreference(javaPath);
        progress.Report(new("应用游戏设置", null));
        await DefaultGameSettingsService.ApplyAsync(instance, gameDirectory, global.GameDefaults ?? new(), cancellationToken);
        ApplyLanguagePreference(gameDirectory, instance.MinecraftVersionId, global.AutoSetChinese);
        if (server is not null)
        {
            foreach (var other in _sessions.ActiveSessions.Where(s => s.Id != sessionId))
            {
                var otherDirectory = other.GameDirectory;
                if (otherDirectory is null)
                {
                    var otherRoot = Path.GetDirectoryName(Path.GetDirectoryName(other.InstancePath)!)!;
                    if (GameFolderService.PathsEqual(otherRoot, gameRoot))
                        throw new IOException($"实例「{other.InstanceName}」正在准备启动，请稍后再加入服务器。");
                    continue;
                }
                if (GameFolderService.PathsEqual(otherDirectory, gameDirectory))
                    throw new IOException($"请先关闭使用相同游戏目录的实例「{other.InstanceName}」，再加入服务器。");
            }
            InstanceContentService.ThrowIfBusy(gameDirectory);
            progress.Report(new("添加多人游戏服务器", null));
            await MinecraftServerList.AddAsync(gameDirectory, server, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new("启动 Minecraft", null));
        _authorization.RequireAuthorization();
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Java 进程。");
        ApplyPriority(process, options.ProcessPriority);
        var ready = _sessions.Attach(sessionId, process, gameDirectory);
        var title = string.IsNullOrWhiteSpace(options.WindowTitle) ? global.WindowTitle : options.WindowTitle;
        WatchWindowTitle(process.Id, title, instance, gameRoot, gameDirectory, java.Path);
        _log.LogInformation("已启动 Minecraft：{Instance}，PID {ProcessId}，Java {JavaVersion}",
            instance.Name, process.Id, java.Version);
        try
        {
            await ready.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _sessions.Stop(sessionId);
            throw;
        }
        InstanceUsageStore.RecordLaunch(instance.Path);
        return new LaunchResult(process.Id, java.Version);
    }

    private static async Task<List<VersionProfile>> ReadProfileChainAsync(
        string instancePath,
        string gameRoot,
        CancellationToken cancellationToken)
    {
        var versionsRoot = Path.Combine(gameRoot, "versions");
        var currentPath = Path.GetFullPath(instancePath);
        var currentId = Path.GetFileName(currentPath);
        var profiles = new List<VersionProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth < 16; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(currentId)) throw new InvalidDataException("版本 JSON 的前置关系形成循环。");
            var data = await Task.Run(() => InstanceProfileReader.Read(currentPath), cancellationToken);
            profiles.Add(new VersionProfile(currentId, currentPath, data));

            var parent = ReadString(data["inheritsFrom"]);
            if (parent.Length == 0) return profiles;
            if (Path.GetFileName(parent) != parent || parent is "." or "..")
                throw new InvalidDataException("版本 JSON 中的前置版本名称无效。");
            currentId = parent;
            currentPath = Path.Combine(versionsRoot, currentId);
        }

        throw new InvalidDataException("版本前置层级超过 16 层。");
    }

    private async Task<bool> PrepareGameFilesAsync(
        InstanceItem instance,
        string gameRoot,
        IReadOnlyList<VersionProfile> profiles,
        LauncherSettings settings,
        IProgress<LaunchProgress> progress,
        CancellationToken cancellationToken)
    {
        var coreFiles = new List<DownloadFileRequest>();
        var root = profiles[0].Data;
        var instanceId = profiles[0].Id;
        var client = FindObject(profiles, "downloads")?["client"] as JsonObject;
        if (client is null)
            throw new InvalidDataException("版本元数据没有客户端文件信息；请重新安装此实例。");
        var clientPath = Path.Combine(instance.Path, instanceId + ".jar");
        coreFiles.Add(ToRequest(client, clientPath, "Minecraft 客户端",
            [new Uri($"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(instance.MinecraftVersionId)}/client")]));

        var libraries = new List<LibraryFile>();
        var librarySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile.Data["libraries"] is not JsonArray entries) continue;
            foreach (var library in entries.OfType<JsonObject>())
            {
                if (!RulesAllow(library["rules"] as JsonArray)) continue;
                var artifact = library["downloads"]?["artifact"] as JsonObject;
                var coordinate = ReadString(library["name"]);
                if (artifact is null && !string.IsNullOrWhiteSpace(coordinate))
                    artifact = MakeMavenArtifact(library, coordinate);
                if (artifact is not null)
                {
                    var artifactPath = ResolveLibraryPath(gameRoot, artifact);
                    if (artifactPath is not null && librarySeen.Add(artifactPath))
                        libraries.Add(new LibraryFile(artifactPath,
                            ToRequest(artifact, artifactPath, "依赖库", metadataFallback: library, includeLibraryMirrors: true), false));
                }

                var natives = FindNativeClassifier(library);
                if (natives is not null)
                {
                    var nativePath = ResolveLibraryPath(gameRoot, natives);
                    if (nativePath is not null && librarySeen.Add(nativePath))
                        libraries.Add(new LibraryFile(nativePath,
                            ToRequest(natives, nativePath, "原生库", includeLibraryMirrors: true), true));
                }
            }
        }
        coreFiles.AddRange(libraries.Select(library => library.Request));

        var assetIndex = FindObject(profiles, "assetIndex") as JsonObject;
        var assetIndexId = ReadString(assetIndex?["id"]);
        if (assetIndexId.Length == 0) assetIndexId = ReadString(FindValue(profiles, "assets"));
        var assetIndexPath = Path.Combine(gameRoot, "assets", "indexes", assetIndexId + ".json");
        if (assetIndex is not null)
        {
            var assetIndexUrl = ReadString(assetIndex["url"]);
            if (Uri.TryCreate(assetIndexUrl, UriKind.Absolute, out _))
            {
                coreFiles.Add(ToRequest(assetIndex, assetIndexPath, "资源索引",
                    [new Uri("https://bmclapi2.bangbang93.com/" + new Uri(assetIndexUrl).AbsolutePath.TrimStart('/'))]));
            }
        }

        var logging = FindObject(profiles, "logging")?["client"] as JsonObject;
        var logFile = logging?["file"] as JsonObject;
        var logPath = Path.Combine(gameRoot, "assets", "log_configs", ReadString(logFile?["id"]));
        if (logFile is not null && Uri.TryCreate(ReadString(logFile["url"]), UriKind.Absolute, out _))
            coreFiles.Add(ToRequest(logFile, logPath, "日志配置"));

        await DownloadMissingAsync(coreFiles, gameRoot, settings, progress, "准备游戏文件", cancellationToken);
        if (assetIndex is null || !File.Exists(assetIndexPath)) return assetIndex is not null;

        var indexJson = JsonNode.Parse(await File.ReadAllTextAsync(assetIndexPath, cancellationToken)) as JsonObject
            ?? throw new InvalidDataException("资源索引解析失败。");
        var assets = BuildAssetRequests(indexJson, gameRoot);
        await DownloadMissingAsync(assets, gameRoot, settings, progress, "下载游戏资源", cancellationToken);
        BuildLegacyAssets(indexJson, gameRoot, cancellationToken);
        return true;
    }

    private async Task DownloadMissingAsync(
        IReadOnlyList<DownloadFileRequest> requests,
        string gameRoot,
        LauncherSettings settings,
        IProgress<LaunchProgress> progress,
        string stage,
        CancellationToken cancellationToken)
    {
        var missing = new List<DownloadFileRequest>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsValidAsync(request, cancellationToken)) missing.Add(request);
        }
        if (missing.Count == 0)
        {
            progress.Report(new(stage, 1));
            return;
        }

        progress.Report(new(stage, 0, 0, missing.Count));
        var reuseRoots = _folders.Discover().Select(folder => folder.Path)
            .Append(gameRoot)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var options = new DownloadFileOptions(settings.DownloadThreads, settings.DownloadSpeedLimitKb,
            settings.FileDownloadSource, gameRoot, reuseRoots);
        var reporter = new Progress<DownloadBatchProgress>(batch =>
            progress.Report(new(stage, batch.Progress, batch.BytesPerSecond, batch.RemainingFiles)));
        await _downloads.DownloadFilesAsync(missing, options, reporter, cancellationToken);
    }

    private string ExtractNatives(
        IReadOnlyList<VersionProfile> profiles,
        string gameRoot,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(AppInfo.DataDirectory, "temp", "natives", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var profile in profiles)
        {
            if (profile.Data["libraries"] is not JsonArray entries) continue;
            foreach (var library in entries.OfType<JsonObject>())
            {
                if (!RulesAllow(library["rules"] as JsonArray)) continue;
                var native = FindNativeClassifier(library);
                var path = native is null ? null : ResolveLibraryPath(gameRoot, native);
                if (path is null || !File.Exists(path)) continue;
                using var archive = ZipFile.OpenRead(path);
                var exclusions = (library["extract"]?["exclude"] as JsonArray)?.Select(ReadString)
                    .Where(value => value.Length > 0).ToArray() ?? ["META-INF/"];
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/') || exclusions.Any(exclusion => entry.FullName.StartsWith(exclusion, StringComparison.OrdinalIgnoreCase))) continue;
                    var target = Path.GetFullPath(Path.Combine(folder, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
        }
        return folder;
    }

    private static ProcessStartInfo BuildStartInfo(
        string javaPath,
        InstanceItem instance,
        IReadOnlyList<VersionProfile> profiles,
        string gameRoot,
        string gameDirectory,
        string nativeDirectory,
        string classpath,
        string mainClass,
        Account account,
        InstanceLaunchSettings options,
        LauncherSettings global,
        MemoryAllocationPlan memory,
        CatalogServer? server)
    {
        var root = profiles[0].Data;
        var assetIndex = FindObject(profiles, "assetIndex") as JsonObject;
        var assetId = ReadString(assetIndex?["id"]);
        if (assetId.Length == 0) assetId = ReadString(FindValue(profiles, "assets"));
        var indexPath = Path.Combine(gameRoot, "assets", "indexes", assetId + ".json");
        var legacy = false;
        if (File.Exists(indexPath))
        {
            try { legacy = ReadBool(JsonNode.Parse(File.ReadAllText(indexPath))?["virtual"]); }
            catch { }
        }

        var size = ParseWindowSize(options, global);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = ReadString(root["id"]).Length == 0 ? instance.Name : ReadString(root["id"]),
            ["game_directory"] = gameDirectory,
            ["assets_root"] = Path.Combine(gameRoot, "assets", legacy ? Path.Combine("virtual", "legacy") : ""),
            ["assets_index_name"] = assetId,
            ["auth_uuid"] = OfflineUuid.ToDashed(account.Uuid),
            ["auth_access_token"] = string.IsNullOrWhiteSpace(account.AccessToken) ? "0" : account.AccessToken,
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["user_type"] = account.Type switch { LoginType.Ms => "msa", LoginType.Legacy => "legacy", _ => "mojang" },
            ["user_properties"] = "{}",
            ["version_type"] = FindValue(profiles, "type") ?? "release",
            ["natives_directory"] = nativeDirectory,
            ["launcher_name"] = AppInfo.Name,
            ["launcher_version"] = AppInfo.Version,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine(gameRoot, "libraries"),
            ["resolution_width"] = size.Width.ToString(),
            ["resolution_height"] = size.Height.ToString(),
        };
        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = size.IsCustom,
            ["has_quick_plays_support"] = false,
        };

        var start = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in Tokenize(options.JvmArgsHead)) start.ArgumentList.Add(Expand(arg, variables));
        if (options.IpPreference == "IPv4 优先") start.ArgumentList.Add("-Djava.net.preferIPv4Stack=true");
        else if (options.IpPreference == "IPv6 优先") start.ArgumentList.Add("-Djava.net.preferIPv6Addresses=true");

        var logging = FindObject(profiles, "logging")?["client"] as JsonObject;
        var logFile = logging?["file"] as JsonObject;
        if (logFile is not null)
        {
            var configPath = Path.Combine(gameRoot, "assets", "log_configs", ReadString(logFile["id"]));
            var argument = ReadString(logging?["argument"]);
            if (argument.Length > 0 && File.Exists(configPath)) start.ArgumentList.Add(Expand(argument.Replace("${path}", configPath), variables));
        }

        var modernArgs = FindObject(profiles, "arguments");
        if (modernArgs?["jvm"] is JsonArray jvm)
        {
            foreach (var arg in ResolveArguments(jvm, variables, features)) start.ArgumentList.Add(arg);
        }
        else
        {
            start.ArgumentList.Add("-Djava.library.path=" + nativeDirectory);
            start.ArgumentList.Add("-cp");
            start.ArgumentList.Add(classpath);
        }
        if (!ContainsClassPathArguments(start.ArgumentList))
        {
            start.ArgumentList.Add("-cp");
            start.ArgumentList.Add(classpath);
        }
        // The memory controls own these arguments, including when a profile supplies duplicates.
        for (var i = start.ArgumentList.Count - 1; i >= 0; i--)
            if (start.ArgumentList[i].StartsWith("-Xmx", StringComparison.Ordinal)
                || start.ArgumentList[i].StartsWith("-Xms", StringComparison.Ordinal)) start.ArgumentList.RemoveAt(i);
        start.ArgumentList.Add($"-Xms{Math.Min(512, memory.HeapMb)}M");
        start.ArgumentList.Add($"-Xmx{memory.HeapMb}M");
        start.ArgumentList.Add(mainClass);

        if (modernArgs?["game"] is JsonArray gameArgs)
        {
            foreach (var arg in ResolveArguments(gameArgs, variables, features)) start.ArgumentList.Add(arg);
        }
        else
        {
            var legacyArgs = profiles.Select(profile => ReadString(profile.Data["minecraftArguments"]))
                .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
            foreach (var arg in Tokenize(legacyArgs)) start.ArgumentList.Add(Expand(arg, variables));
        }
        if (size.IsCustom)
        {
            start.ArgumentList.Add("--width"); start.ArgumentList.Add(size.Width.ToString());
            start.ArgumentList.Add("--height"); start.ArgumentList.Add(size.Height.ToString());
        }
        foreach (var arg in Tokenize(options.GameArgsTail)) start.ArgumentList.Add(Expand(arg, variables));
        if (server is not null)
        {
            ServerCatalogService.ValidateAddress(server);
            var quickPlay = profiles.Any(p => p.Data["arguments"]?["game"]?.ToJsonString().Contains("quickPlayMultiplayer", StringComparison.Ordinal) == true);
            var legacyJoin = Version.TryParse(instance.MinecraftVersionId, out var version)
                && version >= new Version(1, 6) && version < new Version(1, 20);
            if (quickPlay || legacyJoin)
            {
                var joinArgs = new[] { "--server", "--port", "--quickPlayMultiplayer", "--quickPlaySingleplayer", "--quickPlayRealms", "--quickPlayPath" };
                for (var i = 0; i < start.ArgumentList.Count; i++)
                {
                    if (!joinArgs.Contains(start.ArgumentList[i])) continue;
                    start.ArgumentList.RemoveAt(i);
                    if (i < start.ArgumentList.Count && !start.ArgumentList[i].StartsWith("--", StringComparison.Ordinal)) start.ArgumentList.RemoveAt(i);
                    i--;
                }
                if (quickPlay)
                {
                    start.ArgumentList.Add("--quickPlayMultiplayer"); start.ArgumentList.Add(MinecraftServerList.Address(server));
                }
                else
                {
                    start.ArgumentList.Add("--server"); start.ArgumentList.Add(server.Host);
                    start.ArgumentList.Add("--port"); start.ArgumentList.Add(server.Port.ToString());
                }
            }
            else LogBufferForServerFallback(server.Name);
        }
        return start;
    }

    private static void LogBufferForServerFallback(string name) => Serilog.Log.Information(
        "当前版本未声明直连支持，启动后请在多人游戏列表选择服务器：{Server}", name);

    private static List<string> BuildClasspath(InstanceItem instance, IReadOnlyList<VersionProfile> profiles, string gameRoot)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile.Data["libraries"] is not JsonArray entries) continue;
            foreach (var library in entries.OfType<JsonObject>())
            {
                if (!RulesAllow(library["rules"] as JsonArray)) continue;
                var artifact = library["downloads"]?["artifact"] as JsonObject;
                if (artifact is null && ReadString(library["name"]) is { Length: > 0 } coordinate)
                    artifact = MakeMavenArtifact(library, coordinate);
                if (artifact is null) continue;
                var path = ResolveLibraryPath(gameRoot, artifact);
                if (path is not null && File.Exists(path) && seen.Add(path)) result.Add(path);
            }
        }
        var id = profiles[0].Id;
        var client = Path.Combine(instance.Path, id + ".jar");
        if (File.Exists(client)) result.Add(client);
        return result;
    }

    private static IReadOnlyList<DownloadFileRequest> BuildAssetRequests(JsonObject index, string gameRoot)
    {
        var requests = new List<DownloadFileRequest>();
        if (index["objects"] is not JsonObject objects) return requests;
        foreach (var (name, value) in objects)
        {
            var hash = ReadString(value?["hash"]);
            if (hash.Length < 2) continue;
            var url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
            var mirror = $"https://bmclapi2.bangbang93.com/assets/{hash[..2]}/{hash}";
            requests.Add(new DownloadFileRequest(
                Path.Combine(gameRoot, "assets", "objects", hash[..2], hash), "游戏资源", hash,
                ReadLong(value?["size"]), [new Uri(url), new Uri(mirror)]));
        }
        return requests;
    }

    private static void BuildLegacyAssets(JsonObject index, string gameRoot, CancellationToken cancellationToken)
    {
        var virtualAssets = ReadBool(index["virtual"]);
        var mapResources = ReadBool(index["map_to_resources"]);
        if ((!virtualAssets && !mapResources) || index["objects"] is not JsonObject objects) return;
        var root = Path.Combine(gameRoot, virtualAssets ? Path.Combine("assets", "virtual", "legacy") : "resources");
        foreach (var (name, value) in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = ReadString(value?["hash"]);
            if (hash.Length < 2) continue;
            var source = Path.Combine(gameRoot, "assets", "objects", hash[..2], hash);
            var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(source, target);
        }
    }

    private static DownloadFileRequest ToRequest(
        JsonObject data,
        string path,
        string kind,
        IEnumerable<Uri>? extraSources = null,
        JsonObject? metadataFallback = null,
        bool includeLibraryMirrors = false)
    {
        var url = ReadString(data["url"]);
        var sources = new List<Uri>();
        if (Uri.TryCreate(url, UriKind.Absolute, out var official)) sources.Add(official);
        var mirrorPath = ReadString(data["path"]);
        if (sources.Count > 0)
        {
            var source = sources[0];
            if (source.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = GetLibraryRelativePath(mirrorPath, source);
                AddSource(sources, $"https://bmclapi2.bangbang93.com/maven/{suffix}");
            }
            else if (source.Host.Contains("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
                || source.Host.Contains("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                AddSource(sources, source.ToString()
                    .Replace(source.Host, "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase));
            }
            else if (source.Host.Equals("maven.fabricmc.net", StringComparison.OrdinalIgnoreCase)
                || source.Host.Equals("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
                || source.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = source.AbsolutePath.TrimStart('/');
                if (source.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
                    && suffix.StartsWith("releases/", StringComparison.OrdinalIgnoreCase))
                    suffix = suffix["releases/".Length..];
                AddSource(sources, $"https://bmclapi2.bangbang93.com/maven/{suffix}");
            }
        }
        if (includeLibraryMirrors && sources.Count <= 2)
        {
            var relative = GetLibraryRelativePath(mirrorPath,
                sources.FirstOrDefault() ?? new Uri("https://libraries.minecraft.net/"));
            AddSource(sources, $"https://libraries.minecraft.net/{relative}");
            AddSource(sources, $"https://bmclapi2.bangbang93.com/maven/{relative}");
            AddSource(sources, $"https://bmclapi2.bangbang93.com/libraries/{relative}");
        }
        if (extraSources is not null)
            foreach (var source in extraSources) AddSource(sources, source.ToString());
        if (sources.Count == 0) throw new InvalidDataException($"文件下载地址无效：{Path.GetFileName(path)}");
        var sha1 = ReadString(data["sha1"]);
        if (sha1.Length == 0) sha1 = ReadString(metadataFallback?["sha1"]);
        var md5 = ReadString(data["md5"]);
        if (md5.Length == 0) md5 = ReadString(metadataFallback?["md5"]);
        var size = ReadLong(data["size"]) ?? ReadLong(metadataFallback?["size"]);
        if (size is <= 0) size = null;
        return new DownloadFileRequest(path, kind, EmptyToNull(sha1), size, sources, EmptyToNull(md5));
    }

    private static string GetLibraryRelativePath(string path, Uri source)
    {
        var relative = path.Length > 0
            ? path
            : Uri.UnescapeDataString(source.AbsolutePath.TrimStart('/'));
        return relative.TrimStart('/').Replace('\\', '/');
    }

    private static void AddSource(List<Uri> sources, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !sources.Contains(uri))
            sources.Add(uri);
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static JsonObject? MakeMavenArtifact(JsonObject library, string coordinate)
    {
        var extension = "jar";
        var at = coordinate.LastIndexOf('@');
        if (at >= 0)
        {
            extension = coordinate[(at + 1)..];
            coordinate = coordinate[..at];
        }
        var parts = coordinate.Split(':');
        if (parts.Length < 3) return null;
        var groupPath = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : string.Empty;
        var relative = $"{groupPath}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
        var repository = ReadString(library["url"]);
        if (repository.Length == 0) repository = "https://libraries.minecraft.net/";
        if (!repository.EndsWith('/')) repository += "/";
        return new JsonObject { ["path"] = relative, ["url"] = repository + relative };
    }

    private static JsonObject? FindNativeClassifier(JsonObject library)
    {
        if (library["natives"] is not JsonObject natives || library["downloads"]?["classifiers"] is not JsonObject classifiers) return null;
        var osName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var classifier = ReadString(natives[osName]);
        if (classifier.Length == 0) return null;
        classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
        return classifiers[classifier] as JsonObject;
    }

    private static string? ResolveLibraryPath(string gameRoot, JsonObject file)
    {
        var relative = ReadString(file["path"]);
        if (relative.Length > 0)
        {
            var path = Path.GetFullPath(Path.Combine(gameRoot, "libraries", relative.Replace('/', Path.DirectorySeparatorChar)));
            return path.StartsWith(Path.GetFullPath(Path.Combine(gameRoot, "libraries")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        var url = ReadString(file["url"]);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return Path.Combine(gameRoot, "libraries", Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
            .Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool RulesAllow(JsonArray? rules)
    {
        if (rules is null) return true;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var allowed = false;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var ruleOs = ReadString(rule["os"]?["name"]);
            if (ruleOs.Length > 0 && ruleOs != os) continue;
            if (rule["features"] is JsonObject features && features.Any(pair => ReadBool(pair.Value))) continue;
            allowed = ReadString(rule["action"]) == "allow";
        }
        return allowed;
    }

    private static IEnumerable<string> ResolveArguments(
        JsonArray args,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, bool> features)
    {
        foreach (var item in args)
        {
            if (item is JsonValue text && text.TryGetValue<string>(out var simple))
            {
                yield return Expand(simple, variables);
                continue;
            }
            if (item is not JsonObject conditional || !RulesAllowWithFeatures(conditional["rules"] as JsonArray, features)) continue;
            var value = conditional["value"];
            if (value is JsonArray values)
            {
                foreach (var entry in values) if (entry is JsonValue arg && arg.TryGetValue<string>(out var textValue)) yield return Expand(textValue, variables);
            }
            else if (value is JsonValue arg && arg.TryGetValue<string>(out var textValue)) yield return Expand(textValue, variables);
        }
    }

    private static bool RulesAllowWithFeatures(JsonArray? rules, IReadOnlyDictionary<string, bool> features)
    {
        if (rules is null) return true;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var allowed = false;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var ruleOs = ReadString(rule["os"]?["name"]);
            if (ruleOs.Length > 0 && ruleOs != os) continue;
            if (rule["features"] is JsonObject required
                && required.Any(pair => !features.TryGetValue(pair.Key, out var feature) || feature != ReadBool(pair.Value))) continue;
            allowed = ReadString(rule["action"]) == "allow";
        }
        return allowed;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables)
        => Regex.Replace(value, @"\$\{([^}]+)\}", match => variables.TryGetValue(match.Groups[1].Value, out var replacement)
            ? replacement : match.Value);

    private static IEnumerable<string> Tokenize(string value)
    {
        var result = new List<string>();
        var token = new System.Text.StringBuilder();
        var quote = '\0';
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped) { token.Append(character); escaped = false; continue; }
            if (character == '\\' && quote != '\'') { escaped = true; continue; }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0'; else token.Append(character);
            }
            else if (character is '\'' or '"') quote = character;
            else if (char.IsWhiteSpace(character))
            {
                if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
            }
            else token.Append(character);
        }
        if (escaped) token.Append('\\');
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    private static bool ContainsClassPathArguments(IEnumerable<string> args)
    {
        var values = args.ToArray();
        return values.Any(value => value is "-cp" or "-classpath" or "--class-path");
    }

    private static async Task<bool> IsValidAsync(DownloadFileRequest file, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(file.Path)) return false;
            var info = new FileInfo(file.Path);
            if (file.Size is > 0 && info.Length != file.Size.Value) return false;
            if (!string.IsNullOrWhiteSpace(file.Sha1))
            {
                await using var stream = File.OpenRead(file.Path);
                var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
                return hash.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrWhiteSpace(file.Md5))
            {
                await using var stream = File.OpenRead(file.Path);
                var hash = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken));
                return hash.Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static bool ShouldIsolate(InstanceItem instance, InstanceLaunchSettings settings, LauncherSettings global)
    {
        if (settings.VersionIsolation == "关闭") return false;
        if (Directory.Exists(Path.Combine(instance.Path, "mods")) || Directory.Exists(Path.Combine(instance.Path, "saves"))) return true;
        var mode = settings.VersionIsolation;
        return mode switch
        {
            "关闭" => false,
            "隔离可安装 Mod 的版本" => instance.IsModdable,
            "隔离非正式版" => !instance.IsRelease,
            "隔离可安装 Mod 的版本与非正式版" => instance.IsModdable || !instance.IsRelease,
            _ => true,
        };
    }

    private void ApplyLanguagePreference(string gameDirectory, string gameVersion, bool useChinese)
    {
        try
        {
            var optionsPath = DefaultGameSettingsService.OptionsSource(gameDirectory);
            var isTemplate = !GameFolderService.PathsEqual(optionsPath, Path.Combine(gameDirectory, "options.txt"));

            Directory.CreateDirectory(Path.GetDirectoryName(optionsPath)!);
            var lines = File.Exists(optionsPath) ? File.ReadAllLines(optionsPath).ToList() : new List<string>();
            var languageLine = lines.FindIndex(line => line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));
            // PCL ignores a YOSBR template's language and initializes it from
            // the launcher preference before Minecraft creates options.txt.
            var currentLanguage = !isTemplate && languageLine >= 0
                ? lines[languageLine][5..].Trim()
                : "none";
            var hasWorlds = Directory.Exists(Path.Combine(gameDirectory, "saves"));

            // Match PCL: preserve the selected language once saves exist. Before
            // the first world, initialize it from the launcher's Chinese option.
            var language = currentLanguage.Equals("none", StringComparison.OrdinalIgnoreCase) || !hasWorlds
                ? useChinese ? "zh_cn" : "en_us"
                : currentLanguage.ToLowerInvariant();

            // Minecraft versions through 1.10 require an uppercase locale suffix.
            if (RequiresLegacyLanguageSuffix(gameVersion))
                language = language.Length >= 2
                    ? language[..^2] + language[^2..].ToUpperInvariant()
                    : language;

            if (languageLine >= 0 && currentLanguage.Equals(language, StringComparison.Ordinal)) return;
            if (languageLine >= 0)
            {
                lines[languageLine] = $"lang:{language}";
                for (var index = lines.Count - 1; index > languageLine; index--)
                    if (lines[index].StartsWith("lang:", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(index);
            }
            else
            {
                lines.Add($"lang:{language}");
            }

            File.WriteAllLines(optionsPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _log.LogInformation("已将 Minecraft 语言设置为 {Language}：{Path}", language, optionsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogWarning(ex, "更新 Minecraft 语言设置失败，继续启动：{GameDirectory}", gameDirectory);
        }
    }

    private static bool RequiresLegacyLanguageSuffix(string gameVersion)
    {
        var match = Regex.Match(gameVersion, @"^(?<major>\d+)\.(?<minor>\d+)(?:\.|-|$)");
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor))
            return true;

        return major == 1 && minor <= 10;
    }

    internal static string ResolveGameDirectory(InstanceItem instance, string gameRoot, InstanceLaunchSettings options, LauncherSettings global)
        => ShouldIsolate(instance, options, global) ? instance.Path : gameRoot;

    private static string ChooseJavaExecutable(string path, bool useJavaExe)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var targetName = useJavaExe ? "java.exe" : "javaw.exe";
        var candidate = Path.Combine(directory, targetName);
        if (File.Exists(candidate)) return candidate;
        var fallback = Path.Combine(directory, useJavaExe ? "javaw.exe" : "java.exe");
        if (File.Exists(fallback)) return fallback;
        return path;
    }

    private static (int Width, int Height, bool IsCustom) ParseWindowSize(InstanceLaunchSettings options, LauncherSettings global)
    {
        var value = options.WindowSize;
        if (value == "默认") value = global.WindowSize;
        var match = Regex.Match(value, @"(\d+)\s*[×x]\s*(\d+)");
        if (match.Success) return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), true);
        var width = int.TryParse(options.WindowWidth, out var customWidth) ? customWidth : int.Parse(global.WindowWidth);
        var height = int.TryParse(options.WindowHeight, out var customHeight) ? customHeight : int.Parse(global.WindowHeight);
        return value == "自定义" && width > 0 && height > 0 ? (width, height, true) : (854, 480, false);
    }

    private static async Task RunPreLaunchCommandAsync(string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法执行启动前命令。");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"启动前命令退出码为 {process.ExitCode}。");
    }

    private static void ApplyPriority(Process process, string value)
    {
        try
        {
            process.PriorityClass = value switch
            {
                "高（优先保证游戏运行）" => ProcessPriorityClass.High,
                "低（优先保证其他程序运行）" => ProcessPriorityClass.BelowNormal,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch (Exception) { }
    }

    private void ApplyHighPerformanceGpuPreference(string javaPath)
    {
        const string registryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string preference = "GpuPreference=2;";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            if (key is null) return;
            foreach (var executable in new[] { javaPath, Environment.ProcessPath })
            {
                if (string.IsNullOrWhiteSpace(executable)) continue;
                var fullPath = Path.GetFullPath(executable);
                if (string.Equals(key.GetValue(fullPath) as string, preference, StringComparison.Ordinal)) continue;
                key.SetValue(fullPath, preference, RegistryValueKind.String);
                _log.LogInformation("已为 {Executable} 设置高性能显卡偏好", fullPath);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "设置高性能显卡偏好失败");
        }
    }

    private static void WatchWindowTitle(
        int processId,
        string template,
        InstanceItem instance,
        string gameRoot,
        string gameDirectory,
        string javaPath)
    {
        if (string.IsNullOrWhiteSpace(template)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                while (!process.HasExited)
                {
                    process.Refresh();
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero)
                    {
                        var title = template
                            .Replace("{date}", DateTime.Now.ToString("yyyy/M/d"), StringComparison.OrdinalIgnoreCase)
                            .Replace("{time}", DateTime.Now.ToString("HH:mm:ss"), StringComparison.OrdinalIgnoreCase)
                            .Replace("{name}", instance.Name, StringComparison.OrdinalIgnoreCase)
                            .Replace("{version}", instance.MinecraftVersionId, StringComparison.OrdinalIgnoreCase)
                            .Replace("{version_path}", instance.Path, StringComparison.OrdinalIgnoreCase)
                            .Replace("{verpath}", instance.Path, StringComparison.OrdinalIgnoreCase)
                            .Replace("{version_indie}", gameDirectory, StringComparison.OrdinalIgnoreCase)
                            .Replace("{verindie}", gameDirectory, StringComparison.OrdinalIgnoreCase)
                            .Replace("{java}", Path.GetDirectoryName(javaPath) ?? javaPath, StringComparison.OrdinalIgnoreCase)
                            .Replace("{minecraft}", gameRoot, StringComparison.OrdinalIgnoreCase);
                        SetWindowText(window, title);
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(IntPtr window, string title);

    private static JsonObject? FindObject(IReadOnlyList<VersionProfile> profiles, string property)
        => profiles.Select(profile => profile.Data[property]).OfType<JsonObject>().FirstOrDefault();

    private static string? FindValue(IReadOnlyList<VersionProfile> profiles, string property)
        => profiles.Select(profile => ReadString(profile.Data[property])).FirstOrDefault(value => value.Length > 0);

    private static long? ReadLong(JsonNode? node)
    {
        try { return node?.GetValue<long>(); } catch { return null; }
    }

    private static string ReadString(JsonNode? node)
    {
        try { return node?.GetValue<string>() ?? string.Empty; } catch { return string.Empty; }
    }

    private static bool ReadBool(JsonNode? node)
    {
        try { return node?.GetValue<bool>() ?? false; } catch { return false; }
    }

    private sealed record VersionProfile(string Id, string Path, JsonObject Data);
    private sealed record LibraryFile(string Path, DownloadFileRequest Request, bool IsNative);
}
