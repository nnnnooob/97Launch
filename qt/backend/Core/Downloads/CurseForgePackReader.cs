using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Core.Downloads;

public sealed record CurseForgePackFile(string ProjectId, string FileId);
public sealed record CurseForgePackPlan(string MinecraftVersion, string? Loader, string? LoaderVersion,
    string Overrides, IReadOnlyList<CurseForgePackFile> Files);

public static class CurseForgePackReader
{
    public static CurseForgePackPlan Read(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifest = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("整合包缺少 manifest.json。");
        if (manifest.Length > 8 * 1024 * 1024) throw new InvalidDataException("整合包清单过大。");
        using var stream = manifest.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.GetProperty("manifestType").GetString() != "minecraftModpack" || root.GetProperty("manifestVersion").GetInt32() != 1)
            throw new InvalidDataException("不支持此 CurseForge 整合包格式。");
        var minecraft = root.GetProperty("minecraft");
        var game = minecraft.GetProperty("version").GetString() ?? throw new InvalidDataException("整合包缺少 Minecraft 版本。");
        var loaders = minecraft.GetProperty("modLoaders").EnumerateArray().ToArray();
        var selected = loaders.FirstOrDefault(loader => loader.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
        if (selected.ValueKind == JsonValueKind.Undefined && loaders.Length > 0) selected = loaders[0];
        string? loaderName = null, loaderVersion = null;
        if (selected.ValueKind != JsonValueKind.Undefined)
        {
            var id = selected.GetProperty("id").GetString() ?? "";
            var separator = id.IndexOf('-');
            if (separator <= 0 || separator == id.Length - 1) throw new InvalidDataException("整合包加载器信息无效。");
            loaderName = id[..separator].ToLowerInvariant() switch { "forge" => "Forge", "neoforge" => "NeoForge", "fabric" => "Fabric", _ => throw new NotSupportedException("整合包使用了暂不支持的加载器。") };
            loaderVersion = id[(separator + 1)..];
        }
        var overrides = root.TryGetProperty("overrides", out var field) ? field.GetString() ?? "overrides" : "overrides";
        ModrinthPackReader.ResolvePath(Path.Combine(Path.GetTempPath(), "97launch-pack-validation"), overrides);
        var files = root.GetProperty("files").EnumerateArray().Select(file => new CurseForgePackFile(
            file.GetProperty("projectID").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            file.GetProperty("fileID").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture))).Distinct().ToArray();
        return new(game, loaderName, loaderVersion, overrides.Replace('\\', '/').TrimEnd('/') + "/", files);
    }

    public static async Task ExtractOverridesAsync(string archivePath, string instanceDirectory, string prefix, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        long total = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && !entry.FullName.EndsWith('/')))
        {
            token.ThrowIfCancellationRequested();
            total += entry.Length;
            if (total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("整合包覆盖文件总大小超过支持范围。");
            var path = ModrinthPackReader.ResolvePath(instanceDirectory, entry.FullName[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }
}
