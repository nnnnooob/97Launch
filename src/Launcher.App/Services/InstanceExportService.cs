using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Core.Instances;

namespace Launcher.App.Services;

public sealed record InstanceExportOptions
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Summary { get; set; } = "";
    public bool Mods { get; set; } = true;
    public bool DisabledMods { get; set; }
    public bool Options { get; set; } = true;
    public bool Configs { get; set; } = true;
    public bool Scripts { get; set; } = true;
    public bool ResourcePacks { get; set; } = true;
    public bool ShaderPacks { get; set; } = true;
    public bool Saves { get; set; }
    public bool Screenshots { get; set; }
    public bool Servers { get; set; }
    public bool Personal { get; set; }
    public bool DirectFiles { get; set; }
    public bool OnlyModrinth { get; set; }
    public bool IncludeLauncher { get; set; }
    public string? JavaDirectory { get; set; }
    public string Rules { get; set; } = "";
}

public sealed class InstanceExportService(InstanceContentService content, InstanceModificationService modification)
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"97Launch/{AppInfo.Version.TrimStart('v')} (modpack-export)");
        return http;
    }

    public async Task ExportAsync(InstanceContext context, InstanceExportOptions options, string destination,
        Func<string, Task<bool>> confirmDirect, IProgress<InstallProgress> progress, CancellationToken token)
    {
        using var operation = await content.AcquireAsync(context, token);
        if (options.IncludeLauncher && Path.GetExtension(destination).Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("携带启动器时请选择 ZIP 格式。");
        if (destination.StartsWith(context.GameDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(AppInfo.ApplicationDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请把导出文件保存在游戏目录和启动器程序目录以外。");
        var manifest = await modification.ReadInstalledAsync(context, token);
        progress.Report(new(0, 0, "整理导出内容"));
        var files = await Task.Run(() => EnumerateFiles(context.GameDirectory, options, token).ToArray(), token);
        var hosted = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var candidates = files.Where(f => Path.GetExtension(LocalContentReader.EnabledName(f)).ToLowerInvariant() is ".jar" or ".zip" or ".litemod").ToArray();
        if (!options.DirectFiles && candidates.Length > 0)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < candidates.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                hashes[candidates[i]] = await InstanceModificationService.HashAsync(candidates[i], token);
                progress.Report(new(0, (i + 1d) / candidates.Length * .65, "识别资源来源"));
            }
            var failed = false;
            try
            {
                foreach (var batch in hashes.Values.Distinct().Chunk(150))
                {
                    using var response = await Http.PostAsJsonAsync("https://api.modrinth.com/v2/version_files", new { hashes = batch, algorithm = "sha1" }, token);
                    response.EnsureSuccessStatusCode();
                    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                    foreach (var pair in hashes.Where(p => batch.Contains(p.Value)))
                    {
                        if (!document.RootElement.TryGetProperty(pair.Value, out var version)) continue;
                        var file = version.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("hashes").GetProperty("sha1").GetString() == pair.Value);
                        if (file.ValueKind != JsonValueKind.Object || !Uri.TryCreate(file.GetProperty("url").GetString(), UriKind.Absolute, out var url)
                            || url.Scheme != "https" || url.Host != "cdn.modrinth.com") continue;
                        await using var input = File.OpenRead(pair.Key);
                        hosted[pair.Key] = new JsonObject
                        {
                            ["path"] = Path.GetRelativePath(context.GameDirectory, pair.Key).Replace('\\', '/'),
                            ["hashes"] = new JsonObject { ["sha1"] = pair.Value, ["sha512"] = Convert.ToHexString(await SHA512.HashDataAsync(input, token)).ToLowerInvariant() },
                            ["downloads"] = new JsonArray(url.AbsoluteUri), ["fileSize"] = new FileInfo(pair.Key).Length,
                            ["env"] = new JsonObject { ["client"] = "required", ["server"] = "optional" }
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested) { failed = true; }
            if (failed && !await confirmDirect("联网识别部分资源失败。是否继续导出，并将未识别的资源文件直接打包？"))
                throw new OperationCanceledException(token);
            if (options.OnlyModrinth && candidates.Any(f => !hosted.ContainsKey(f)))
                throw new InvalidOperationException("有资源无法在 Modrinth 识别。请关闭“仅使用 Modrinth 托管资源”，或移除这些资源后再导出。");
        }
        var dependencies = new JsonObject { ["minecraft"] = manifest.MinecraftVersion };
        if (manifest.Loader is not null)
            dependencies[manifest.Loader switch { "Fabric" => "fabric-loader", "Forge" => "forge", "NeoForge" => "neoforge", _ => throw new NotSupportedException("此加载器暂不支持标准整合包导出。") }] = manifest.LoaderVersion;
        var index = new JsonObject
        {
            ["game"] = "minecraft", ["formatVersion"] = 1, ["name"] = options.Name, ["versionId"] = options.Version,
            ["summary"] = options.Summary, ["dependencies"] = dependencies,
            ["files"] = new JsonArray(hosted.Values.Select(v => (JsonNode)v).ToArray()),
        };
        var temp = destination + ".97launch-" + Guid.NewGuid().ToString("N") + ".tmp";
        var packTemp = options.IncludeLauncher ? Path.Combine(Path.GetDirectoryName(destination)!, ".97launch-pack-" + Guid.NewGuid().ToString("N") + ".mrpack") : temp;
        try
        {
            await Task.Run(async () =>
            {
                using (var archive = ZipFile.Open(packTemp, ZipArchiveMode.Create))
                {
                    await WriteJson(archive, "modrinth.index.json", index, token);
                    await WriteJson(archive, "97launch.components.json", manifest, token);
                    var included = files.Where(f => !hosted.ContainsKey(f)).ToArray();
                    for (var i = 0; i < included.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        await AddFile(archive, included[i], "overrides/" + Path.GetRelativePath(context.GameDirectory, included[i]).Replace('\\', '/'), token);
                        progress.Report(new(1, (i + 1d) / Math.Max(1, included.Length), "打包实例内容"));
                    }
                    if (options.JavaDirectory is { } java)
                    {
                        if (!File.Exists(Path.Combine(java, "bin", "java.exe"))) throw new IOException("所选 Java 文件夹缺少 bin/java.exe。");
                        foreach (var file in InstanceContentService.Files(java)) await AddFile(archive, file, "overrides/java/" + Path.GetRelativePath(java, file).Replace('\\', '/'), token);
                    }
                }
                if (options.IncludeLauncher)
                {
                    using var outer = ZipFile.Open(temp, ZipArchiveMode.Create);
                    await AddFile(outer, packTemp, options.Name + ".mrpack", token);
                    var launcher = Environment.ProcessPath ?? throw new IOException("无法定位启动器程序。");
                    await AddFile(outer, launcher, "97Launch/97Launch.exe", token);
                }
            }, token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
            progress.Report(new(1, 1, "导出完成"));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (packTemp != temp && File.Exists(packTemp)) File.Delete(packTemp); }
    }

    private static bool Include(string relative, InstanceExportOptions options)
    {
        var parts = relative.Split('/');
        if (parts.Any(p => p.StartsWith(".") || p.Equals("97Launch", StringComparison.OrdinalIgnoreCase) || p.Equals("PCL", StringComparison.OrdinalIgnoreCase))) return false;
        if (relative.Contains("account", StringComparison.OrdinalIgnoreCase) || relative.Contains("token", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || relative.Contains("/cache/", StringComparison.OrdinalIgnoreCase)) return false;
        var top = parts[0].ToLowerInvariant();
        if (top is "logs" or "crash-reports" or "libraries" or "assets" or "versions" or "natives" or "backups" or "java") return false;
        var include = top switch
        {
            "mods" or "coremods" => options.Mods && (options.DisabledMods || LocalContentReader.EnabledName(relative) == relative),
            "config" or "defaultconfigs" => options.Configs,
            "kubejs" or "scripts" or "resources" or "datapacks" or "openloader" or "global_packs" or "global_resource_packs" or "global_data_packs" => options.Scripts,
            "resourcepacks" => options.ResourcePacks, "shaderpacks" => options.ShaderPacks,
            "saves" => options.Saves, "screenshots" => options.Screenshots, "servers.dat" => options.Servers,
            "options.txt" or "optionsof.txt" or "optionsshaders.txt" or "iris.properties" => options.Options,
            "hotbar.nbt" or "command_history.txt" => options.Personal,
            _ => top.StartsWith("licen", StringComparison.OrdinalIgnoreCase),
        };
        foreach (var rule in options.Rules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exclude = rule.StartsWith('!'); var glob = rule.TrimStart('!').Replace('\\', '/');
            if (glob.StartsWith('/') || glob.Split('/').Any(p => p is "." or "..")) throw new InvalidDataException("导出规则必须是相对游戏目录的路径。");
            var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + (glob.EndsWith('/') ? ".*" : "") + "$";
            if (Regex.IsMatch(relative, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) include = !exclude;
        }
        return include;
    }

    private static IEnumerable<string> EnumerateFiles(string root, InstanceExportOptions options, CancellationToken token)
    {
        IEnumerable<string> Walk(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var parts = relative.Split('/');
                if (parts.Any(p => p.StartsWith('.') || p is "97Launch" or "PCL" or "libraries" or "assets" or "versions" or "natives" or "logs" or "crash-reports" or "backups" or "java")) continue;
                if (Directory.Exists(path))
                {
                    var custom = options.Rules.Split('\n').Any(r => !r.TrimStart().StartsWith('!') && r.TrimStart().StartsWith(parts[0] + "/", StringComparison.OrdinalIgnoreCase));
                    if (!Include(parts[0] + "/", options) && !custom) continue;
                    foreach (var file in Walk(path)) yield return file;
                }
                else if (Include(relative, options)) yield return path;
            }
        }
        return Walk(root);
    }

    private static async Task WriteJson(ZipArchive archive, string name, object value, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(name).Open();
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token);
    }

    private static async Task AddFile(ZipArchive archive, string source, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var input = File.OpenRead(source);
        await using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        await input.CopyToAsync(output, token);
    }
}
