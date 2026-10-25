using System.Security.Cryptography;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.IO.Compression;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Install;
using Launcher.Core.Instances;
using Launcher.Core.Versions;

namespace Launcher.App.Services;

public sealed record InstallRequest(
    string Version,
    string Name,
    string GameDirectory,
    string? Loader = null,
    string? LoaderVersion = null,
    string? LiteLoaderVersion = null,
    string? OptiFineVersion = null,
    string? FabricApiVersion = null,
    string? OptiFabricVersion = null,
    string? OutputDirectory = null,
    bool? Isolated = null);

public sealed record LoaderVersionOption(
    string Version,
    string Detail,
    bool IsStable,
    bool IsRecommended = false,
    string? Id = null,
    string? Compatibility = null);

public sealed record InstallProgress(
    int Stage,
    double Progress,
    string Detail,
    long BytesPerSecond = 0,
    bool PreserveOtherStages = false);

/// <summary>Downloads the vanilla files needed to create a Minecraft instance.</summary>
public sealed class MinecraftInstallService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<MinecraftInstallService>();
    private static readonly HttpClient LoaderHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string[] SupportedLoaders = ["Forge", "NeoForge", "Fabric"];
    private static readonly string[] SupportedComponents = ["Forge", "NeoForge", "Fabric", "LiteLoader", "OptiFine", "Fabric API", "OptiFabric"];
    private readonly MinecraftVersionService _versions;
    private readonly GameFolderService _folders;
    private readonly SettingsStore _settings;
    private readonly InstanceLaunchSettingsStore _instanceSettings;
    private readonly PclFileDownloadManager _downloads;
    private readonly JavaRuntimeService _java;
    private readonly ConcurrentDictionary<(string Loader, string MinecraftVersion), Lazy<Task<IReadOnlyList<LoaderVersionOption>>>> _loaderVersionCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<ForgeInstallerOption>>>> _forgeOptionsCache = new(StringComparer.OrdinalIgnoreCase);

    public MinecraftInstallService(
        MinecraftVersionService versions,
        GameFolderService folders,
        SettingsStore settings,
        InstanceLaunchSettingsStore instanceSettings,
        PclFileDownloadManager downloads,
        JavaRuntimeService java)
    {
        _versions = versions;
        _folders = folders;
        _settings = settings;
        _instanceSettings = instanceSettings;
        _downloads = downloads;
        _java = java;
    }

    public async Task<IReadOnlyList<LoaderVersionOption>> GetLoaderVersionsAsync(
        string loader,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedComponents.Contains(loader, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"当前不支持 {loader} 版本列表。");

        var normalizedLoader = SupportedComponents.First(item => item.Equals(loader, StringComparison.OrdinalIgnoreCase));
        var key = (normalizedLoader, minecraftVersion);
        var lazy = _loaderVersionCache.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<LoaderVersionOption>>>(
            () => FetchLoaderVersionsAsync(normalizedLoader, minecraftVersion),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                ((ICollection<KeyValuePair<(string Loader, string MinecraftVersion), Lazy<Task<IReadOnlyList<LoaderVersionOption>>>>>)_loaderVersionCache)
                    .Remove(new(key, lazy));
            }
            throw;
        }
        finally
        {
            TrimCompletedCache(_loaderVersionCache, 32);
        }
    }

    private static void TrimCompletedCache<TKey, TValue>(ConcurrentDictionary<TKey, Lazy<Task<TValue>>> cache, int limit)
        where TKey : notnull
    {
        // Never evict in-flight requests: callers still share the same download.
        foreach (var entry in cache)
        {
            if (cache.Count <= limit) break;
            if (entry.Value.IsValueCreated && entry.Value.Value.IsCompleted)
                ((ICollection<KeyValuePair<TKey, Lazy<Task<TValue>>>>)cache).Remove(entry);
        }
    }

    /// <summary>Warms the Minecraft and common loader catalogs without blocking the UI.</summary>
    public async Task PreloadNetworkMetadataAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await _versions.GetVersionsAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var candidates = manifest
            .Where(version => version.IsRelease)
            .Take(1)
            .Concat(manifest.Where(version => version.IsSnapshot).Take(1))
            .DistinctBy(version => version.Id, StringComparer.OrdinalIgnoreCase)
            .Select(version => version.Id)
            .ToArray();

        var warmups = candidates.SelectMany(version => SupportedComponents.Select(loader => (loader, version)))
            .Select(async item =>
            {
                try
                {
                    await GetLoaderVersionsAsync(item.loader, item.version, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "启动预取 {Loader} / Minecraft {Version} 版本列表失败", item.loader, item.version);
                }
            });

        await Task.WhenAll(warmups).ConfigureAwait(false);
        Log.Information("启动网络元数据预取完成，加载了 {VersionCount} 个 Minecraft 版本对应的加载器列表", candidates.Length);
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> FetchLoaderVersionsAsync(
        string loader,
        string minecraftVersion)
    {
        if (loader.Equals("Forge", StringComparison.OrdinalIgnoreCase))
            return (await GetForgeOptionsCachedAsync(minecraftVersion).ConfigureAwait(false))
                .Select(option => option.Version).ToArray();
        if (loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase))
            return await GetNeoForgeVersionsAsync(minecraftVersion, CancellationToken.None).ConfigureAwait(false);
        if (loader.Equals("Fabric", StringComparison.OrdinalIgnoreCase))
            return await GetFabricVersionsAsync(minecraftVersion, CancellationToken.None).ConfigureAwait(false);
        if (loader.Equals("LiteLoader", StringComparison.OrdinalIgnoreCase))
            return await GetLiteLoaderVersionsAsync(minecraftVersion, CancellationToken.None).ConfigureAwait(false);
        if (loader.Equals("OptiFine", StringComparison.OrdinalIgnoreCase))
            return await GetOptiFineVersionsAsync(minecraftVersion, CancellationToken.None).ConfigureAwait(false);
        if (loader.Equals("Fabric API", StringComparison.OrdinalIgnoreCase))
            return await GetModrinthVersionsAsync("fabric-api", minecraftVersion, "fabric", CancellationToken.None).ConfigureAwait(false);
        if (loader.Equals("OptiFabric", StringComparison.OrdinalIgnoreCase))
            return await GetOptiFabricVersionsAsync(minecraftVersion, CancellationToken.None).ConfigureAwait(false);
        throw new NotSupportedException($"当前不支持 {loader} 版本列表。");
    }

    private Task<List<ForgeInstallerOption>> GetForgeOptionsCachedAsync(string minecraftVersion)
    {
        var lazy = _forgeOptionsCache.GetOrAdd(minecraftVersion,
            version => new Lazy<Task<List<ForgeInstallerOption>>>(
                () => FetchForgeOptionsAsync(version), LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted || completed.IsCanceled)
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<List<ForgeInstallerOption>>>>>)_forgeOptionsCache)
                    .Remove(new(minecraftVersion, lazy));
            }
            TrimCompletedCache(_forgeOptionsCache, 8);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public async Task InstallAsync(
        InstallRequest request,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var name = SanitizeName(request.Name);
        var gameDirectory = Path.GetFullPath(request.GameDirectory);
        var versionDirectory = request.OutputDirectory is null ? Path.Combine(gameDirectory, "versions", name) : Path.GetFullPath(request.OutputDirectory);
        InstanceContentService.ValidatePath(gameDirectory, versionDirectory);
        var versionJsonPath = Path.Combine(versionDirectory, name + ".json");
        if (File.Exists(versionJsonPath))
        {
            throw new InvalidOperationException($"实例“{name}”已存在，请更换名称。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preferences = _settings.Load();
        using var networkSlots = _downloads.CreateNetworkSlots(preferences.DownloadThreads);
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(Path.Combine(gameDirectory, "versions"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "libraries"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "assets", "indexes"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "assets", "objects"));

        progress.Report(new(0, 0.02, $"检查 Minecraft {request.Version}"));
        var (json, metadataSha1) = await _versions.GetVersionDocumentAsync(
            request.Version, cancellationToken, preferences.VersionDownloadSource);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Minecraft 版本数据格式无效。");
        var hasInstallComponents = HasInstallComponents(request);
        var isolated = request.Isolated ?? ShouldIsolate(
            preferences.VersionIsolation,
            hasInstallComponents,
            root["type"]?.GetValue<string>() ?? "release");
        var metadataPath = Path.Combine(versionDirectory, name + ".json.download");
        await File.WriteAllTextAsync(metadataPath, json, cancellationToken);
        if (!await IsValidAsync(metadataPath, metadataSha1, null, cancellationToken))
        {
            File.Delete(metadataPath);
            throw new InvalidDataException("Minecraft 版本数据校验失败。");
        }
        File.Delete(metadataPath);

        var reuseRoots = _folders.Discover()
            .Select(folder => Path.GetFullPath(folder.Path))
            .Append(gameDirectory)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var downloads = new List<InstallFile>();
        var client = root["downloads"]?["client"];
        if (client is not JsonObject clientData || !AddFile(clientData,
                Path.Combine(versionDirectory, name + ".jar"), "客户端", downloads,
                new Uri($"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(request.Version)}/client")))
        {
            throw new InvalidDataException("版本数据中没有客户端文件下载信息。");
        }

        var libraryFiles = ReadLibraries(root, gameDirectory);
        libraryFiles.AddRange(downloads);
        var assetIndex = root["assetIndex"] as JsonObject
            ?? throw new InvalidDataException("版本数据中没有资源索引。");
        var indexId = assetIndex["id"]?.GetValue<string>() ?? request.Version;
        var indexPath = Path.Combine(gameDirectory, "assets", "indexes", indexId + ".json");
        var indexUrl = assetIndex["url"]?.GetValue<string>()
            ?? throw new InvalidDataException("资源索引地址缺失。");
        var indexSha1 = assetIndex["sha1"]?.GetValue<string>();
        var indexSize = assetIndex["size"]?.GetValue<long?>();

        var assetIndexFile = new InstallFile(indexPath, "资源索引", indexSha1, indexSize,
            Sources(indexUrl, isAssetIndex: true));
        progress.Report(new(0, 1, "Minecraft 版本信息已就绪"));
        using (var branches = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var libraryTask = DownloadStageAsync(libraryFiles, 1, "下载客户端与依赖库", gameDirectory,
                reuseRoots, networkSlots, progress, branches.Token, preserveOtherStages: true);
            var assetsTask = DownloadAssetsAsync(assetIndexFile, indexPath, gameDirectory, reuseRoots,
                networkSlots, progress, branches.Token);
            try { await Task.WhenAll(libraryTask, assetsTask); }
            catch
            {
                branches.Cancel();
                try { await Task.WhenAll(libraryTask, assetsTask); } catch { }
                throw;
            }
        }

        var indexJson = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken)) as JsonObject
            ?? throw new InvalidDataException("资源索引解析失败。");

        cancellationToken.ThrowIfCancellationRequested();
        var finalStage = hasInstallComponents ? 5 : 4;
        var installedFabricApiVersion = request.FabricApiVersion;
        var installedOptiFabricVersion = request.OptiFabricVersion;
        IReadOnlyList<ManagedComponentFile> managedFiles = [];
        if (hasInstallComponents)
        {
            if (!string.IsNullOrWhiteSpace(request.Loader))
                await InstallLoaderAsync(request, root, gameDirectory, versionDirectory, reuseRoots,
                    networkSlots, progress, cancellationToken);
            var installed = await InstallAdditionalComponentsAsync(request, root, gameDirectory, versionDirectory, isolated,
                reuseRoots, networkSlots, progress, cancellationToken);
            installedFabricApiVersion = installed.FabricApiVersion;
            installedOptiFabricVersion = installed.OptiFabricVersion;
            managedFiles = installed.ManagedFiles;
        }

        progress.Report(new(finalStage, 0.2, "整理旧版资源目录"));
        await Task.Run(() => BuildLegacyAssetDirectories(indexJson, gameDirectory, cancellationToken), cancellationToken);
        progress.Report(new(finalStage, 0.75, "写入版本信息"));
        root["id"] = name;
        root["assets"] ??= indexId;
        var tempJson = versionJsonPath + ".tmp";
        await File.WriteAllTextAsync(tempJson, root.ToJsonString(), cancellationToken);
        File.Move(tempJson, versionJsonPath, overwrite: true);
        if (isolated)
        {
            Directory.CreateDirectory(Path.Combine(versionDirectory, "resourcepacks"));
            if (!string.IsNullOrWhiteSpace(request.Loader) || !string.IsNullOrWhiteSpace(request.LiteLoaderVersion)
                || !string.IsNullOrWhiteSpace(request.OptiFineVersion) || !string.IsNullOrWhiteSpace(request.FabricApiVersion)
                || !string.IsNullOrWhiteSpace(request.OptiFabricVersion))
            {
                Directory.CreateDirectory(Path.Combine(versionDirectory, "mods"));
            }
        }
        _instanceSettings.Save(versionDirectory, InstanceLaunchSettings.From(preferences));
        new InstanceInstallManifest(2, request.Version, request.Loader, request.LoaderVersion,
            request.LiteLoaderVersion, request.OptiFineVersion, installedFabricApiVersion, installedOptiFabricVersion, managedFiles)
            .Save(versionDirectory);
        if (request.OutputDirectory is null) DefaultGameSettingsService.RecordNewInstance(versionDirectory);
        progress.Report(new(finalStage, 1, "实例文件已就绪"));
    }

    private async Task InstallLoaderAsync(
        InstallRequest request,
        JsonObject minecraftProfile,
        string gameDirectory,
        string versionDirectory,
        string[] reuseRoots,
        SemaphoreSlim networkSlots,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var loader = request.Loader!.Trim();
        var stage = 4;
        JsonObject profile;
        if (loader.Equals("Fabric", StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new(stage, 0.02, "获取 Fabric 版本列表", PreserveOtherStages: true));
            var availableVersions = await GetLoaderVersionsAsync("Fabric", request.Version, cancellationToken);
            var selectedVersion = request.LoaderVersion is null
                ? availableVersions.FirstOrDefault(item => item.IsStable) ?? availableVersions.FirstOrDefault()
                : availableVersions.FirstOrDefault(item =>
                    item.Version.Equals(request.LoaderVersion, StringComparison.OrdinalIgnoreCase));
            if (selectedVersion is null)
                throw new InvalidDataException(request.LoaderVersion is null
                    ? $"Minecraft {request.Version} 没有可用的 Fabric 版本。"
                    : $"Minecraft {request.Version} 已没有 Fabric {request.LoaderVersion} 版本。");
            var loaderVersion = selectedVersion.Version;
            var profilePath = Path.Combine(versionDirectory, ".fabric-profile.json");
            var profileUrls = new[]
            {
                new Uri($"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{Uri.EscapeDataString(request.Version)}/{Uri.EscapeDataString(loaderVersion)}/profile/json"),
                new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(request.Version)}/{Uri.EscapeDataString(loaderVersion)}/profile/json"),
            };
            await DownloadStageAsync([new InstallFile(profilePath, "Fabric 配置文件", null, null, profileUrls.ToList())],
                stage, "安装 Fabric", gameDirectory, reuseRoots, networkSlots, progress, cancellationToken,
                preserveOtherStages: true, progressOffset: 0.05, progressScale: 0.15);
            profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath, cancellationToken)) as JsonObject
                ?? throw new InvalidDataException("Fabric 配置文件格式无效。");
            TryDelete(profilePath);

            var loaderLibraries = ReadLibraries(profile, gameDirectory);
            await DownloadStageAsync(loaderLibraries, stage, "安装 Fabric", gameDirectory, reuseRoots,
                networkSlots, progress, cancellationToken, preserveOtherStages: true,
                progressOffset: 0.2, progressScale: 0.72);
        }
        else if (loader.Equals("Forge", StringComparison.OrdinalIgnoreCase)
            || loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase))
        {
            var installer = loader.Equals("Forge", StringComparison.OrdinalIgnoreCase)
                ? await GetForgeInstallerAsync(request.Version, request.LoaderVersion, cancellationToken)
                : await GetNeoForgeInstallerAsync(request.Version, request.LoaderVersion, cancellationToken);
            var installerPath = Path.Combine(gameDirectory, ".97launch", "installers", installer.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            await DownloadStageAsync([new InstallFile(installerPath, $"{loader} 安装器", installer.Sha1, null,
                    installer.Sources, installer.Md5)],
                stage, $"下载 {loader} 安装文件", gameDirectory, reuseRoots, networkSlots, progress,
                cancellationToken, preserveOtherStages: true, progressOffset: 0.05, progressScale: 0.2);
            if (!File.Exists(installerPath) || new FileInfo(installerPath).Length < 64 * 1024)
                throw new InvalidDataException($"{loader} 安装器文件无效。");

            var prepared = await PrepareLoaderLibrariesAsync(request, minecraftProfile, installerPath,
                versionDirectory, reuseRoots, networkSlots, progress, stage, cancellationToken);
            try
            {
                var instance = new InstanceItem(request.Name, versionDirectory, request.Version, loader, true, string.Empty,
                    LoaderVersionDisplay: $"{loader} {request.LoaderVersion}", MinecraftVersionId: request.Version);
                var javaProgress = new ActionProgress<LaunchProgress>(update => progress.Report(
                    new(stage, 0.65 + (update.Progress ?? 0) * 0.08, update.Stage, update.BytesPerSecond, PreserveOtherStages: true)));
                var java = await _java.SelectAsync(instance, [minecraftProfile, prepared.Profile],
                    _instanceSettings.Load(versionDirectory, _settings.Load()), LoginType.Legacy, javaProgress, cancellationToken);
                progress.Report(new(stage, 0.75, $"运行 {loader} 安装器", PreserveOtherStages: true));
                var profilePath = await RunLoaderInstallerAsync(loader, installerPath, prepared.Directory,
                    java.Path, prepared.Profile["id"]?.GetValue<string>(), cancellationToken);
                profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath, cancellationToken)) as JsonObject
                    ?? throw new InvalidDataException($"{loader} 安装器生成的版本信息无效。");
                await ImportLoaderLibrariesAsync(prepared.Directory, gameDirectory, cancellationToken);
                var loaderLibraries = ReadLibraries(profile, gameDirectory);
                await DownloadStageAsync(loaderLibraries, stage, $"安装 {loader}", gameDirectory, reuseRoots,
                    networkSlots, progress, cancellationToken, preserveOtherStages: true,
                    progressOffset: 0.9, progressScale: 0.08);
            }
            catch
            {
                Log.Warning("加载器安装未完成，保留任务工作目录：{Workspace}", prepared.Directory);
                throw;
            }
            TryDeleteLoaderWorkspace(gameDirectory, prepared.Directory);
        }
        else
        {
            throw new NotSupportedException($"当前不支持安装 {loader}。");
        }

        MergeLoaderProfile(minecraftProfile, profile);
        progress.Report(new(stage, 1, $"{loader} 安装信息已整理", PreserveOtherStages: true));
    }

    private static bool HasInstallComponents(InstallRequest request)
        => request.Loader is not null || request.LiteLoaderVersion is not null || request.OptiFineVersion is not null
            || request.FabricApiVersion is not null || request.OptiFabricVersion is not null;

    private async Task<(string? FabricApiVersion, string? OptiFabricVersion, IReadOnlyList<ManagedComponentFile> ManagedFiles)> InstallAdditionalComponentsAsync(
        InstallRequest request,
        JsonObject profile,
        string gameDirectory,
        string versionDirectory,
        bool isolated,
        string[] reuseRoots,
        SemaphoreSlim networkSlots,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        const int stage = 4;
        var modsDirectory = isolated ? Path.Combine(versionDirectory, "mods") : Path.Combine(gameDirectory, "mods");
        var hasLoader = request.Loader is not null || request.LiteLoaderVersion is not null;
        var files = new List<InstallFile>();

        if (request.Loader == "NeoForge" && request.OptiFineVersion is not null)
            throw new InvalidOperationException("PCL 不支持 NeoForge 与 OptiFine 组合安装。");
        if (request.FabricApiVersion is not null && request.Loader != "Fabric")
            throw new InvalidOperationException("Fabric API 需要同时安装 Fabric。");
        if (request.OptiFabricVersion is not null && (request.Loader != "Fabric" || request.OptiFineVersion is null))
            throw new InvalidOperationException("OptiFabric 需要同时安装 Fabric 与 OptiFine。");

        if (request.LiteLoaderVersion is not null)
        {
            progress.Report(new(stage, 0.08, "获取 LiteLoader 安装信息", PreserveOtherStages: true));
            var documentText = await GetTextFromSourcesAsync(
                ["https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json",
                 "https://dl.liteloader.com/versions/versions.json"], cancellationToken);
            using var document = JsonDocument.Parse(documentText);
            if (!document.RootElement.GetProperty("versions").TryGetProperty(request.Version, out var gameVersion))
                throw new InvalidDataException($"Minecraft {request.Version} 没有可用的 LiteLoader 版本。");
            var entry = FindLiteLoaderLatest(gameVersion);
            if (GetJsonString(entry, "version") != request.LiteLoaderVersion)
                throw new InvalidDataException("LiteLoader 版本列表已更新，请重新选择版本。");

            var libraries = new JsonArray();
            if (entry.TryGetProperty("libraries", out var declaredLibraries) && declaredLibraries.ValueKind == JsonValueKind.Array)
                foreach (var item in declaredLibraries.EnumerateArray())
                    libraries.Add(NormalizeMavenLibrary(item, "https://repo.mumfrey.com/content/repositories/snapshots/"));
            libraries.Add(NormalizeMavenLibrary(JsonSerializer.SerializeToElement(new
            {
                name = $"com.mumfrey:liteloader:{request.LiteLoaderVersion}",
                md5 = GetJsonString(entry, "md5"),
            }), "https://dl.liteloader.com/versions/"));
            var liteProfile = new JsonObject
            {
                ["arguments"] = new JsonObject
                {
                    ["game"] = entry.TryGetProperty("tweakClass", out var tweak)
                        ? new JsonArray("--tweakClass", tweak.GetString())
                        : new JsonArray(),
                },
                ["libraries"] = libraries,
            };
            if (request.Loader is null) liteProfile["mainClass"] = "net.minecraft.launchwrapper.Launch";
            MergeLoaderProfile(profile, liteProfile);
        }

        if (request.OptiFineVersion is not null)
        {
            progress.Report(new(stage, 0.15, "获取 OptiFine 安装文件", PreserveOtherStages: true));
            var optiFine = await GetOptiFineEntryAsync(request.Version, request.OptiFineVersion, cancellationToken);
            if (request.Loader == "Forge")
            {
                var requirement = GetJsonStringOrNull(optiFine, "forge");
                if (PclLoaderRules.IsForgeOptiFineRangeUnsupported(request.Version)
                    || !PclLoaderRules.IsOptiFineCompatibleWithForge(
                        requirement, request.Version, request.LoaderVersion ?? string.Empty))
                    throw new InvalidOperationException("所选 OptiFine 版本与当前 Forge 不兼容。");
            }
            if (request.Loader == "Fabric" && !PclLoaderRules.SupportsOptiFineWithFabric(request.Version))
                throw new InvalidOperationException("该 Minecraft 版本不支持 Fabric 与 OptiFine 组合安装。");

            var installer = await CreateOptiFineFileAsync(optiFine, cancellationToken);
            if (hasLoader)
            {
                var fileName = GetJsonString(optiFine, "filename");
                files.Add(installer with { Path = Path.Combine(modsDirectory, fileName), Kind = "OptiFine" });
            }
            else
            {
                var installerPath = Path.Combine(gameDirectory, ".97launch", "installers", GetJsonString(optiFine, "filename"));
                await DownloadStageAsync([installer with { Path = installerPath }], stage, "下载 OptiFine 安装文件",
                    gameDirectory, reuseRoots, networkSlots, progress, cancellationToken,
                    preserveOtherStages: true, progressOffset: 0.15, progressScale: 0.2);
                await RunOptiFineInstallerAsync(installerPath, request.Version, request.Name, gameDirectory,
                    versionDirectory, profile, cancellationToken);
            }
        }

        var fabricApiVersion = request.FabricApiVersion;
        if (fabricApiVersion is not null)
            files.Add(await GetModrinthFileAsync("fabric-api", request.Version, "fabric", fabricApiVersion, modsDirectory, cancellationToken));

        var optiFabricVersion = request.OptiFabricVersion;
        if (optiFabricVersion is not null)
            files.Add(await GetOptiFabricFileAsync(request.Version, optiFabricVersion, modsDirectory, cancellationToken));

        if (files.Count > 0)
        {
            Directory.CreateDirectory(modsDirectory);
            await DownloadStageAsync(files, stage, "下载所选 Mod 组件", gameDirectory, reuseRoots,
                networkSlots, progress, cancellationToken, preserveOtherStages: true,
                progressOffset: 0.38, progressScale: 0.5);
            if (files.Any(file => file.Kind == "OptiFine")
                && files.Where(file => file.Kind == "OptiFine").Any(file => new FileInfo(file.Path).Length < 300 * 1024))
                throw new InvalidDataException("OptiFine 安装文件不完整。");
        }
        progress.Report(new(stage, 1, "所选加载器与组件已整理", PreserveOtherStages: true));
        var managed = new List<ManagedComponentFile>();
        foreach (var file in files)
        {
            await using var input = File.OpenRead(file.Path);
            managed.Add(new("mods/" + Path.GetFileName(file.Path), Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken)).ToLowerInvariant(),
                file.Kind == "OptiFine" ? "OptiFine" : Path.GetFileName(file.Path).StartsWith("fabric-api", StringComparison.OrdinalIgnoreCase) ? "Fabric API" : "OptiFabric"));
        }
        return (fabricApiVersion, optiFabricVersion, managed);
    }

    private async Task<JsonElement> GetOptiFineEntryAsync(string minecraftVersion, string fileName, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync(
            ["https://bmclapi2.bangbang93.com/optifine/versionList"], cancellationToken));
        var match = document.RootElement.EnumerateArray().FirstOrDefault(entry =>
            GetJsonString(entry, "mcversion").Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase)
            && GetJsonString(entry, "filename").Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return match.ValueKind == JsonValueKind.Object
            ? match.Clone()
            : throw new InvalidDataException("所选 OptiFine 版本已从列表中移除。");
    }

    private async Task<InstallFile> CreateOptiFineFileAsync(JsonElement entry, CancellationToken cancellationToken)
    {
        var minecraftVersion = GetJsonString(entry, "mcversion");
        var type = GetJsonString(entry, "type");
        var patch = GetJsonString(entry, "patch");
        var isPreview = patch.Contains("pre", StringComparison.OrdinalIgnoreCase);
        var typeSuffix = type.Replace("HD_U", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('_');
        var path = isPreview
            ? string.IsNullOrWhiteSpace(typeSuffix) ? $"HD_U_{patch}" : $"HD_U_{typeSuffix}/{patch}"
            : $"HD_U/{patch}";
        var sources = new List<Uri>
        {
            new($"https://bmclapi2.bangbang93.com/optifine/{minecraftVersion}/{path}"),
        };
        var fileName = GetJsonString(entry, "filename");
        try
        {
            var page = await GetTextFromSourcesAsync(
                [$"https://optifine.net/adloadx?f={Uri.EscapeDataString(fileName)}"], cancellationToken);
            var match = System.Text.RegularExpressions.Regex.Match(page, "downloadx\\?f=[^\\\"']+");
            if (match.Success) sources.Add(new Uri("https://optifine.net/" + match.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
        return new InstallFile(string.Empty, "OptiFine", null, null, sources);
    }

    private async Task<InstallFile> GetModrinthFileAsync(
        string project,
        string minecraftVersion,
        string loader,
        string? versionId,
        string modsDirectory,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(versionId)
            ? $"https://api.modrinth.com/v2/project/{project}/version?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }))}&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}&include_changelog=false"
            : $"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(versionId)}";
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync([url], cancellationToken));
        var version = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : document.RootElement;
        if (version.ValueKind != JsonValueKind.Object || !version.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{project} 没有可用于 Minecraft {minecraftVersion} 的文件。");
        var file = files.EnumerateArray().FirstOrDefault(item => item.TryGetProperty("primary", out var primary)
            && primary.ValueKind == JsonValueKind.True);
        if (file.ValueKind == JsonValueKind.Undefined) file = files.EnumerateArray().FirstOrDefault();
        if (file.ValueKind != JsonValueKind.Object || !Uri.TryCreate(GetJsonString(file, "url"), UriKind.Absolute, out var download))
            throw new InvalidDataException($"{project} 返回的文件地址无效。");
        var hashes = file.TryGetProperty("hashes", out var value) ? value : default;
        var sha1 = hashes.ValueKind == JsonValueKind.Object ? GetJsonString(hashes, "sha1") : null;
        var filename = Path.GetFileName(GetJsonString(file, "filename"));
        var size = file.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : (long?)null;
        return new InstallFile(Path.Combine(modsDirectory, filename), project, string.IsNullOrWhiteSpace(sha1) ? null : sha1,
            size, [download]);
    }

    private async Task<InstallFile> GetOptiFabricFileAsync(
        string minecraftVersion,
        string? fileId,
        string modsDirectory,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync(["https://api.cfwidget.com/322385"], cancellationToken));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("无法获取 OptiFabric 版本列表。");
        var file = files.EnumerateArray().FirstOrDefault(item => GetJsonString(item, "id") == fileId);
        if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("versions", out var versions)
            || !versions.EnumerateArray().Any(item => item.GetString()?.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidDataException("所选 OptiFabric 版本已不可用于当前 Minecraft 版本。");
        var id = GetJsonString(file, "id");
        var numericId = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
        var name = Path.GetFileName(GetJsonString(file, "name"));
        var cdn = new Uri($"https://edge.forgecdn.net/files/{numericId / 1000}/{numericId % 1000}/{Uri.EscapeDataString(name)}");
        var size = file.TryGetProperty("filesize", out var fileSize) && fileSize.TryGetInt64(out var parsedSize) ? parsedSize : (long?)null;
        return new InstallFile(Path.Combine(modsDirectory, name), "OptiFabric", null, size, [cdn]);
    }

    private async Task RunOptiFineInstallerAsync(
        string installerPath,
        string minecraftVersion,
        string instanceName,
        string gameDirectory,
        string versionDirectory,
        JsonObject profile,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Load();
        var javaPath = settings.SelectedJavaPath;
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            javaPath = (await JavaRuntimeDetector.DetectAsync(
                settings.AdditionalJavaPaths, cancellationToken, settings.ManagedJavaRuntimeDirectories, settings.SelectedGameFolderPath)).FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(javaPath))
            throw new InvalidOperationException("独立安装 OptiFine 需要 Java，请先在设置中添加或选择 Java。");

        var requiredJava = ReadOptiFineRequiredJava(installerPath);
        var selectedRuntime = await JavaRuntimeDetector.ProbeAsync(javaPath, cancellationToken: cancellationToken);
        if (selectedRuntime is null || !Version.TryParse(selectedRuntime.Version.Split('-')[0], out var selectedVersion)
            || selectedVersion.Major < requiredJava)
        {
            var compatibleRuntime = (await JavaRuntimeDetector.DetectAsync(
                    settings.AdditionalJavaPaths, cancellationToken, settings.ManagedJavaRuntimeDirectories, settings.SelectedGameFolderPath))
                .FirstOrDefault(runtime => Version.TryParse(runtime.Version.Split('-')[0], out var version)
                    && version.Major >= requiredJava);
            if (compatibleRuntime is null)
                throw new InvalidOperationException($"OptiFine 安装器需要 Java {requiredJava} 或更高版本，请在设置中添加兼容的 Java。 ");
            javaPath = compatibleRuntime.Path;
        }

        var temporaryHome = Path.Combine(gameDirectory, ".97launch", "tmp", "optifine-" + Guid.NewGuid().ToString("N"));
        var temporaryGame = Path.Combine(temporaryHome, ".minecraft");
        var vanillaDirectory = Path.Combine(temporaryGame, "versions", minecraftVersion);
        Directory.CreateDirectory(vanillaDirectory);
        try
        {
            var vanillaProfile = (JsonObject)profile.DeepClone();
            vanillaProfile["id"] = minecraftVersion;
            await File.WriteAllTextAsync(Path.Combine(vanillaDirectory, minecraftVersion + ".json"),
                vanillaProfile.ToJsonString(), cancellationToken);
            File.Copy(Path.Combine(versionDirectory, Path.GetFileName(versionDirectory) + ".jar"),
                Path.Combine(vanillaDirectory, minecraftVersion + ".jar"), overwrite: true);
            var start = new ProcessStartInfo(javaPath)
            {
                WorkingDirectory = temporaryHome,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add($"-Duser.home={temporaryHome}");
            start.ArgumentList.Add("-cp");
            start.ArgumentList.Add(installerPath);
            start.ArgumentList.Add("optifine.Installer");
            start.Environment["APPDATA"] = temporaryHome;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 OptiFine 安装器。");
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await stderr + Environment.NewLine + await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException($"OptiFine 安装器退出码 {process.ExitCode}：{output.Trim()}");

            var generated = Directory.GetDirectories(Path.Combine(temporaryGame, "versions"))
                .Where(path => !Path.GetFileName(path).Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault(path => Directory.GetFiles(path, "*.json").Length > 0)
                ?? throw new InvalidDataException("OptiFine 安装器没有生成版本配置文件。");
            var generatedJson = Directory.GetFiles(generated, "*.json").First();
            var generatedProfile = JsonNode.Parse(await File.ReadAllTextAsync(generatedJson, cancellationToken)) as JsonObject
                ?? throw new InvalidDataException("OptiFine 生成的版本信息无效。");
            var generatedJar = Path.Combine(generated, Path.GetFileNameWithoutExtension(generatedJson) + ".jar");
            if (File.Exists(generatedJar))
                File.Copy(generatedJar, Path.Combine(versionDirectory, instanceName + ".jar"), overwrite: true);
            MergeLoaderProfile(profile, generatedProfile);
            profile["id"] = instanceName;
        }
        finally
        {
            try { if (Directory.Exists(temporaryHome)) Directory.Delete(temporaryHome, recursive: true); }
            catch (IOException) { }
        }
    }

    private static JsonObject NormalizeMavenLibrary(JsonElement entry, string repository)
    {
        var library = JsonNode.Parse(entry.GetRawText()) as JsonObject ?? new JsonObject();
        var coordinate = GetJsonString(entry, "name");
        var parts = coordinate.Split(':');
        if (parts.Length < 3) return library;
        var group = parts[0].Replace('.', '/');
        var classifier = parts.Length > 3 ? "-" + parts[3] : string.Empty;
        var extension = parts[2].Contains('@') ? parts[2].Split('@')[1] : "jar";
        var artifactVersion = parts[2].Split('@')[0];
        var fileName = $"{parts[1]}-{artifactVersion}{classifier}.{extension}";
        var relative = $"{group}/{parts[1]}/{artifactVersion}/{fileName}";
        var artifact = new JsonObject
        {
            ["path"] = relative,
            ["url"] = new Uri(new Uri(repository), relative).ToString(),
        };
        var md5 = GetJsonString(entry, "md5");
        if (md5.Length == 32) artifact["md5"] = md5;
        library["url"] = repository;
        library["downloads"] = new JsonObject
        {
            ["artifact"] = artifact,
        };
        return library;
    }

    private static int ReadOptiFineRequiredJava(string installerPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(installerPath);
            using var stream = archive.GetEntry("optifine/Installer.class")?.Open()
                ?? throw new InvalidDataException("OptiFine 安装器缺少主类文件。");
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (header[0] != 0xCA || header[1] != 0xFE || header[2] != 0xBA || header[3] != 0xBE)
                throw new InvalidDataException("OptiFine 安装器主类格式无效。");
            var classVersion = header[6] * 256 + header[7];
            return classVersion is >= 49 and <= 100 ? Math.Max(8, classVersion - 44) : 21;
        }
        catch (IOException)
        {
            return 21;
        }
    }

    private async Task<LoaderInstaller> GetForgeInstallerAsync(
        string minecraftVersion,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var options = await GetForgeOptionsCachedAsync(minecraftVersion).WaitAsync(cancellationToken);
        var match = selectedVersion is null
            ? options.FirstOrDefault()
            : options.FirstOrDefault(option => option.Version.Version.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase));
        return match?.Installer
            ?? throw new InvalidDataException($"Forge 没有适用于 Minecraft {minecraftVersion} 的安装器版本。");
    }

    private async Task<List<ForgeInstallerOption>> FetchForgeOptionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<Exception>();
        var settings = _settings.Load();
        foreach (var source in GetForgeVersionSources(settings.VersionDownloadSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = source.IsOfficial
                    ? await GetOfficialForgeOptionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false)
                    : await GetBmclForgeOptionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
                if (options.Count > 0) return options;
                errors.Add(new InvalidDataException($"{source.Name} 返回了空的 Forge 安装器列表。"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                Log.Warning(ex, "从 {Source} 获取 Minecraft {Version} 的 Forge 版本失败", source.Name, minecraftVersion);
            }
        }

        if (errors.Count == 0) return [];
        throw new HttpRequestException("无法从 Forge 版本源获取可安装版本：" + errors[^1].Message, errors[^1]);
    }

    private static IEnumerable<(string Name, bool IsOfficial)> GetForgeVersionSources(string preference)
    {
        var bmcl = ("BMCLAPI", false);
        var official = ("Forge 官网", true);
        if (preference.Equals("官方源", StringComparison.OrdinalIgnoreCase)) return [official];
        if (preference.Equals("BMCLAPI", StringComparison.OrdinalIgnoreCase)) return [bmcl];
        return [bmcl, official];
    }

    private async Task<List<ForgeInstallerOption>> GetBmclForgeOptionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var endpoint = $"https://bmclapi2.bangbang93.com/forge/minecraft/{minecraftVersion.Replace('.', '_')}";
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync([endpoint], cancellationToken).ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("BMCLAPI Forge 版本列表格式无效。");

        var recommendedVersion = await GetForgeRecommendedVersionAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
        var options = new List<ForgeInstallerOption>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var version = GetJsonString(entry, "version");
            if (version.Length == 0 || !entry.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array) continue;
            var installerFile = files.EnumerateArray().FirstOrDefault(file => GetJsonString(file, "category") == "installer"
                && GetJsonString(file, "format") == "jar");
            if (installerFile.ValueKind != JsonValueKind.Object) continue;

            var branch = GetJsonString(entry, "branch");
            var fileVersion = version + (branch.Length == 0 ? "" : "-" + branch);
            var coordinate = $"net/minecraftforge/forge/{minecraftVersion}-{fileVersion}/forge-{minecraftVersion}-{fileVersion}-installer.jar";
            var hash = GetJsonString(installerFile, "hash");
            var installer = new LoaderInstaller($"forge-{minecraftVersion}-{fileVersion}-installer.jar", [
                new Uri("https://bmclapi2.bangbang93.com/maven/" + coordinate),
                new Uri("https://maven.minecraftforge.net/" + coordinate),
            ], hash.Length == 40 ? hash : null, hash.Length == 32 ? hash : null);
            options.Add(new(new LoaderVersionOption(fileVersion, GetReleaseDetail(entry), true,
                fileVersion.Equals(recommendedVersion, StringComparison.OrdinalIgnoreCase)), installer));
        }
        return SortForgeOptions(options);
    }

    private async Task<List<ForgeInstallerOption>> GetOfficialForgeOptionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var url = $"https://files.minecraftforge.net/net/minecraftforge/forge/index_{minecraftVersion}.html";
        var html = await GetTextFromSourcesAsync([url], cancellationToken).ConfigureAwait(false);
        var options = new List<ForgeInstallerOption>();
        foreach (System.Text.RegularExpressions.Match rowMatch in System.Text.RegularExpressions.Regex.Matches(
                     html, @"<tr\b(?<row>.*?)</tr>", System.Text.RegularExpressions.RegexOptions.IgnoreCase
                         | System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var row = rowMatch.Groups["row"].Value;
            var versionCell = System.Text.RegularExpressions.Regex.Match(row,
                @"<td\b[^>]*class=[""'][^""']*download-version[^""']*[""'][^>]*>(?<value>.*?)</td>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!versionCell.Success) continue;
            var version = System.Text.RegularExpressions.Regex.Match(versionCell.Groups["value"].Value,
                @"[0-9A-Za-z][0-9A-Za-z.+_-]*").Value;
            if (version.Length == 0) continue;
            var recommendation = versionCell.Groups["value"].Value.Contains("promo-recommended", StringComparison.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match fileMatch in System.Text.RegularExpressions.Regex.Matches(
                         row, @"<li\b(?<file>.*?)</li>", System.Text.RegularExpressions.RegexOptions.IgnoreCase
                             | System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var file = fileMatch.Groups["file"].Value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(file,
                        @"classifier-installer", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                var directLink = System.Text.RegularExpressions.Regex.Match(file,
                    @"<a\b[^>]*class=[""'][^""']*info-link[^""']*[""'][^>]*href=[""'](?<url>[^""']+)[""'][^>]*title=[""']Direct Download[""']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!directLink.Success) continue;

                var md5 = ReadForgeHash(file, "MD5");
                var sha1 = ReadForgeHash(file, "SHA1");
                var releaseTime = System.Text.RegularExpressions.Regex.Match(row,
                    @"<td\b[^>]*class=[""'][^""']*download-time[^""']*[""'][^>]*title=[""'](?<date>[^""']+)[""']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var detail = releaseTime.Success && DateTimeOffset.TryParse(releaseTime.Groups["date"].Value, out var date)
                    ? $"发布于 {date.ToLocalTime():yyyy/M/d HH:mm}"
                    : "稳定版 · Forge 官网";
                var installer = new LoaderInstaller(
                    $"forge-{minecraftVersion}-{version}-installer.jar",
                    [new Uri(System.Net.WebUtility.HtmlDecode(directLink.Groups["url"].Value))],
                    sha1,
                    md5);
                options.Add(new(new LoaderVersionOption(version, detail, true, recommendation), installer));
                break;
            }
        }
        return SortForgeOptions(options);
    }

    private static string? ReadForgeHash(string fileHtml, string algorithm)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileHtml,
            $@"<strong>\s*{algorithm}:\s*</strong>\s*(?<hash>[a-f0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["hash"].Value : null;
    }

    private static List<ForgeInstallerOption> SortForgeOptions(List<ForgeInstallerOption> options)
        => options.OrderByDescending(option => ParseNumericVersion(option.Version.Version.Split('-')[0])).ToList();

    private async Task<string?> GetForgeRecommendedVersionAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await GetTextFromSourcesAsync(
                ["https://bmclapi2.bangbang93.com/forge/promos"], cancellationToken));
            if (!document.RootElement.TryGetProperty(minecraftVersion + "-recommended", out var promo)) return null;
            return promo.ValueKind == JsonValueKind.String ? promo.GetString() : GetJsonString(promo, "version");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<LoaderInstaller> GetNeoForgeInstallerAsync(
        string minecraftVersion,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var legacy = minecraftVersion == "1.20.1";
        var artifact = legacy ? "forge" : "neoforge";
        var versions = await GetLoaderVersionsAsync("NeoForge", minecraftVersion, cancellationToken);
        var selected = selectedVersion is null
            ? versions.FirstOrDefault()
            : versions.FirstOrDefault(version => version.Version.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new InvalidDataException($"NeoForge 没有适用于 Minecraft {minecraftVersion} 的稳定版本。");

        var coordinate = $"net/neoforged/{artifact}/{selected.Version}/{artifact}-{selected.Version}-installer.jar";
        return new($"{artifact}-{selected.Version}-installer.jar", [
            new Uri("https://bmclapi2.bangbang93.com/maven/" + coordinate),
            new Uri("https://maven.neoforged.net/releases/" + coordinate),
        ]);
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetNeoForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var artifact = minecraftVersion == "1.20.1" ? "forge" : "neoforge";
        var errors = new List<Exception>();
        var settings = _settings.Load();
        var sources = GetNeoForgeVersionSources(artifact, settings.VersionDownloadSource);
        foreach (var (name, endpoint) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contents = await GetTextFromSourcesAsync([endpoint], cancellationToken).ConfigureAwait(false);
                var versions = ParseNeoForgeVersions(contents, minecraftVersion);
                if (versions.Count > 0) return versions;
                errors.Add(new InvalidDataException($"{name} 没有返回适用于 Minecraft {minecraftVersion} 的 NeoForge 版本。"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                Log.Warning(ex, "从 {Source} 获取 Minecraft {Version} 的 NeoForge 版本失败", name, minecraftVersion);
            }
        }
        if (errors.Count == 0) return [];
        throw new HttpRequestException("无法获取 NeoForge 版本列表：" + errors[^1].Message, errors[^1]);
    }

    private static IEnumerable<(string Name, string Endpoint)> GetNeoForgeVersionSources(string artifact, string preference)
    {
        var bmcl = ("BMCLAPI", $"https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/{artifact}");
        var official = ("NeoForged 官网", $"https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/{artifact}");
        if (preference.Equals("官方源", StringComparison.OrdinalIgnoreCase)) return [official];
        if (preference.Equals("BMCLAPI", StringComparison.OrdinalIgnoreCase)) return [bmcl];
        return [bmcl, official];
    }

    private static IReadOnlyList<LoaderVersionOption> ParseNeoForgeVersions(string content, string minecraftVersion)
    {
        var names = System.Text.RegularExpressions.Regex.Matches(content,
                @"(?<version>1\.20\.1-\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.-]+)?|0\.[A-Za-z0-9][A-Za-z0-9.-]*|\d+(?:\.\d+){2,3}(?:-(?:beta|alpha)(?:\.[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?|\+[A-Za-z0-9.-]+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups["version"].Value.TrimEnd('.', ','))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(version => version != "1.20.1-47.1.82")
            .Where(version => PclLoaderRules.TryMapNeoForgeMinecraftVersion(version, out var inherited)
                && inherited.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            .Select(version => new LoaderVersionOption(
                version,
                version.Contains("-beta", StringComparison.OrdinalIgnoreCase)
                    || version.Contains("-alpha", StringComparison.OrdinalIgnoreCase) ? "测试版" : "稳定版",
                !version.Contains("-beta", StringComparison.OrdinalIgnoreCase)
                    && !version.Contains("-alpha", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(option => option.IsStable)
            .ThenByDescending(option => NeoForgeSortVersion(option.Version))
            .ToArray();
        return names;
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetFabricVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var versionsJson = await GetTextFromSourcesAsync(
            [$"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}",
             $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}"],
            cancellationToken);
        using var document = JsonDocument.Parse(versionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Fabric 版本列表格式无效。");

        return document.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("loader", out var loader)
                && loader.TryGetProperty("version", out _))
            .Select(item =>
            {
                var loader = item.GetProperty("loader");
                var stable = loader.TryGetProperty("stable", out var value)
                    && value.ValueKind == JsonValueKind.True;
                return new LoaderVersionOption(
                    loader.GetProperty("version").GetString() ?? string.Empty,
                    stable ? "稳定版" : "测试版",
                    stable);
            })
            .Where(option => option.Version.Length > 0)
            .DistinctBy(option => option.Version, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(option => option.IsStable)
            .ThenByDescending(option => ParseNumericVersion(option.Version))
            .ToArray();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetOptiFineVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync(
            ["https://bmclapi2.bangbang93.com/optifine/versionList"], cancellationToken));
        return document.RootElement.EnumerateArray()
            .Where(entry => GetJsonString(entry, "mcversion").Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                var type = GetJsonString(entry, "type");
                var patch = GetJsonString(entry, "patch");
                var file = GetJsonString(entry, "filename");
                var preview = patch.Contains("pre", StringComparison.OrdinalIgnoreCase);
                var typeLabel = type.Replace("HD_U", string.Empty, StringComparison.OrdinalIgnoreCase).Replace('_', ' ').Trim();
                var label = string.Join(' ', new[] { minecraftVersion, typeLabel, patch }.Where(value => value.Length > 0));
                return new LoaderVersionOption(label, preview ? "预览版 · BMCLAPI" : "稳定版 · BMCLAPI", !preview,
                    Id: file, Compatibility: GetJsonStringOrNull(entry, "forge"));
            })
            .Where(option => !string.IsNullOrWhiteSpace(option.Id))
            .ToArray();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetLiteLoaderVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (!TryGetMinecraftVersion(minecraftVersion, out var version) || version >= new Version(1, 13)) return [];
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync(
            ["https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json",
             "https://dl.liteloader.com/versions/versions.json"], cancellationToken));
        if (!document.RootElement.TryGetProperty("versions", out var versions)
            || !versions.TryGetProperty(minecraftVersion, out var gameVersion)) return [];
        var entry = FindLiteLoaderLatest(gameVersion);
        if (entry.ValueKind != JsonValueKind.Object) return [];
        var versionName = GetJsonString(entry, "version");
        var timestamp = GetJsonString(entry, "timestamp");
        var stable = !GetJsonString(entry, "stream").Equals("SNAPSHOT", StringComparison.OrdinalIgnoreCase);
        var date = long.TryParse(timestamp, out var unix)
            ? $" · 发布于 {DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime():yyyy/M/d HH:mm}"
            : string.Empty;
        var detail = (stable ? "稳定版" : "测试版") + date;
        return string.IsNullOrWhiteSpace(versionName)
            ? []
            : [new LoaderVersionOption(versionName, detail, stable)];
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetModrinthVersionsAsync(
        string project,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.modrinth.com/v2/project/{project}/version?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }))}"
            + $"&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}&include_changelog=false";
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync([url], cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        return document.RootElement.EnumerateArray()
            .Select(item => new LoaderVersionOption(
                GetJsonString(item, "version_number"),
                GetJsonString(item, "version_type") == "release" ? "稳定版 · Modrinth" : "测试版 · Modrinth",
                GetJsonString(item, "version_type") == "release",
                Id: GetJsonString(item, "id")))
            .Where(option => !string.IsNullOrWhiteSpace(option.Version))
            .ToArray();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetOptiFabricVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await GetTextFromSourcesAsync(
            ["https://api.cfwidget.com/322385"], cancellationToken));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
        return files.EnumerateArray()
            .Where(file => file.TryGetProperty("versions", out var supported)
                && supported.ValueKind == JsonValueKind.Array
                && supported.EnumerateArray().Any(value => value.GetString()?.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(file => GetJsonString(file, "uploaded_at"), StringComparer.Ordinal)
            .Select(file => new LoaderVersionOption(
                GetJsonString(file, "display"),
                "OptiFabric · CurseForge",
                true,
                Id: GetJsonString(file, "id")))
            .ToArray();
    }

    private static JsonElement FindLiteLoaderLatest(JsonElement gameVersion)
    {
        foreach (var key in new[] { "artefacts", "snapshots" })
        {
            if (!gameVersion.TryGetProperty(key, out var artifacts)
                || !artifacts.TryGetProperty("com.mumfrey:liteloader", out var versions)) continue;
            if (versions.TryGetProperty("latest", out var latest)) return latest;
            return versions.EnumerateObject()
                .Where(pair => pair.Name != "latest")
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }
        return default;
    }

    private static bool TryGetMinecraftVersion(string value, out Version version)
    {
        var numeric = value.Split('-', '+')[0];
        if (Version.TryParse(numeric, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    private async Task<(JsonObject Profile, string Directory)> PrepareLoaderLibrariesAsync(
        InstallRequest request,
        JsonObject minecraft,
        string installerPath,
        string versionDirectory,
        string[] reuseRoots,
        SemaphoreSlim networkSlots,
        IProgress<InstallProgress> progress,
        int stage,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(installerPath);
        async Task<JsonObject?> ReadProfileAsync(string name)
        {
            var entry = archive.GetEntry(name);
            if (entry is null) return null;
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("安装器版本信息过大。");
            using var reader = new StreamReader(entry.Open());
            return JsonNode.Parse(await reader.ReadToEndAsync(cancellationToken)) as JsonObject
                ?? throw new InvalidDataException($"安装器中的 {name} 格式无效。");
        }

        var install = await ReadProfileAsync("install_profile.json")
            ?? throw new InvalidDataException("安装器缺少 install_profile.json。");
        var version = await ReadProfileAsync("version.json") ?? install["versionInfo"] as JsonObject
            ?? throw new InvalidDataException("安装器缺少客户端版本信息。");
        var files = ReadLibraries(install, request.GameDirectory);
        files.AddRange(ReadLibraries(version, request.GameDirectory));

        // PCL prepares MOJMAPS before running the installer; other data paths are processor outputs.
        if (install["data"]?["MOJMAPS"]?["client"]?.GetValue<string>() is { } mappings && mappings.StartsWith('['))
        {
            var coordinate = mappings.Trim('[', ']');
            var pieces = coordinate.Split('@', 2);
            var parts = pieces[0].Split(':');
            if (parts.Length is < 3 or > 4) throw new InvalidDataException("安装器映射文件坐标无效。");
            var extension = pieces.Length == 2 ? pieces[1] : "jar";
            var classifier = parts.Length == 4 ? "-" + parts[3] : string.Empty;
            var relative = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{classifier}.{extension}";
            var path = Path.Combine(request.GameDirectory, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));
            if (minecraft["downloads"]?["client_mappings"] is not JsonObject metadata
                || !Uri.TryCreate(metadata["url"]?.GetValue<string>(), UriKind.Absolute, out var mappingsUrl))
                throw new InvalidDataException("Minecraft 版本信息缺少安装加载器所需的客户端映射文件。");
            files.Add(new InstallFile(path, "映射文件", metadata["sha1"]?.GetValue<string>(),
                metadata["size"]?.GetValue<long?>(), Sources(mappingsUrl.ToString(), isAssetIndex: true)));
        }

        var librariesRoot = Path.Combine(request.GameDirectory, "libraries");
        foreach (var file in files) InstanceContentService.ValidatePath(librariesRoot, file.Path);
        var downloads = files.GroupBy(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(file => file.Sha1 is not null).First()).ToList();
        Log.Information("准备 {Loader} {Version} 安装依赖：{Count} 个文件", request.Loader, request.LoaderVersion, downloads.Count);
        await DownloadStageAsync(downloads, stage, $"下载 {request.Loader} 安装依赖", request.GameDirectory,
            reuseRoots, networkSlots, progress, cancellationToken, preserveOtherStages: true,
            progressOffset: 0.25, progressScale: 0.4);

        // Keep the installer's vanilla and loader profiles outside the scanned versions directory.
        var workspace = Path.Combine(request.GameDirectory, ".97launch", "tmp", "loader-" + Guid.NewGuid().ToString("N"));
        InstanceContentService.ValidatePath(request.GameDirectory, workspace);
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "install-task.json"), JsonSerializer.Serialize(new
        {
            TargetInstancePath = Path.GetFullPath(versionDirectory),
            request.Version, request.Loader, request.LoaderVersion, CreatedAt = DateTimeOffset.UtcNow,
        }), cancellationToken);
        Log.Information("加载器安装工作目录：{Workspace}，目标实例：{Target}", workspace, versionDirectory);
        foreach (var file in downloads)
        {
            var destination = Path.Combine(workspace, "libraries", Path.GetRelativePath(librariesRoot, file.Path));
            InstanceContentService.ValidatePath(workspace, destination);
            await CopyInstallerFileAsync(file.Path, destination, cancellationToken);
        }

        var vanillaDirectory = Path.Combine(workspace, "versions", SanitizeName(request.Version));
        Directory.CreateDirectory(vanillaDirectory);
        var vanillaJsonPath = Path.Combine(vanillaDirectory, request.Version + ".json");
        var vanilla = (JsonObject)minecraft.DeepClone();
        vanilla["id"] = request.Version;
        await File.WriteAllTextAsync(vanillaJsonPath, vanilla.ToJsonString(), cancellationToken);
        var vanillaJar = Path.Combine(vanillaDirectory, request.Version + ".jar");
        await CopyInstallerFileAsync(Path.Combine(versionDirectory, SanitizeName(request.Name) + ".jar"), vanillaJar, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace, "launcher_profiles.json"), "{\"profiles\":{}}", cancellationToken);
        return (version, workspace);
    }

    private static async Task CopyInstallerFileAsync(string source, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await input.CopyToAsync(output, token);
    }

    private static async Task ImportLoaderLibrariesAsync(string workspace, string gameDirectory, CancellationToken token)
    {
        var stagedLibraries = Path.Combine(workspace, "libraries");
        var librariesRoot = Path.Combine(gameDirectory, "libraries");
        foreach (var source in Directory.EnumerateFiles(stagedLibraries, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            InstanceContentService.ValidatePath(stagedLibraries, source);
            var destination = Path.Combine(librariesRoot, Path.GetRelativePath(stagedLibraries, source));
            InstanceContentService.ValidatePath(librariesRoot, destination);
            string hash;
            await using (var input = File.OpenRead(source))
                hash = Convert.ToHexString(await SHA1.HashDataAsync(input, token));
            if (await IsValidAsync(destination, hash, new FileInfo(source).Length, token)) continue;
            var temporary = destination + ".97launch-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await CopyInstallerFileAsync(source, temporary, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally { TryDelete(temporary); }
        }
    }

    private static void TryDeleteLoaderWorkspace(string gameDirectory, string workspace)
    {
        try
        {
            InstanceContentService.ValidatePath(Path.Combine(gameDirectory, ".97launch", "tmp"), workspace);
            if (!Path.GetFileName(workspace).StartsWith("loader-", StringComparison.Ordinal)
                || !File.Exists(Path.Combine(workspace, "install-task.json"))) return;
            Directory.Delete(workspace, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "无法清理加载器安装工作目录：{Workspace}", workspace);
        }
    }

    private async Task<string> RunLoaderInstallerAsync(
        string loader,
        string installerPath,
        string gameDirectory,
        string javaPath,
        string? profileId,
        CancellationToken cancellationToken)
    {
        var versionFolder = Path.Combine(gameDirectory, "versions");
        Directory.CreateDirectory(versionFolder);
        var previous = Directory.GetDirectories(versionFolder)
            .ToDictionary(path => path, Directory.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        var startTime = DateTime.UtcNow;
        var start = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-Djava.net.useSystemProxies=true");
        var repository = new Uri(loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase)
            ? "https://maven.neoforged.net/" : "https://maven.minecraftforge.net/");
        if (!HttpClient.DefaultProxy.IsBypassed(repository) && HttpClient.DefaultProxy.GetProxy(repository) is { } proxy
            && proxy != repository && proxy.Scheme == "http")
        {
            foreach (var protocol in new[] { "http", "https" })
            {
                start.ArgumentList.Add($"-D{protocol}.proxyHost={proxy.Host}");
                start.ArgumentList.Add($"-D{protocol}.proxyPort={proxy.Port}");
            }
        }
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(installerPath);
        start.ArgumentList.Add("--installClient");
        start.ArgumentList.Add(gameDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 Java 安装 {loader}。");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var output = (await stderrTask) + Environment.NewLine + (await stdoutTask);
            Log.Error("{Loader} 安装器退出码 {ExitCode}：{Output}", loader, process.ExitCode, output);
            throw new InvalidOperationException($"{loader} 安装失败（退出码 {process.ExitCode}）。日志：{Path.Combine(gameDirectory, Path.GetFileName(installerPath) + ".log")}");
        }

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var expected = Path.Combine(versionFolder, SanitizeName(profileId), profileId + ".json");
            if (File.Exists(expected)) return expected;
        }
        return Directory.GetDirectories(versionFolder)
            .Where(path => path.Contains(loader, StringComparison.OrdinalIgnoreCase)
                && ( !previous.TryGetValue(path, out var lastWrite) || lastWrite < startTime
                    || Directory.GetLastWriteTimeUtc(path) > lastWrite))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .SelectMany(path => Directory.GetFiles(path, "*.json"))
            .FirstOrDefault() ?? throw new InvalidDataException($"{loader} 安装器完成，但没有生成版本配置文件。");
    }

    private static void MergeLoaderProfile(JsonObject minecraft, JsonObject loader)
    {
        var libraries = minecraft["libraries"] as JsonArray ?? new JsonArray();
        var knownLibraries = libraries.OfType<JsonObject>()
            .Select(item => item["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (loader["libraries"] is JsonArray loaderLibraries)
        {
            foreach (var library in loaderLibraries.OfType<JsonObject>())
            {
                var name = library["name"]?.GetValue<string>();
                if (name is null || knownLibraries.Add(name)) libraries.Add(library.DeepClone());
            }
        }
        minecraft["libraries"] = libraries;

        if (loader["arguments"] is JsonObject loaderArguments)
        {
            var arguments = minecraft["arguments"] as JsonObject ?? new JsonObject();
            foreach (var key in new[] { "game", "jvm" })
            {
                if (loaderArguments[key] is not JsonArray additions) continue;
                var target = arguments[key] as JsonArray ?? new JsonArray();
                foreach (var value in additions) target.Add(value?.DeepClone());
                arguments[key] = target;
            }
            minecraft["arguments"] = arguments;
        }
        if (loader["mainClass"] is JsonNode mainClass) minecraft["mainClass"] = mainClass.DeepClone();
        if (loader["minecraftArguments"] is JsonNode minecraftArguments)
            minecraft["minecraftArguments"] = minecraftArguments.DeepClone();
        minecraft.Remove("inheritsFrom");
    }

    private async Task<string> GetTextFromSourcesAsync(IEnumerable<string> sources, CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                request.Headers.UserAgent.ParseAdd("97launch/0.1.0");
                using var response = await LoaderHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(ex); }
        }
        throw new HttpRequestException("无法获取加载器信息：" + errors.LastOrDefault()?.Message,
            errors.LastOrDefault());
    }

    private static Version NeoForgeSortVersion(string version)
    {
        var numeric = version.StartsWith("1.20.1-", StringComparison.Ordinal)
            ? version[8..]
            : version.Split('-', '+')[0];
        return Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0);
    }

    private static string GetJsonString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetJsonStringOrNull(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetReleaseDetail(JsonElement entry)
    {
        foreach (var property in new[] { "modified", "releaseTime", "date", "time" })
        {
            var value = GetJsonString(entry, property);
            if (DateTimeOffset.TryParse(value, out var date))
                return $"发布于 {date.ToLocalTime():yyyy/M/d HH:mm}";
        }
        return "稳定版";
    }

    private static Version ParseNumericVersion(string value)
    {
        var numeric = value.Split('-', '+')[0];
        return Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0);
    }

    private sealed record ForgeInstallerOption(LoaderVersionOption Version, LoaderInstaller Installer);
    private sealed record LoaderInstaller(string FileName, List<Uri> Sources, string? Sha1 = null, string? Md5 = null);

    public async Task DownloadFilesAsync(
        string gameDirectory,
        string stageName,
        IReadOnlyList<DownloadFileRequest> files,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken,
        string? authorizedDestinationPath = null)
    {
        gameDirectory = Path.GetFullPath(gameDirectory);
        var settings = _settings.Load();
        using var networkSlots = _downloads.CreateNetworkSlots(settings.DownloadThreads);
        var reuseRoots = _folders.Discover().Select(folder => Path.GetFullPath(folder.Path))
            .Append(gameDirectory)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var options = new DownloadFileOptions(settings.DownloadThreads, settings.DownloadSpeedLimitKb,
            settings.FileDownloadSource, gameDirectory, reuseRoots, authorizedDestinationPath);
        var reporter = new ActionProgress<DownloadBatchProgress>(update =>
        {
            progress.Report(new(0, update.Progress, stageName, update.BytesPerSecond));
        });
        await _downloads.DownloadFilesAsync(files, options, reporter, cancellationToken, networkSlots);
    }

    private async Task DownloadStageAsync(
        List<InstallFile> files,
        int stage,
        string stageName,
        string gameDirectory,
        string[] reuseRoots,
        SemaphoreSlim networkSlots,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken,
        bool preserveOtherStages = false,
        double progressOffset = 0,
        double progressScale = 1)
    {
        var preferences = _settings.Load();
        progress.Report(new(stage, 0, $"{stageName}：准备文件列表", 0, preserveOtherStages));
        var options = new DownloadFileOptions(
            preferences.DownloadThreads,
            preferences.DownloadSpeedLimitKb,
            preferences.FileDownloadSource,
            gameDirectory,
            reuseRoots);
        var requests = files.Select(file => new DownloadFileRequest(
            file.Path, file.Kind, file.Sha1, file.Size, file.Sources, file.Md5));
        var reporter = new ActionProgress<DownloadBatchProgress>(update =>
        {
            progress.Report(new(stage, progressOffset + update.Progress * progressScale, stageName,
                update.BytesPerSecond, preserveOtherStages));
        });
        await _downloads.DownloadFilesAsync(requests, options, reporter, cancellationToken, networkSlots);
    }

    private async Task DownloadAssetsAsync(
        InstallFile assetIndexFile,
        string indexPath,
        string gameDirectory,
        string[] reuseRoots,
        SemaphoreSlim networkSlots,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        await DownloadStageAsync([assetIndexFile], 2, "下载资源索引", gameDirectory, reuseRoots,
            networkSlots, progress, cancellationToken, preserveOtherStages: true);
        var indexJson = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken)) as JsonObject
            ?? throw new InvalidDataException("资源索引解析失败。");
        var assetFiles = ReadAssets(indexJson, gameDirectory);
        await DownloadStageAsync(assetFiles, 3, "下载游戏资源", gameDirectory, reuseRoots,
            networkSlots, progress, cancellationToken, preserveOtherStages: true);
    }

    private static async Task<bool> IsValidAsync(
        string path,
        string? sha1,
        long? size,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (size is > 0 && info.Length != size.Value) return false;
            if (sha1 is null) return size.HasValue;
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return actual.Equals(sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool AddFile(
        JsonObject data,
        string path,
        string kind,
        List<InstallFile> files,
        Uri? extraSource = null,
        bool isLibrary = false)
    {
        var url = data["url"]?.GetValue<string>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return false;
        var sources = Sources(url, mirrorPath: data["path"]?.GetValue<string>(), isLibrary: isLibrary);
        if (extraSource is not null && !sources.Contains(extraSource)) sources.Add(extraSource);
        files.Add(new InstallFile(path, kind, data["sha1"]?.GetValue<string>(),
            data["size"]?.GetValue<long?>(), sources, data["md5"]?.GetValue<string>()));
        return true;
    }

    private static List<InstallFile> ReadLibraries(JsonObject root, string gameDirectory)
    {
        var result = new List<InstallFile>();
        if (root["libraries"] is not JsonArray libraries) return result;

        foreach (var library in libraries.OfType<JsonObject>())
        {
            if (!RulesAllow(library["rules"] as JsonArray)) continue;
            var downloads = library["downloads"] as JsonObject;
            if (downloads is null)
            {
                if (library["name"]?.GetValue<string>() is { } coordinate && coordinate.Count(character => character == ':') >= 2)
                {
                    using var document = JsonDocument.Parse(library.ToJsonString());
                    var repository = library["url"]?.GetValue<string>() ?? "https://bmclapi2.bangbang93.com/maven/";
                    var normalized = NormalizeMavenLibrary(document.RootElement, repository);
                    library["url"] = normalized["url"]?.DeepClone();
                    library["downloads"] = normalized["downloads"]?.DeepClone();
                    downloads = library["downloads"] as JsonObject ?? new JsonObject();
                }
            }
            if (downloads?["artifact"] is JsonObject artifact)
                AddFile(artifact, LibraryPath(gameDirectory, artifact), "依赖库", result, isLibrary: true);

            if (downloads?["classifiers"] is not JsonObject classifiers
                || library["natives"] is not JsonObject natives)
                continue;

            var osName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var classifier = natives[osName]?.GetValue<string>();
            if (classifier is null) continue;
            classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
            if (classifiers[classifier] is JsonObject native)
                AddFile(native, LibraryPath(gameDirectory, native), "依赖库", result, isLibrary: true);
        }

        return result;
    }

    private static bool RulesAllow(JsonArray? rules)
    {
        if (rules is null) return true;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var allowed = false;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var ruleOs = rule["os"]?["name"]?.GetValue<string>();
            if (ruleOs is not null && ruleOs != os) continue;
            allowed = rule["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }

    private static string LibraryPath(string gameDirectory, JsonObject file)
    {
        if (file["path"]?.GetValue<string>() is { Length: > 0 } path)
            return Path.Combine(gameDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar));
        var uri = new Uri(file["url"]!.GetValue<string>());
        return Path.Combine(gameDirectory, "libraries", Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
            .Replace('/', Path.DirectorySeparatorChar));
    }

    private static List<InstallFile> ReadAssets(JsonObject index, string gameDirectory)
    {
        var result = new List<InstallFile>();
        if (index["objects"] is not JsonObject objects) return result;
        foreach (var item in objects.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var hash = item["hash"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
            var relative = Path.Combine(hash[..2], hash);
            var url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
            result.Add(new InstallFile(Path.Combine(gameDirectory, "assets", "objects", relative), "游戏资源",
                hash, item["size"]?.GetValue<long?>(), Sources(url, isAssetIndex: true)));
        }
        return result;
    }

    private static bool ShouldIsolate(string mode, bool hasModLoader, string versionType) => mode switch
    {
        "关闭" => false,
        "隔离可安装 Mod 的版本" => hasModLoader,
        "隔离非正式版" => !versionType.Equals("release", StringComparison.OrdinalIgnoreCase),
        "隔离可安装 Mod 的版本与非正式版" => hasModLoader
            || !versionType.Equals("release", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static void BuildLegacyAssetDirectories(
        JsonObject index,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        if (index["objects"] is not JsonObject objects) return;
        var legacy = index["virtual"]?.GetValue<bool>() == true;
        var mapToResources = index["map_to_resources"]?.GetValue<bool>() == true;
        if (!legacy && !mapToResources) return;

        var destinationRoot = Path.Combine(gameDirectory, legacy
            ? Path.Combine("assets", "virtual", "legacy")
            : "resources");
        foreach (var (name, value) in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = value?["hash"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
            var source = Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash);
            var destination = Path.Combine(destinationRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination) && File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }
    }

    private static List<Uri> Sources(
        string url,
        bool isAssetIndex = false,
        string? mirrorPath = null,
        bool isLibrary = false)
    {
        var result = new List<Uri>();
        if (Uri.TryCreate(url, UriKind.Absolute, out var official)) result.Add(official);
        string? mirror = null;
        if (isLibrary && official is not null)
        {
            var libraryPath = string.IsNullOrWhiteSpace(mirrorPath)
                ? Uri.UnescapeDataString(official.AbsolutePath.TrimStart('/'))
                : mirrorPath.TrimStart('/');
            AddMirror($"https://bmclapi2.bangbang93.com/maven/{libraryPath}");
            AddMirror($"https://bmclapi2.bangbang93.com/libraries/{libraryPath}");
        }
        else if (url.Contains("resources.download.minecraft.net/", StringComparison.OrdinalIgnoreCase))
            mirror = url.Replace("resources.download.minecraft.net/", "bmclapi2.bangbang93.com/assets/", StringComparison.OrdinalIgnoreCase);
        else if (url.Contains("/maven/", StringComparison.OrdinalIgnoreCase))
            mirror = "https://bmclapi2.bangbang93.com/maven/" + url[(url.IndexOf("/maven/", StringComparison.OrdinalIgnoreCase) + 7)..];
        else if (mirrorPath is not null && Uri.TryCreate(url, UriKind.Absolute, out var libraryUri)
                 && libraryUri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
            mirror = "https://bmclapi2.bangbang93.com/maven/" + mirrorPath;
        else if (isAssetIndex)
            mirror = "https://bmclapi2.bangbang93.com/" + new Uri(url).AbsolutePath.TrimStart('/');
        if (mirror is not null) AddMirror(mirror);
        return result;

        void AddMirror(string candidate)
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var mirrored) && !result.Contains(mirrored))
                result.Add(mirrored);
        }
    }

    private static string SanitizeName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0 || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("实例名称包含无效字符。", nameof(value));
        return name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record InstallFile(string Path, string Kind, string? Sha1, long? Size, List<Uri> Sources, string? Md5 = null);
}
