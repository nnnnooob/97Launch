using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Core.Downloads;

public sealed record ModrinthPackPlan(string MinecraftVersion, string? Loader, string? LoaderVersion,
    IReadOnlyList<DownloadFileRequest> Files);

/// <summary>Reads client files and overrides from the published .mrpack format.</summary>
public static class ModrinthPackReader
{
    public static ModrinthPackPlan Read(string archivePath, string instanceDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("整合包缺少 modrinth.index.json。");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包清单过大。");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1 || root.GetProperty("game").GetString() != "minecraft")
            throw new InvalidDataException("不支持的 Modrinth 整合包格式。");
        var dependencies = root.GetProperty("dependencies");
        var game = dependencies.GetProperty("minecraft").GetString();
        if (string.IsNullOrWhiteSpace(game)) throw new InvalidDataException("整合包没有指定 Minecraft 版本。");
        string? loader = null, loaderVersion = null;
        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (dependency.Name == "minecraft") continue;
            var supported = dependency.Name switch
            {
                "fabric-loader" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge",
                _ => throw new NotSupportedException($"当前不支持整合包所需的组件：{dependency.Name}，可以先另存为整合包文件。"),
            };
            if (loader is not null) throw new InvalidDataException("整合包包含多个互斥的 Mod 加载器。");
            loader = supported;
            loaderVersion = dependency.Value.GetString();
        }
        var files = new List<DownloadFileRequest>();
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            if (file.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client) && client.GetString() == "unsupported") continue;
            var path = ResolvePath(instanceDirectory, file.GetProperty("path").GetString() ?? "");
            var sources = file.GetProperty("downloads").EnumerateArray().Select(source =>
            {
                if (!Uri.TryCreate(source.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || !new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" }
                        .Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("整合包包含不受支持的下载地址。");
                return uri;
            }).ToArray();
            var sha1 = file.GetProperty("hashes").GetProperty("sha1").GetString();
            if (sha1 is null || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit) || sources.Length == 0)
                throw new InvalidDataException("整合包文件缺少有效的校验信息或下载地址。");
            files.Add(new(path, "整合包组件", sha1, file.GetProperty("fileSize").GetInt64(), sources));
        }
        if (files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("整合包中存在重复的文件路径。");
        return new(game, loader, loaderVersion, files);
    }

    public static async Task ExtractClientOverridesAsync(string archivePath, string instanceDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        long total = 0;
        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            total += entry.Length;
            if (total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("整合包覆盖文件总大小超过支持范围。");
            var path = ResolvePath(instanceDirectory, entry.FullName[prefix.Length..]);
            var name = Path.GetFileName(instanceDirectory);
            if (path.Equals(Path.Combine(instanceDirectory, name + ".json"), StringComparison.OrdinalIgnoreCase)
                || path.Equals(Path.Combine(instanceDirectory, name + ".jar"), StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(Path.Combine(instanceDirectory, "97Launch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("整合包覆盖文件不能替换启动器的实例元数据。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    public static string ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(part => part is "." or ".." || part.Length == 0
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("整合包包含无效的相对文件路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包文件不能写入实例目录以外的位置。");
        var instanceName = Path.GetFileName(Path.GetFullPath(root));
        if (relative.Equals(".97launch-installing", StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(Path.Combine(root, instanceName + ".json"), StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(Path.Combine(root, instanceName + ".jar"), StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(Path.Combine(Path.GetFullPath(root), "97Launch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包不能替换启动器的实例元数据。");
        return fullPath;
    }
}
