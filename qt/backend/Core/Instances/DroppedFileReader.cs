using System.IO.Compression;
using System.Text.Json;
using Launcher.Core.Downloads;

namespace Launcher.Core.Instances;

public sealed record DroppedFile(string Path, string Name, LocalContent? Mod = null)
{
    public bool IsModpack => Mod is null;
}

public static class DroppedFileReader
{
    public static DroppedFile Read(string path, string iconCache)
    {
        if (!File.Exists(path)) throw new InvalidDataException("请拖入 Mod 文件或整合包压缩文件。");
        var name = LocalContentReader.EnabledName(Path.GetFileName(path));
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".jar" or ".zip" or ".litemod" or ".mrpack"))
            throw new InvalidDataException("不支持此文件类型。");
        using var archive = ZipFile.OpenRead(path);
        var index = archive.GetEntry("modrinth.index.json");
        var manifest = archive.GetEntry("manifest.json");
        if (index is not null)
        {
            ModrinthPackReader.Read(path, Path.Combine(Path.GetTempPath(), "97launch-drop-validation"));
            return new(path, PackName(index, Path.GetFileNameWithoutExtension(name)));
        }
        if (manifest is not null)
        {
            CurseForgePackReader.Read(path);
            return new(path, PackName(manifest, Path.GetFileNameWithoutExtension(name)));
        }
        if (extension == ".zip" && archive.Entries.Count(e => e.FullName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) == 1)
            return new(path, Path.GetFileNameWithoutExtension(name));
        if (extension == ".mrpack") throw new InvalidDataException("整合包缺少有效的清单。");
        if (!new[] { "fabric.mod.json", "quilt.mod.json", "mcmod.info", "litemod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml" }
                .Any(entry => archive.GetEntry(entry) is not null))
            throw new InvalidDataException("未找到 Mod 或整合包信息。");
        var mod = LocalContentReader.ReadModFile(path, iconCache);
        if (string.IsNullOrWhiteSpace(mod.ModId)) throw new InvalidDataException("Mod 信息不完整，无法识别。");
        return new(path, mod.Name, mod);
    }

    private static string PackName(ZipArchiveEntry entry, string fallback)
    {
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包清单过大。");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString()! : fallback;
    }
}
