using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App.Services;

public sealed record InstanceContext(InstanceItem Instance, string Root, string GameDirectory)
{
    public bool Shared => !GameFolderService.PathsEqual(GameDirectory, Instance.Path);
    public string DirectoryFor(ContentKind kind) => Path.Combine(GameDirectory, LocalContentReader.Folder(kind));
}

public sealed class InstanceContentService(SettingsStore settings, InstanceLaunchSettingsStore localSettings, GameSessionManager sessions)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Busy = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Changed;

    public InstanceContext Context(InstanceItem instance)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(instance.Path))!)!;
        var global = settings.Load();
        var local = localSettings.Load(instance.Path, global);
        return new(instance, root, MinecraftLaunchService.ResolveGameDirectory(instance, root, local, global));
    }

    public void Notify(InstanceContext context) => Changed?.Invoke(context.GameDirectory);
    public static void ThrowIfBusy(string directory)
    {
        if (Busy.ContainsKey(Path.GetFullPath(directory))) throw new IOException("此实例正在修改文件，请等待任务完成。");
    }

    public void EnsureWritable(InstanceContext context)
    {
        if (!Directory.Exists(context.Instance.Path)) throw new DirectoryNotFoundException("实例目录已不存在。");
        foreach (var session in sessions.ActiveSessions)
        {
            var sameInstance = GameFolderService.PathsEqual(session.InstancePath, context.Instance.Path);
            var sameDirectory = session.GameDirectory is { } path && GameFolderService.PathsEqual(path, context.GameDirectory);
            var startingInSharedRoot = session.GameDirectory is null && context.Shared
                && session.InstancePath.StartsWith(Path.Combine(context.Root, "versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (sameInstance || sameDirectory || startingInSharedRoot)
                throw new IOException($"请先关闭正在启动或运行的实例「{session.InstanceName}」，再修改文件。");
        }
        ValidatePath(context.Root, context.GameDirectory);
    }

    public async Task<IDisposable> AcquireAsync(InstanceContext context, CancellationToken token)
    {
        var key = Path.GetFullPath(context.GameDirectory);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try { EnsureWritable(context); Busy[key] = 0; return new Release(() => { Busy.TryRemove(key, out _); gate.Release(); }); }
        catch { gate.Release(); throw; }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public Task<IReadOnlyList<LocalContent>> ScanAsync(InstanceContext context, ContentKind kind, CancellationToken token) =>
        Task.Run(() => LocalContentReader.Scan(context.DirectoryFor(kind), kind, context.Instance.MinecraftVersionId,
            context.Instance.Loader == "Forge" && Version.TryParse(context.Instance.MinecraftVersionId, out var v) && v < new Version(1, 13),
            Path.Combine(context.Root, "97Launch", "cache", "local-icons"), token), token);

    public Task<Version?> ResourceFormatAsync(InstanceContext context, CancellationToken token) => Task.Run(() =>
    {
        try
        {
            var jar = Path.Combine(context.Instance.Path, Path.GetFileName(context.Instance.Path) + ".jar");
            if (!File.Exists(jar)) return null;
            using var archive = ZipFile.OpenRead(jar);
            var entry = archive.GetEntry("version.json");
            if (entry is null || entry.Length > 1024 * 1024) return null;
            using var input = entry.Open(); using var document = JsonDocument.Parse(input);
            var pack = document.RootElement.GetProperty("pack_version");
            if (pack.TryGetProperty("resource_major", out var major)) return new Version(major.GetInt32(), pack.TryGetProperty("resource_minor", out var minor) ? minor.GetInt32() : 0);
            return pack.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Number ? new Version(resource.GetInt32(), 0) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return null; }
    }, token);

    public async Task ToggleModsAsync(InstanceContext context, IReadOnlyList<LocalContent> entries, bool enabled, CancellationToken token)
    {
        Serilog.Log.Information("开始{Operation} Mod：{Instance}，{Count} 项", enabled ? "启用" : "禁用", context.Instance.Name, entries.Count);
        using var operation = await AcquireAsync(context, token);
        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ValidatePath(context.DirectoryFor(ContentKind.Mods), entry.Path);
                if (entry.Enabled == enabled) continue;
                var target = enabled ? Path.Combine(Path.GetDirectoryName(entry.Path)!, LocalContentReader.EnabledName(entry.FileName)) : entry.Path + ".disabled";
                if (File.Exists(target)) throw new IOException($"启用和禁用文件发生冲突：{Path.GetFileName(target)}。请先检查这两个文件。");
                File.Move(entry.Path, target);
            }
        }, token);
        Notify(context);
        Serilog.Log.Information("Mod {Operation}完成：{Instance}，{Count} 项", enabled ? "启用" : "禁用", context.Instance.Name, entries.Count);
    }

    public async Task DeleteAsync(InstanceContext context, ContentKind kind, IReadOnlyList<LocalContent> entries, CancellationToken token)
    {
        Serilog.Log.Information("开始移入回收站：{Instance}，{Kind}，{Count} 项", context.Instance.Name, kind, entries.Count);
        using var operation = await AcquireAsync(context, token);
        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ValidatePath(context.DirectoryFor(kind), entry.Path);
                Recycle(entry.Path);
            }
        }, token);
        Notify(context);
        Serilog.Log.Information("已移入回收站：{Instance}，{Kind}，{Count} 项", context.Instance.Name, kind, entries.Count);
    }

    internal static void Recycle(string path)
    {
        var operation = new Win32.SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero, wFunc = Win32.FO_DELETE, pFrom = path + "\0\0",
            fFlags = Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_SILENT | Win32.FOF_NOERRORUI,
        };
        var result = Win32.SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted) throw new IOException($"无法移到回收站：{Path.GetFileName(path)}（错误码 {result}）。");
    }

    public async Task ImportAsync(InstanceContext context, ContentKind kind, IReadOnlyList<string> sources, bool overwrite, CancellationToken token)
    {
        Serilog.Log.Information("开始导入实例资源：{Instance}，{Kind}，{Count} 项", context.Instance.Name, kind, sources.Count);
        using var operation = await AcquireAsync(context, token);
        await Task.Run(async () =>
        {
            var folder = context.DirectoryFor(kind);
            Directory.CreateDirectory(folder);
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (kind == ContentKind.Saves) { await ImportWorldAsync(folder, source, token); continue; }
                var name = LocalContentReader.EnabledName(Path.GetFileName(source));
                if (kind == ContentKind.Mods && Path.GetExtension(name).ToLowerInvariant() is not (".jar" or ".zip" or ".litemod"))
                    throw new InvalidDataException("请选择 jar、zip、litemod 或对应的禁用 Mod 文件。");
                if (kind is ContentKind.ResourcePacks or ContentKind.ShaderPacks && !Directory.Exists(source))
                {
                    using var archive = ZipFile.OpenRead(source);
                    if (kind == ContentKind.ResourcePacks && archive.GetEntry("pack.mcmeta") is null) throw new InvalidDataException("资源包缺少 pack.mcmeta。");
                    if (kind == ContentKind.ShaderPacks && !archive.Entries.Any(e => e.FullName.StartsWith("shaders/"))) throw new InvalidDataException("光影包缺少 shaders 文件夹。");
                }
                var target = SafeChild(folder, name);
                if (GameFolderService.PathsEqual(source, target)) continue;
                if (!overwrite && (File.Exists(target) || Directory.Exists(target))) throw new IOException($"同名文件已存在：{name}");
                var temporary = Path.Combine(folder, ".97launch-import-" + Guid.NewGuid().ToString("N"));
                try
                {
                    if (Directory.Exists(source))
                    {
                        await CopyTreeAsync(source, temporary, token);
                        if (Directory.Exists(target)) Recycle(target);
                        Directory.Move(temporary, target);
                    }
                    else
                    {
                        await using (var input = File.OpenRead(source))
                        await using (var output = File.Create(temporary)) await input.CopyToAsync(output, token);
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(target)) Recycle(target);
                        File.Move(temporary, target);
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            }
        }, token);
        Notify(context);
        Serilog.Log.Information("实例资源导入完成：{Instance}，{Kind}，{Count} 项", context.Instance.Name, kind, sources.Count);
    }

    private static async Task ImportWorldAsync(string saves, string source, CancellationToken token)
    {
        var stage = Path.Combine(saves, ".97launch-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string world;
            if (Directory.Exists(source))
            {
                world = File.Exists(Path.Combine(source, "level.dat")) ? source
                    : Directory.EnumerateDirectories(source).SingleOrDefault(p => File.Exists(Path.Combine(p, "level.dat")))
                        ?? throw new InvalidDataException("文件夹中没有找到单个 Minecraft 世界。");
                await CopyTreeAsync(world, stage, token);
            }
            else
            {
                using var archive = ZipFile.OpenRead(source);
                var levels = archive.Entries.Where(e => e.FullName == "level.dat" || e.FullName.EndsWith("/level.dat", StringComparison.Ordinal)).ToArray();
                if (levels.Length != 1) throw new InvalidDataException("压缩包中必须包含一个 Minecraft 世界（level.dat）。");
                var prefix = levels[0].FullName[..^9];
                world = prefix.TrimEnd('/').Split('/').LastOrDefault() ?? "";
                if (world.Length == 0) world = Path.GetFileNameWithoutExtension(source);
                long total = 0;
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
                {
                    token.ThrowIfCancellationRequested();
                    total += entry.Length;
                    if (total > 32L * 1024 * 1024 * 1024) throw new InvalidDataException("世界压缩包解压大小超过支持范围。");
                    var target = SafeChild(stage, entry.FullName[prefix.Length..]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.Open(); await using var output = File.Create(target);
                    await input.CopyToAsync(output, token);
                }
            }
            WorldMetadata.Read(Path.Combine(stage, "level.dat"));
            var name = Directory.Exists(source) ? Path.GetFileName(world) : world;
            var destination = SafeChild(saves, name);
            for (var n = 2; Directory.Exists(destination); n++) destination = SafeChild(saves, name + " (" + n + ")");
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static IEnumerable<string> Files(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(path)) { foreach (var file in Files(path)) yield return file; }
            else yield return path;
        }
    }

    internal static async Task CopyTreeAsync(string source, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Files(source))
        {
            token.ThrowIfCancellationRequested();
            var target = SafeChild(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file); await using var output = File.Create(target);
            await input.CopyToAsync(output, token);
        }
    }

    public static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(p => p is "." or ".." || p.Length == 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("文件路径无效。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        ValidatePath(root, full);
        return full;
    }

    public static void ValidatePath(string root, string target)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件不属于当前实例目录。");
        for (var current = target; current.Length >= root.Length; current = Path.GetDirectoryName(current) ?? "")
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不支持通过目录链接修改实例文件。");
    }

    public static void Open(string path, bool select = false)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("文件已不存在。", path);
        if (select)
        {
            Marshal.ThrowExceptionForHR(Win32.SHParseDisplayName(path, IntPtr.Zero, out var item, 0, out _));
            try { Marshal.ThrowExceptionForHR(Win32.SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0)); }
            finally { Marshal.FreeCoTaskMem(item); }
        }
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public string ReadOption(InstanceContext context, string file, string key)
    {
        var path = SafeChild(context.GameDirectory, file);
        if (!File.Exists(path)) return "";
        return File.ReadLines(path).FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))?[key.Length..] ?? "";
    }

    public async Task WriteOptionAsync(InstanceContext context, string file, string key, string value, CancellationToken token)
    {
        using var operation = await AcquireAsync(context, token);
        await Task.Run(async () =>
        {
            var path = SafeChild(context.GameDirectory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path, token)).ToList() : [];
            var index = lines.FindIndex(l => l.StartsWith(key, StringComparison.Ordinal));
            if (index < 0) lines.Add(key + value); else lines[index] = key + value;
            var temp = path + ".97launch.tmp";
            try { await File.WriteAllLinesAsync(temp, lines, token); token.ThrowIfCancellationRequested(); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }, token);
        Notify(context);
        Serilog.Log.Information("实例资源选项已保存：{Instance}，{File}，{Option}", context.Instance.Name, file, key);
    }
}
