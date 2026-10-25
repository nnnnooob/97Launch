using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core.Instances;

public enum ContentKind { Mods, Saves, Screenshots, ResourcePacks, ShaderPacks }

public sealed record LocalContent(
    string Path, string Name, string FileName, string Version, string Description,
    string? IconPath, long Size, DateTime Modified, bool Enabled = true,
    string? ModId = null, string? Error = null, int? PackFormat = null,
    IReadOnlyList<string>? RequiredMods = null, Version? MinimumFormat = null, Version? MaximumFormat = null,
    IReadOnlyList<string>? ModLoaders = null, IReadOnlyList<string>? GameRequirements = null,
    IReadOnlyList<string>? ProvidedMods = null, IReadOnlyDictionary<string, string>? ModRequirements = null);

public static class LocalContentReader
{
    public static string Folder(ContentKind kind) => kind switch
    {
        ContentKind.Mods => "mods", ContentKind.Saves => "saves", ContentKind.Screenshots => "screenshots",
        ContentKind.ResourcePacks => "resourcepacks", _ => "shaderpacks"
    };

    public static IReadOnlyList<LocalContent> Scan(string directory, ContentKind kind, string gameVersion,
        bool legacyForge, string cache, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];
        var paths = Directory.EnumerateFileSystemEntries(directory).ToList();
        if (kind == ContentKind.Mods && legacyForge && Directory.Exists(System.IO.Path.Combine(directory, gameVersion)))
            paths.AddRange(Directory.EnumerateFiles(System.IO.Path.Combine(directory, gameVersion)));
        var entries = new List<LocalContent>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            var isDirectory = Directory.Exists(path);
            var filename = System.IO.Path.GetFileName(path);
            if (filename.StartsWith(".97launch", StringComparison.OrdinalIgnoreCase)) continue;
            var enabledName = EnabledName(filename);
            var extension = System.IO.Path.GetExtension(enabledName).ToLowerInvariant();
            if (kind == ContentKind.Mods && (isDirectory || extension is not (".jar" or ".zip" or ".litemod"))) continue;
            if (kind == ContentKind.Saves && !isDirectory) continue;
            if (kind == ContentKind.Screenshots && (isDirectory || extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))) continue;
            if (kind is ContentKind.ResourcePacks or ContentKind.ShaderPacks && !isDirectory && extension != ".zip") continue;
            var size = isDirectory ? 0 : new FileInfo(path).Length;
            var modified = isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
            var entry = new LocalContent(path, System.IO.Path.GetFileNameWithoutExtension(enabledName), filename,
                "", "", null, size, modified, enabledName == filename);
            try
            {
                if (kind == ContentKind.Saves)
                {
                    var level = System.IO.Path.Combine(path, "level.dat");
                    if (!File.Exists(level))
                    {
                        if (!File.Exists(System.IO.Path.Combine(path, "level.dat_old"))) continue;
                        level = System.IO.Path.Combine(path, "level.dat_old");
                    }
                    var world = WorldMetadata.Read(level);
                    var icon = System.IO.Path.Combine(path, "icon.png");
                    entry = entry with { Name = world.Name.Length > 0 ? world.Name : filename, Version = world.Version,
                        Modified = world.LastPlayed ?? modified, Description = world.Mode,
                        IconPath = File.Exists(icon) ? icon : null };
                }
                else if (kind == ContentKind.Screenshots)
                {
                    entry = entry with { Name = filename, IconPath = path, Description = ImageDimensions(path) };
                }
                else if (isDirectory)
                {
                    var meta = System.IO.Path.Combine(path, "pack.mcmeta");
                    if (kind == ContentKind.ResourcePacks && !File.Exists(meta)) continue;
                    if (kind == ContentKind.ShaderPacks && !Directory.Exists(System.IO.Path.Combine(path, "shaders"))) continue;
                    if (File.Exists(meta)) entry = ReadPack(entry, File.ReadAllText(meta));
                    var icon = System.IO.Path.Combine(path, "pack.png");
                    entry = entry with { Name = filename, IconPath = File.Exists(icon) ? icon : null };
                }
                else
                {
                    using var archive = ZipFile.OpenRead(path);
                    if (kind == ContentKind.Mods)
                    {
                        entry = ReadMod(archive, entry, cache);
                        var provided = new HashSet<string>(entry.ProvidedMods ?? [], StringComparer.OrdinalIgnoreCase);
                        long nestedBytes = 0;
                        foreach (var bundled in archive.Entries.Where(e => e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                                     && e.Length <= 32 * 1024 * 1024).Take(64))
                        {
                            nestedBytes += bundled.Length;
                            if (nestedBytes > 64 * 1024 * 1024) break;
                            try
                            {
                                using var input = bundled.Open(); using var nested = new ZipArchive(input, ZipArchiveMode.Read);
                                var json = nested.GetEntry("fabric.mod.json") ?? nested.GetEntry("quilt.mod.json");
                                if (json is not null)
                                {
                                    using var metadata = JsonDocument.Parse(ReadText(json));
                                    var root = metadata.RootElement;
                                    if (root.TryGetProperty("quilt_loader", out var quilt)) root = quilt;
                                    var id = String(root, "id");
                                    if (id.Length > 0) provided.Add(id);
                                }
                                var toml = nested.GetEntry("META-INF/neoforge.mods.toml") ?? nested.GetEntry("META-INF/mods.toml");
                                if (toml is not null) foreach (var id in TomlModIds(ReadText(toml))) provided.Add(id);
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
                        }
                        entry = entry with { ProvidedMods = provided.ToArray() };
                        var loaders = (entry.ModLoaders ?? []).ToList();
                        if (archive.GetEntry("META-INF/neoforge.mods.toml") is not null) loaders.Add("neoforge");
                        if (archive.GetEntry("META-INF/mods.toml") is not null) loaders.AddRange(["forge", "neoforge"]);
                        entry = entry with { ModLoaders = loaders.Distinct().ToArray() };
                    }
                    else
                    {
                        if (kind == ContentKind.ShaderPacks && !archive.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.Ordinal))) continue;
                        var meta = archive.GetEntry("pack.mcmeta");
                        if (kind == ContentKind.ResourcePacks && meta is null) continue;
                        if (meta is not null) entry = ReadPack(entry, ReadText(meta));
                        entry = entry with { IconPath = CacheIcon(archive.GetEntry("pack.png"), entry, cache) };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or FormatException or UnauthorizedAccessException or OverflowException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            {
                entry = entry with { Error = "读取失败：" + ex.Message };
            }
            entries.Add(entry);
        }
        return kind is ContentKind.Screenshots or ContentKind.Saves
            ? entries.OrderByDescending(e => e.Modified).ToArray()
            : entries.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string EnabledName(string name)
    {
        while (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            name = System.IO.Path.GetFileNameWithoutExtension(name);
        return name;
    }

    private static LocalContent ReadPack(LocalContent entry, string text)
    {
        using var doc = JsonDocument.Parse(text);
        var pack = doc.RootElement.GetProperty("pack");
        var description = pack.TryGetProperty("description", out var d) ? PlainText(d) : "";
        var declared = pack.TryGetProperty("pack_format", out var f) ? FormatVersion(f) : null;
        var minimum = pack.TryGetProperty("min_format", out var min) ? FormatVersion(min) : declared;
        var maximum = pack.TryGetProperty("max_format", out var max) ? FormatVersion(max) : declared;
        if (pack.TryGetProperty("supported_formats", out var supported))
        {
            if (supported.ValueKind == JsonValueKind.Array && supported.GetArrayLength() == 2)
            { minimum = FormatVersion(supported[0]); maximum = FormatVersion(supported[1]); }
            else if (supported.ValueKind == JsonValueKind.Object)
            {
                if (supported.TryGetProperty("min_inclusive", out var low)) minimum = FormatVersion(low);
                if (supported.TryGetProperty("max_inclusive", out var high)) maximum = FormatVersion(high);
            }
            else minimum = maximum = FormatVersion(supported);
        }
        return entry with { Description = description, PackFormat = declared?.Major, Version = declared is null ? "" : $"格式 {declared}", MinimumFormat = minimum, MaximumFormat = maximum };
    }

    private static Version? FormatVersion(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2
            && value[0].TryGetInt32(out var major) && value[1].TryGetInt32(out var minor)) return new Version(major, minor);
        if (value.ValueKind != JsonValueKind.Number) return null;
        var parts = value.GetRawText().Split('.');
        return int.TryParse(parts[0], out var a) && a >= 0 ? new Version(a, parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0) : null;
    }

    public static LocalContent ReadModFile(string path, string cache)
    {
        var info = new FileInfo(path);
        using var archive = ZipFile.OpenRead(path);
        return ReadMod(archive, new LocalContent(path, Path.GetFileNameWithoutExtension(EnabledName(info.Name)),
            info.Name, "", "", null, info.Length, info.LastWriteTime, EnabledName(info.Name) == info.Name), cache);
    }

    private static LocalContent ReadMod(ZipArchive archive, LocalContent entry, string cache)
    {
        var metadata = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json")
            ?? archive.GetEntry("mcmod.info") ?? archive.GetEntry("litemod.json");
        if (metadata is not null)
        {
            using var doc = ParseMetadata(ReadText(metadata));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().FirstOrDefault();
            if (root.ValueKind != JsonValueKind.Object) return entry;
            if (root.TryGetProperty("modList", out var list) && list.ValueKind == JsonValueKind.Array) root = list.EnumerateArray().FirstOrDefault();
            if (root.TryGetProperty("quilt_loader", out var quilt))
            {
                var details = quilt.TryGetProperty("metadata", out var qmeta) ? qmeta : quilt;
                entry = entry with { Name = String(details, "name", entry.Name), ModId = String(quilt, "id"),
                    Version = String(quilt, "version"), Description = String(details, "description"), ModLoaders = ["quilt"] };
                if (quilt.TryGetProperty("depends", out var qdeps) && qdeps.ValueKind == JsonValueKind.Array)
                {
                    var quiltRequired = qdeps.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object
                        && (!d.TryGetProperty("optional", out var optional) || optional.ValueKind != JsonValueKind.True))
                        .Where(d => String(d, "id").Length > 0).ToArray();
                    entry = entry with { RequiredMods = quiltRequired.Select(d => String(d, "id")).Where(id => !ModDiagnostics.IsBuiltin(id)).ToArray(),
                        GameRequirements = quiltRequired.Where(d => String(d, "id") == "minecraft").SelectMany(d => Requirements(d, "versions")).ToArray() };
                }
                var icon = String(details, "icon");
                return entry with { IconPath = CacheIcon(archive.GetEntry(icon), entry, cache) };
            }
            var required = root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object
                ? depends.EnumerateObject().Where(p => p.Name is not ("minecraft" or "java" or "fabricloader")).Select(p => p.Name).ToArray() : [];
            entry = entry with { Name = String(root, "name", entry.Name), ModId = String(root, "id", String(root, "modid")),
                Version = String(root, "version"), Description = String(root, "description"), RequiredMods = required,
                ModLoaders = metadata.FullName == "fabric.mod.json" ? ["fabric", "quilt"] : metadata.FullName == "litemod.json" ? ["liteloader"] : ["forge"],
                GameRequirements = depends.ValueKind == JsonValueKind.Object ? Requirements(depends, "minecraft") : [],
                ModRequirements = depends.ValueKind == JsonValueKind.Object ? depends.EnumerateObject()
                    .Where(p => !ModDiagnostics.IsBuiltin(p.Name) && p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "") : null,
                ProvidedMods = root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array
                    ? provides.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray() : [] };
            var iconPath = String(root, "icon", String(root, "logoFile"));
            return entry with { IconPath = CacheIcon(archive.GetEntry(iconPath), entry, cache) };
        }
        metadata = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
        if (metadata is not null)
        {
            var text = ReadText(metadata);
            string Field(string key, string fallback = "")
            {
                var match = Regex.Match(text, @"(?m)^\s*" + key + "\\s*=\\s*[\"']([^\"'\\r\\n]*)[\"']", RegexOptions.CultureInvariant);
                return match.Success ? match.Groups[1].Value : fallback;
            }
            var version = Field("version");
            if (version.Contains("${"))
            {
                var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
                version = manifest is null ? "" : Regex.Match(ReadText(manifest), @"(?m)^Implementation-Version:\s*(.+)$").Groups[1].Value.Trim();
            }
            entry = entry with { Name = Field("displayName", entry.Name), ModId = Field("modId"), Version = version,
                Description = Field("description"), ModLoaders = metadata.FullName.Contains("neoforge") ? ["neoforge"] : ["forge", "neoforge"] };
            var required = new List<string>(); var versions = new Dictionary<string, string>(); var games = new List<string>();
            foreach (Match section in Regex.Matches(text, @"(?ms)^\s*\[\[dependencies\.[^\]]+\]\](.*?)(?=^\s*\[|\z)"))
            {
                var block = section.Groups[1].Value;
                string Value(string key) => Regex.Match(block, @"(?m)^\s*" + key + "\\s*=\\s*[\"']([^\"'\\r\\n]*)[\"']").Groups[1].Value;
                if (!Regex.IsMatch(block, @"(?m)^\s*mandatory\s*=\s*true") && Value("type") != "required") continue;
                if (Value("side") == "SERVER") continue;
                var id = Value("modId"); var range = Value("versionRange");
                if (id == "minecraft") games.Add(range);
                else if (id.Length > 0 && !ModDiagnostics.IsBuiltin(id)) { required.Add(id); versions[id] = range; }
            }
            entry = entry with { RequiredMods = required, ModRequirements = versions, GameRequirements = games,
                ProvidedMods = TomlModIds(text).Where(id => id != entry.ModId).ToArray() };
            return entry with { IconPath = CacheIcon(archive.GetEntry(Field("logoFile")), entry, cache) };
        }
        return entry;
    }

    private static string[] Requirements(JsonElement root, string key) => !root.TryGetProperty(key, out var value) ? []
        : value.ValueKind == JsonValueKind.String ? [value.GetString() ?? ""]
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToArray() : [];

    private static IEnumerable<string> TomlModIds(string text) => Regex.Matches(text, @"(?ms)^\s*\[\[mods\]\](.*?)(?=^\s*\[|\z)")
        .Select(section => Regex.Match(section.Groups[1].Value, "(?m)^\\s*modId\\s*=\\s*[\"']([^\"'\\r\\n]*)[\"']").Groups[1].Value).Where(id => id.Length > 0);

    private static string String(JsonElement element, string key, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : PlainText(value) : fallback;

    private static string PlainText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(PlainText)),
        JsonValueKind.Object => (value.TryGetProperty("text", out var t) ? PlainText(t) : "")
            + (value.TryGetProperty("extra", out var e) ? PlainText(e) : ""),
        _ => ""
    };

    private static JsonDocument ParseMetadata(string text)
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        try { return JsonDocument.Parse(text, options); }
        catch (JsonException)
        {
            // Some mods ship Gson's lenient string syntax. Normalize control characters in strings only.
            var clean = new StringBuilder(text.Length);
            bool quoted = false, escaped = false, lineComment = false, blockComment = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (lineComment) { clean.Append(c); if (c is '\r' or '\n') lineComment = false; continue; }
                if (blockComment) { clean.Append(c); if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { clean.Append(text[++i]); blockComment = false; } continue; }
                if (!quoted && c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
                { lineComment = text[i + 1] == '/'; blockComment = !lineComment; clean.Append(c).Append(text[++i]); continue; }
                if (quoted && c < 32)
                {
                    if (escaped) { clean.Length--; escaped = false; continue; }
                    clean.Append("\\u").Append(((int)c).ToString("x4")); continue;
                }
                clean.Append(c);
                if (escaped) escaped = false;
                else if (quoted && c == '\\') escaped = true;
                else if (c == '"') quoted = !quoted;
            }
            return JsonDocument.Parse(clean.ToString(), options);
        }
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException("资源描述文件过大。");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string? CacheIcon(ZipArchiveEntry? icon, LocalContent entry, string cache)
    {
        if (icon is null || icon.Length is <= 0 or > 4 * 1024 * 1024 || !icon.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Path + entry.Modified.Ticks + icon.FullName)));
        Directory.CreateDirectory(cache);
        var destination = System.IO.Path.Combine(cache, key + ".png");
        if (!File.Exists(destination))
        {
            using var input = icon.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
        }
        return destination;
    }

    private static string ImageDimensions(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (file.Read(header) >= 24 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return $"{BinaryPrimitives.ReadInt32BigEndian(header[16..20])} × {BinaryPrimitives.ReadInt32BigEndian(header[20..24])}";
        return System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
    }
}

