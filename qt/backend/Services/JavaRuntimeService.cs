using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;

namespace Launcher.App.Services;

public sealed record JavaRuntimeCheck(JavaRuntimeInfo? Runtime, bool IsCompatible, string Recommendation);

/// <summary>Chooses a runtime compatible with the selected Minecraft instance, downloading one in automatic mode.</summary>
public sealed class JavaRuntimeService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<JavaRuntimeService>();
    private const string RuntimeIndex = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SettingsStore _settings;
    private readonly GameFolderService _folders;
    private readonly PclFileDownloadManager _downloads;
    private readonly JavaRuntimeChangeNotifier _runtimeChanges;

    public JavaRuntimeService(
        SettingsStore settings,
        GameFolderService folders,
        PclFileDownloadManager downloads,
        JavaRuntimeChangeNotifier runtimeChanges)
    {
        _settings = settings;
        _folders = folders;
        _downloads = downloads;
        _runtimeChanges = runtimeChanges;
    }

    public async Task<JavaRuntimeCheck> CheckInstalledAsync(
        InstanceItem instance,
        IReadOnlyList<JsonObject> profiles,
        InstanceLaunchSettings instanceSettings,
        LoginType loginType,
        CancellationToken cancellationToken)
    {
        var requirement = GetRequirement(instance, profiles, loginType);
        if (instanceSettings.JavaMode == "使用实例文件夹中的 Java")
        {
            JavaRuntimeInfo? found = null;
            var compatible = await FindJavaInFolderAsync(instance.Path, cancellationToken, requirement,
                onRuntimeFound: runtime => found ??= runtime);
            return new(compatible ?? found, compatible is not null, requirement.Recommendation);
        }

        if (instanceSettings.JavaMode == "强制指定 Java")
        {
            var forced = await JavaRuntimeDetector.ProbeAsync(instanceSettings.JavaPath, cancellationToken: cancellationToken);
            return new(forced, forced is not null && IsCompatible(forced, requirement), requirement.Recommendation);
        }

        var settings = _settings.Load();
        var paths = new[] { settings.SelectedJavaPath }
            .Concat(settings.AdditionalJavaPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path));
        var runtimes = await JavaRuntimeDetector.DetectAsync(
            paths, cancellationToken, settings.ManagedJavaRuntimeDirectories, settings.SelectedGameFolderPath);
        var selected = SelectInstalledRuntime(runtimes, requirement);
        var fallback = runtimes.FirstOrDefault(runtime =>
            JavaRuntimeDetector.IsSameRuntime(runtime.Path, settings.SelectedJavaPath)) ?? runtimes.FirstOrDefault();
        return new(selected ?? fallback, selected is not null, requirement.Recommendation);
    }

    public async Task<JavaRuntimeInfo> SelectAsync(
        InstanceItem instance,
        IReadOnlyList<JsonObject> profiles,
        InstanceLaunchSettings instanceSettings,
        LoginType loginType,
        IProgress<LaunchProgress> progress,
        CancellationToken cancellationToken)
    {
        var launcherSettings = _settings.Load();
        var requirement = GetRequirement(instance, profiles, loginType);
        progress.Report(new("检查 Java", null));

        var installed = await CheckInstalledAsync(
            instance, profiles, instanceSettings, loginType, cancellationToken);
        if (installed.IsCompatible && installed.Runtime is { } runtime)
        {
            Log.Information("已选择兼容 Java：{Version}（{Path}），实例：{Instance}", runtime.Version, runtime.Path, instance.Name);
            return runtime;
        }
        if (instanceSettings.JavaMode != "自动选择并按需下载")
            throw new InvalidOperationException((installed.Runtime is { } incompatible
                ? $"Java {incompatible.Version} 版本不匹配。" : "当前指定的 Java 不可用。")
                + installed.Recommendation + "。请在实例设置中更换 Java，或改为自动选择并按需下载。");

        var component = await FindDownloadComponentAsync(requirement, progress, cancellationToken);
        Log.Information("自动安装 Java：{Version}，实例：{Instance}", component.Version, instance.Name);
        return await DownloadRuntimeAsync(component, requirement, launcherSettings, progress, cancellationToken);
    }

    private async Task<JavaRuntimeInfo?> FindJavaInFolderAsync(
        string directory,
        CancellationToken cancellationToken,
        JavaRequirement? requirement = null,
        Action<string, string>? onProbeFailure = null,
        Action<JavaRuntimeInfo>? onRuntimeFound = null)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var name in new[] { "javaw.exe", "java.exe" })
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories).Take(50).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runtime = await JavaRuntimeDetector.ProbeAsync(
                    file, cancellationToken: cancellationToken, onProbeFailure: onProbeFailure);
                if (runtime is not null)
                {
                    onRuntimeFound?.Invoke(runtime);
                    if (requirement is null || IsCompatible(runtime, requirement)) return runtime;
                }
            }
        }
        return null;
    }

    private static JavaRequirement GetRequirement(
        InstanceItem instance,
        IReadOnlyList<JsonObject> profiles,
        LoginType loginType)
    {
        var requirement = new JavaRequirement();
        var gameVersion = ParseMinecraftVersion(instance.MinecraftVersionId);
        var releaseTime = profiles.Select(profile => TryDate(profile["releaseTime"]?.GetValue<string>()))
            .FirstOrDefault(date => date.HasValue);

        // Keep the launcher's version thresholds aligned with PCL's GetJavaRequirement.
        if (gameVersion is { } version)
        {
            if (version.Major > 20 || version.Major == 20 && version.Build >= 5) requirement.AddMinimum(new Version(21, 0));
            else if (version.Major >= 18) requirement.AddMinimum(new Version(17, 0));
            else if (version.Major >= 17) requirement.AddMinimum(new Version(16, 0));
            else if (version.Major >= 12) requirement.AddMinimum(new Version(8, 0));
            else if (version.Major <= 5 && releaseTime is { } oldDate
                     && oldDate.Year >= 2001 && oldDate <= new DateTime(2013, 5, 1))
                requirement.AddMaximum(new Version(9, 0), exclusive: true);
        }
        else if (releaseTime is { } date)
        {
            if (date >= new DateTime(2024, 4, 2)) requirement.AddMinimum(new Version(21, 0));
            else if (date >= new DateTime(2021, 11, 16)) requirement.AddMinimum(new Version(17, 0));
            else if (date >= new DateTime(2021, 5, 11)) requirement.AddMinimum(new Version(16, 0));
            else if (date.Year >= 2017) requirement.AddMinimum(new Version(8, 0));
            else if (date.Year >= 2001 && date <= new DateTime(2013, 5, 1)) requirement.AddMaximum(new Version(9, 0), exclusive: true);
        }

        string? recommended = null;
        foreach (var profile in profiles)
        {
            var javaVersion = profile["javaVersion"] as JsonObject;
            var major = ReadInt(javaVersion?["majorVersion"] ?? profile["java_version"]);
            if (major > 0)
            {
                requirement.AddMinimum(new Version(major, 0));
                var component = ReadString(javaVersion?["component"] ?? profile["java_component"]);
                if (!string.IsNullOrWhiteSpace(component)) recommended ??= component;
            }
        }
        requirement.RecommendedComponent = recommended;

        var loader = instance.Loader;
        var loaderVersion = ExtractLoaderVersion(instance.LoaderVersion);
        if (loader.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            if (gameVersion is { Major: < 7 } || gameVersion is { Major: 12 }) requirement.AddMaximum(new Version(9, 0), exclusive: true);
            else if (gameVersion is { Major: >= 8 and < 12 })
            {
                requirement.AddMinimum(new Version(8, 0));
                requirement.AddMaximum(new Version(9, 0), exclusive: true);
            }
        }

        if (loader.Contains("Forge", StringComparison.OrdinalIgnoreCase))
        {
            if (gameVersion is { Major: 6, Build: >= 1 } or { Major: 7, Build: <= 2 })
            {
                requirement.AddMinimum(new Version(7, 0));
                requirement.AddMaximum(new Version(8, 0), exclusive: true);
            }
            else if (gameVersion is { Major: <= 12 } or null) requirement.AddMaximum(new Version(9, 0), exclusive: true);
            else if (gameVersion is { Major: 13 or 14 })
            {
                requirement.AddMinimum(new Version(8, 0));
                requirement.AddMaximum(new Version(11, 0), exclusive: true);
            }
            else if (gameVersion is { Major: 15 })
            {
                requirement.AddMinimum(new Version(8, 0));
                requirement.AddMaximum(new Version(16, 0), exclusive: true);
            }
            else if (InVersionRange(loaderVersion, "34.0.0", "36.2.25")) requirement.AddMaximum(new Version(8, 0, 320));
            else if (AtLeast(loaderVersion, "36.2.26") && LessThan(loaderVersion, "37.0.0")) requirement.AddMaximum(new Version(24, 0), exclusive: true);
            else if (InVersionRange(loaderVersion, "37.0.0", "37.0.79")) requirement.AddMaximum(new Version(17, 0), exclusive: true);
            else if (gameVersion is { Major: 18 } && loader.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
                requirement.AddMaximum(new Version(19, 0), exclusive: true);
            else if (InVersionRange(loaderVersion, "45.0.21", "45.0.65")) requirement.AddMaximum(new Version(20, 0), exclusive: true);
            else if (InVersionRange(loaderVersion, "45.0.66", "47.4.8")) requirement.AddMaximum(new Version(22, 0), exclusive: true);
        }

        if (loader.Contains("NeoForge", StringComparison.OrdinalIgnoreCase)
            && (gameVersion == new Version(20, 0, 1)
                || AtLeast(loaderVersion, "20.2.62") && !loaderVersion.Contains("25w14craftmine", StringComparison.OrdinalIgnoreCase)))
            requirement.AddMaximum(new Version(22, 0), exclusive: true);

        if (loader.Contains("Fabric", StringComparison.OrdinalIgnoreCase) && gameVersion is { } fabricVersion)
        {
            if (fabricVersion.Major is 15 or 16) requirement.AddMinimum(new Version(8, 0));
            else if (fabricVersion.Major >= 18) requirement.AddMinimum(new Version(17, 0));
            if (TryVersion(loaderVersion, out var fabricLoader) && fabricLoader < new Version(0, 17, 0))
                requirement.AddMaximum(new Version(25, 0), exclusive: true);
        }

        if (loader.Contains("LiteLoader", StringComparison.OrdinalIgnoreCase))
            requirement.AddMaximum(new Version(9, 0), exclusive: true);
        if (loginType == LoginType.Nide) requirement.AddMinimum(new Version(8, 0, 141));

        return requirement;
    }

    private async Task<DownloadComponent> FindDownloadComponentAsync(
        JavaRequirement requirement,
        IProgress<LaunchProgress> progress,
        CancellationToken cancellationToken)
    {
        if (requirement.IsEmpty)
            throw new InvalidOperationException("当前版本的 Java 版本要求互相冲突，请检查实例设置和 Mod 加载器版本。");

        progress.Report(new("获取 Java 下载清单", null));
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "windows-x64",
            Architecture.Arm64 => "windows-arm64",
            _ => Environment.Is64BitOperatingSystem ? "windows-x64" : "windows-x86",
        };
        Exception? lastError = null;
        foreach (var url in new[] { RuntimeIndex, RuntimeIndex.Replace("piston-meta.mojang.com", "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase) })
        {
            try
            {
                var json = JsonNode.Parse(await Http.GetStringAsync(url, cancellationToken)) as JsonObject
                    ?? throw new InvalidDataException("Java 下载清单格式无效。");
                if (json[architecture] is not JsonObject components)
                    throw new InvalidDataException($"Java 下载清单没有 {architecture} 运行时。");

                var choices = new List<DownloadComponent>();
                foreach (var (name, value) in components)
                {
                    var entry = value is JsonArray array ? array.FirstOrDefault() as JsonObject : value as JsonObject;
                    if (entry is null) continue;
                    var javaName = ReadString(entry["version"]?["name"]);
                    if (!TryJavaVersion(javaName, out var version)) continue;
                    var manifestUrl = ReadString(entry["manifest"]?["url"]);
                    if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out _)) continue;
                    choices.Add(new(name, javaName, version, manifestUrl));
                }

                var compatible = choices.Where(choice => requirement.Accepts(choice.Version)).ToArray();
                var recommended = compatible.FirstOrDefault(choice =>
                    string.Equals(choice.Name, requirement.RecommendedComponent, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(choice.JavaName, requirement.RecommendedComponent, StringComparison.OrdinalIgnoreCase));
                return recommended ?? compatible.OrderBy(choice => choice.Version.Major == requirement.Minimum?.Major ? 0 : 1).FirstOrDefault()
                    ?? throw new InvalidOperationException("Mojang 清单中没有满足此实例要求的 Java 运行时，请手动添加兼容的 Java。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; }
        }
        throw new InvalidOperationException("无法获取 Java 下载清单，请检查网络后重试。", lastError);
    }

    private async Task<JavaRuntimeInfo> DownloadRuntimeAsync(
        DownloadComponent component,
        JavaRequirement requirement,
        LauncherSettings settings,
        IProgress<LaunchProgress> progress,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(ResolveRuntimeRoot(settings), component.Name);
        var preserveRootForInspection = false;
        try
        {
            progress.Report(new($"下载 Java {component.Version.Major}", 0));
            var manifestJson = await GetStringWithMirrorAsync(component.ManifestUrl, cancellationToken);
            var manifest = JsonNode.Parse(manifestJson) as JsonObject
                ?? throw new InvalidDataException("Java 文件清单格式无效。");
            if (manifest["files"] is not JsonObject files)
                throw new InvalidDataException("Java 文件清单缺少文件列表。");

            var requests = new List<DownloadFileRequest>();
            foreach (var (relative, value) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = value?["downloads"]?["raw"] as JsonObject;
                var source = ReadString(raw?["url"]);
                if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)) continue;
                var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Java 清单包含不安全的文件路径。");
                var sources = new List<Uri> { sourceUri };
                var mirror = source.Replace("piston-data.mojang.com", "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase);
                if (Uri.TryCreate(mirror, UriKind.Absolute, out var mirrorUri) && !sources.Contains(mirrorUri)) sources.Add(mirrorUri);
                requests.Add(new DownloadFileRequest(path, "Java 运行时",
                    ReadString(raw?["sha1"]), ReadLong(raw?["size"]), sources));
            }
            if (requests.Count == 0) throw new InvalidDataException("Java 清单中没有可下载的运行时文件。");

            Directory.CreateDirectory(root);
            var reuseRoots = _folders.Discover().Select(folder => folder.Path)
                .Append(root)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var options = new DownloadFileOptions(settings.DownloadThreads, settings.DownloadSpeedLimitKb,
                settings.FileDownloadSource, root, reuseRoots);
            var reporter = new Progress<DownloadBatchProgress>(batch =>
                progress.Report(new($"下载 Java {component.Version.Major}", batch.Progress,
                    batch.BytesPerSecond, batch.RemainingFiles)));
            await _downloads.DownloadFilesAsync(requests, options, reporter, cancellationToken);

            var probeFailures = new List<string>();
            var runtime = await FindJavaInFolderAsync(
                root,
                cancellationToken,
                onProbeFailure: (path, reason) => probeFailures.Add($"{path}: {reason}"));
            var requirementText = $"minimum={requirement.Minimum?.ToString() ?? "none"}, maximum={requirement.Maximum?.ToString() ?? "none"}"
                + (requirement.MaximumExclusive ? " (exclusive)" : "");
            if (runtime is null)
            {
                preserveRootForInspection = true;
                Log.Warning(
                    "Downloaded Java runtime could not be probed. Component={Component}, ManifestVersion={ManifestVersion}, Requirement={Requirement}, Root={RuntimeRoot}, ProbeFailures={ProbeFailures}",
                    component.Name, component.JavaName, requirementText, root, string.Join(" | ", probeFailures));
                throw new InvalidDataException(
                    $"已下载 Java {component.JavaName}，但无法识别运行时可执行文件。文件保留在 {root}，详细原因已写入日志。");
            }
            if (!IsCompatible(runtime, requirement))
            {
                preserveRootForInspection = true;
                Log.Warning(
                    "Downloaded Java runtime version is incompatible. Component={Component}, ManifestVersion={ManifestVersion}, DetectedVersion={DetectedVersion}, JavaPath={JavaPath}, Requirement={Requirement}, Root={RuntimeRoot}",
                    component.Name, component.JavaName, runtime.Version, runtime.Path, requirementText, root);
                throw new InvalidDataException(
                    $"下载到 Java {runtime.Version}，但实例要求 {requirementText}。文件保留在 {root}，详细信息已写入日志。");
            }
            var runtimeRoot = Path.GetDirectoryName(root)!;
            var savedSettings = _settings.Load();
            savedSettings.ManagedJavaRuntimeDirectories ??= [];
            if (!savedSettings.ManagedJavaRuntimeDirectories.Contains(runtimeRoot, StringComparer.OrdinalIgnoreCase))
            {
                savedSettings.ManagedJavaRuntimeDirectories.Add(runtimeRoot);
                _settings.Save(savedSettings);
            }
            _runtimeChanges.NotifyChanged();
            progress.Report(new("Java 已准备", 1));
            return runtime;
        }
        catch
        {
            if (preserveRootForInspection)
            {
                Log.Warning("保留 Java 运行时目录供检查：{RuntimeRoot}", root);
            }
            else
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private string ResolveRuntimeRoot(LauncherSettings settings)
    {
        var gameFolder = settings.SelectedGameFolderPath;
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            gameFolder = _folders.Discover().FirstOrDefault()?.Path ?? GameFolderService.LauncherRoot;
        return Path.GetFullPath(Path.Combine(gameFolder, ".97Launch", "runtimes", "java"));
    }

    private static async Task<string> GetStringWithMirrorAsync(string url, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var mirror = url.Replace("piston-meta.mojang.com", "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase);
        foreach (var source in new[] { url, mirror }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { return await Http.GetStringAsync(source, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; }
        }
        throw new HttpRequestException("Java 运行时文件清单下载失败。", lastError);
    }

    private static bool IsCompatible(JavaRuntimeInfo runtime, JavaRequirement requirement)
        => TryJavaVersion(runtime.Version, out var version) && requirement.Accepts(version);

    private static JavaRuntimeInfo? SelectInstalledRuntime(IEnumerable<JavaRuntimeInfo> runtimes, JavaRequirement requirement)
        => runtimes.Where(runtime => IsCompatible(runtime, requirement))
            .OrderBy(runtime => TryJavaVersion(runtime.Version, out var version) && version.Major == requirement.Minimum?.Major ? 0 : 1)
            .FirstOrDefault();

    private static Version? ParseMinecraftVersion(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?:^|\s)(?:1\.)?(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) return null;
        var major = int.Parse(match.Groups[1].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value)
            : match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        return new Version(major, 0, patch);
    }

    private static string ExtractLoaderVersion(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+(?:\.\d+){1,3}(?:[-.][A-Za-z0-9]+)*");
        return match.Success ? match.Value : string.Empty;
    }

    private static bool TryJavaVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<build>\d+))?(?:[._](?<revision>\d+))?");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups["major"].Value);
        var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0;
        var build = match.Groups["build"].Success ? int.Parse(match.Groups["build"].Value) : 0;
        var revision = match.Groups["revision"].Success ? int.Parse(match.Groups["revision"].Value) : 0;
        if (major == 1 && minor > 0) { major = minor; minor = build; build = revision; revision = 0; }
        version = new Version(major, minor, build, revision);
        return true;
    }

    private static bool TryVersion(string value, out Version version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        return Version.TryParse(match.Value, out version!);
    }

    private static bool AtLeast(string value, string minimum)
        => TryVersion(value, out var parsed) && TryVersion(minimum, out var bound) && parsed >= bound;

    private static bool LessThan(string value, string maximum)
        => TryVersion(value, out var parsed) && TryVersion(maximum, out var bound) && parsed < bound;

    private static bool InVersionRange(string value, string minimum, string maximum)
        => AtLeast(value, minimum) && TryVersion(value, out var parsed) && TryVersion(maximum, out var bound) && parsed <= bound;

    private static DateTime? TryDate(string? value)
        => DateTime.TryParse(value, out var date) ? date : null;

    private static int ReadInt(JsonNode? node)
    {
        try { return node?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private static long? ReadLong(JsonNode? node)
    {
        try { return node?.GetValue<long>(); }
        catch { return null; }
    }

    private static string ReadString(JsonNode? node)
    {
        try { return node?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private sealed record DownloadComponent(string Name, string JavaName, Version Version, string ManifestUrl);

    private sealed class JavaRequirement
    {
        public Version? Minimum { get; private set; }
        public Version? Maximum { get; private set; }
        public bool MaximumExclusive { get; private set; }
        public string? RecommendedComponent { get; set; }
        public bool IsEmpty => Minimum is { } min && Maximum is { } max
            && (min > max || min == max && MaximumExclusive);

        public string Recommendation
        {
            get
            {
                var major = Minimum?.Major ?? Math.Min(8,
                    Maximum?.Major - (MaximumExclusive ? 1 : 0) ?? 8);
                var limits = new List<string>();
                if (Minimum is { } min) limits.Add($"最低 Java {DisplayVersion(min)}");
                if (Maximum is { } max) limits.Add(MaximumExclusive
                    ? $"低于 Java {DisplayVersion(max)}" : $"最高 Java {DisplayVersion(max)}");
                return $"推荐使用 Java {major}" + (limits.Count == 0 ? "" : $"（{string.Join("，", limits)}）");
            }
        }

        private static string DisplayVersion(Version version)
            => version.Minor == 0 && version.Build <= 0 && version.Revision <= 0
                ? version.Major.ToString() : version.ToString();

        public bool Accepts(Version version)
            => !IsEmpty && (Minimum is null || version >= Minimum)
                && (Maximum is null || (MaximumExclusive ? version < Maximum : version <= Maximum));

        public void AddMinimum(Version value)
        {
            if (Minimum is null || value > Minimum) Minimum = value;
            if (IsEmpty)
            {
                Minimum = value;
                Maximum = null;
                MaximumExclusive = false;
            }
        }

        public void AddMaximum(Version value, bool exclusive = false)
        {
            if (Maximum is null || value < Maximum || value == Maximum && exclusive)
            {
                Maximum = value;
                MaximumExclusive = exclusive;
            }
            if (IsEmpty)
            {
                Minimum = null;
                Maximum = value;
                MaximumExclusive = exclusive;
            }
        }
    }
}