public sealed record WorldMetadata(string Name, string Version, string Mode, DateTime? LastPlayed)
{
    public static WorldMetadata Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (limited.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("存档信息超过支持范围。");
            limited.Write(buffer, 0, read);
        }
        limited.Position = 0;
        using var reader = new BinaryReader(limited, Encoding.UTF8);
        var values = new Dictionary<string, object>();
        long Long() => BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8));
        int Int() => BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
        string Text()
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }
        void Skip(long bytes)
        {
            if (bytes < 0 || bytes > limited.Length - limited.Position) throw new InvalidDataException("存档数据长度无效。");
            limited.Position += bytes;
        }
        void Tag(byte type, string name, int depth)
        {
            if (depth > 64) throw new InvalidDataException("存档嵌套层数过多。");
            switch (type)
            {
                case 1: values[name] = (int)reader.ReadSByte(); break;
                case 2: Skip(2); break;
                case 3: values[name] = Int(); break;
                case 4: values[name] = Long(); break;
                case 5: Skip(4); break;
                case 6: Skip(8); break;
                case 7: Skip(Int()); break;
                case 8: values[name] = Text(); break;
                case 9:
                    var subtype = reader.ReadByte(); var count = Int();
                    if (count < 0 || count > 1_000_000) throw new InvalidDataException("存档列表长度无效。");
                    for (var i = 0; i < count; i++) Tag(subtype, "", depth + 1);
                    break;
                case 10:
                    byte child;
                    while ((child = reader.ReadByte()) != 0) Tag(child, name + "/" + Text(), depth + 1);
                    break;
                case 11: Skip(checked((long)Int() * 4)); break;
                case 12: Skip(checked((long)Int() * 8)); break;
                default: throw new InvalidDataException("不支持的存档标签。");
            }
        }
        var root = reader.ReadByte();
        if (root != 10) throw new InvalidDataException("存档没有根标签。");
        Text(); Tag(root, "", 0);
        object? Get(string name) => values.GetValueOrDefault("/Data/" + name);
        var mode = Get("GameType") as int?;
        var description = mode switch { 0 => "生存模式", 1 => "创造模式", 2 => "冒险模式", 3 => "旁观模式", _ => "游戏模式未知" };
        if (Get("hardcore") is int hardcore && hardcore != 0) description = "极限模式";
        DateTime? played = Get("LastPlayed") is long millis && millis > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime : null;
        return new(Get("LevelName") as string ?? "", Get("Version/Name") as string ?? "", description, played);
    }
}
