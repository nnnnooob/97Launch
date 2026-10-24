using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;

namespace Launcher.App.Services;

public sealed class InstanceModificationService(InstanceContentService content, InstanceScanner scanner,
    MinecraftInstallService installer)
{
    public async Task<InstanceInstallManifest> ReadInstalledAsync(InstanceContext context, CancellationToken token)
    {
        var manifest = await Task.Run(() => InstanceInstallManifest.TryLoad(context.Instance.Path), token);
        if (manifest is { SchemaVersion: > 2 }) throw new InvalidDataException("安装清单由更新版本的启动器创建，请更新后再修改。");
        if (manifest is null)
        {
            var found = (await scanner.ScanAsync(context.Root, token)).FirstOrDefault(i => GameFolderService.PathsEqual(i.Path, context.Instance.Path))
                ?? throw new InvalidDataException("无法识别此实例，请先检查版本文件。");
            if (found.Kind == InstanceKind.Error || found.VanillaName == "Unknown") throw new InvalidDataException("无法准确识别 Minecraft 版本，不能直接修改。");
            var loader = found.FabricVersion is not null ? "Fabric" : found.NeoForgeVersion is not null ? "NeoForge" : found.ForgeVersion is not null ? "Forge" : null;
            var version = found.FabricVersion ?? found.NeoForgeVersion ?? found.ForgeVersion;
            if (version == "未知版本" || found.OptiFineVersion == "未知版本") throw new InvalidDataException("无法识别已安装组件的版本，请先检查版本文件。");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(found.Path, found.Name + ".json"), token));
            var lite = json?["libraries"]?.AsArray().Select(n => n?["name"]?.GetValue<string>())
                .FirstOrDefault(n => n?.StartsWith("com.mumfrey:liteloader:", StringComparison.Ordinal) == true)?.Split(':').ElementAtOrDefault(2);
            manifest = new(2, found.VanillaName, loader, version, lite, found.OptiFineVersion);
        }
        if (manifest.OptiFineVersion is { } optiFine && !optiFine.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            var versions = await installer.GetLoaderVersionsAsync("OptiFine", manifest.MinecraftVersion, token);
            var normalized = versions.FirstOrDefault(v => v.Version.Replace(' ', '_').EndsWith(optiFine.Replace("HD_U_", "").Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
            if (normalized is null) throw new InvalidDataException("无法还原当前 OptiFine 的安装信息，请刷新版本列表后重试。");
            manifest = manifest with { OptiFineVersion = normalized.Id ?? normalized.Version };
        }
        if (manifest.ManagedFiles is null)
        {
            var files = await content.ScanAsync(context, ContentKind.Mods, token);
            var managed = new List<ManagedComponentFile>();
            foreach (var file in files.Where(f => f.Enabled && (f.ModId is "fabric-api" or "optifabric"
                         || manifest.OptiFineVersion is not null && f.FileName.StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase))))
            {
                var component = file.ModId == "fabric-api" ? "Fabric API" : file.ModId == "optifabric" ? "OptiFabric" : "OptiFine";
                if (component == "Fabric API") manifest = manifest with { FabricApiVersion = manifest.FabricApiVersion ?? file.Version };
                if (component == "OptiFabric") manifest = manifest with { OptiFabricVersion = manifest.OptiFabricVersion ?? file.Version };
                managed.Add(new(Path.GetRelativePath(context.GameDirectory, file.Path).Replace('\\', '/'), await HashAsync(file.Path, token), component));
            }
            manifest = manifest with { ManagedFiles = managed };
        }
        // Installation-owned API mods may have been updated, disabled or removed in the mod manager.
        if (manifest.Loader == "Fabric")
        {
            var files = await content.ScanAsync(context, ContentKind.Mods, token);
            var managed = (manifest.ManagedFiles ?? []).ToList();
            foreach (var (id, component) in new[] { ("fabric-api", "Fabric API"), ("optifabric", "OptiFabric") })
            {
                var live = files.Where(f => f.Enabled && f.ModId == id).OrderByDescending(f => f.Modified).FirstOrDefault();
                var old = managed.FirstOrDefault(f => f.Component == component);
                if (live is null)
                {
                    managed.RemoveAll(f => f.Component == component);
                    manifest = component == "Fabric API" ? manifest with { FabricApiVersion = null } : manifest with { OptiFabricVersion = null };
                    continue;
                }
                var hash = await HashAsync(live.Path, token);
                var installedValue = component == "Fabric API" ? manifest.FabricApiVersion : manifest.OptiFabricVersion;
                if (old is not null && old.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase)
                    && GameFolderService.PathsEqual(InstanceContentService.SafeChild(context.GameDirectory, old.Path), live.Path)
                    && installedValue is not null && !installedValue.Contains('.')) continue;
                var version = live.Version;
                try
                {
                    var versions = await installer.GetLoaderVersionsAsync(component, manifest.MinecraftVersion, token);
                    var match = versions.FirstOrDefault(v => v.Version == live.Version);
                    if (match is not null) version = match.Id ?? match.Version;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException && !token.IsCancellationRequested) { }
                if (string.IsNullOrWhiteSpace(version)) continue;
                manifest = component == "Fabric API" ? manifest with { FabricApiVersion = version } : manifest with { OptiFabricVersion = version };
                managed.RemoveAll(f => f.Component == component);
                managed.Add(new(Path.GetRelativePath(context.GameDirectory, live.Path).Replace('\\', '/'), hash, component));
            }
            manifest = manifest with { ManagedFiles = managed };
        }
        return manifest;
    }

    public static bool SameComponents(InstanceInstallManifest current, InstallRequest target) =>
        current.MinecraftVersion == target.Version && current.Loader == target.Loader && current.LoaderVersion == target.LoaderVersion
        && current.LiteLoaderVersion == target.LiteLoaderVersion && current.OptiFineVersion == target.OptiFineVersion
        && current.FabricApiVersion == target.FabricApiVersion && current.OptiFabricVersion == target.OptiFabricVersion;

    public async Task<string> ModifyAsync(InstanceContext context, InstallRequest request,
        IProgress<InstallProgress> progress, CancellationToken token)
    {
        ModrinthModpackInstaller.ValidateName(request.Name);
        using var operation = await content.AcquireAsync(context, token);
        var oldName = Path.GetFileName(context.Instance.Path);
        var newPath = Path.Combine(context.Root, "versions", request.Name);
        InstanceContentService.ValidatePath(context.Root, newPath);
        if (!GameFolderService.PathsEqual(context.Instance.Path, newPath) && Directory.Exists(newPath)) throw new IOException("同名实例已经存在。");
        var current = await ReadInstalledAsync(context, token);
        if (SameComponents(current, request))
        {
            if (request.Name != oldName) await RenameAsync(context, request.Name, token);
            progress.Report(new(1, 1, "实例名称已更新"));
            content.Notify(context);
            return newPath;
        }
        var work = Path.Combine(context.Root, "97Launch", "temp", "modify-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(work, oldName);
        try
        {
            // Shared libraries are reused; version metadata and managed mods are built away from the live instance.
            await installer.InstallAsync(request with { Name = oldName, GameDirectory = context.Root, OutputDirectory = stage, Isolated = true },
                new BuildProgress(progress), token);
            var next = InstanceInstallManifest.TryLoad(stage) ?? throw new InvalidDataException("新版本缺少安装清单。");
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(context.Instance.Path, oldName + ".json")] = Path.Combine(stage, oldName + ".json"),
                [Path.Combine(context.Instance.Path, oldName + ".jar")] = Path.Combine(stage, oldName + ".jar"),
            };
            foreach (var file in next.ManagedFiles ?? [])
                replacements[InstanceContentService.SafeChild(context.GameDirectory, file.Path)] = InstanceContentService.SafeChild(stage, file.Path);
            var remove = new List<string>();
            foreach (var file in current.ManagedFiles ?? [])
            {
                var path = InstanceContentService.SafeChild(context.GameDirectory, file.Path);
                if (!File.Exists(path)) continue;
                if (!string.Equals(await HashAsync(path, token), file.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"组件文件已被手动修改，请先处理后再修改版本：{Path.GetFileName(path)}");
                if (!replacements.ContainsKey(path)) remove.Add(path);
            }
            foreach (var pair in replacements.Where(p => p.Key.StartsWith(context.DirectoryFor(ContentKind.Mods) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                if (File.Exists(pair.Key) && !(current.ManagedFiles ?? []).Any(f => GameFolderService.PathsEqual(InstanceContentService.SafeChild(context.GameDirectory, f.Path), pair.Key))
                    && !string.Equals(await HashAsync(pair.Key, token), await HashAsync(pair.Value, token), StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"新组件与已有文件冲突：{Path.GetFileName(pair.Key)}。原文件未被覆盖。");

            var nextPath = Path.Combine(stage, "97Launch", "InstallManifest.json");
            replacements[InstanceInstallManifest.GetPath(context.Instance.Path)] = nextPath;
            var touched = replacements.Keys.Concat(remove).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backup = Path.Combine(context.Root, "97Launch", "backups", "modify-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(backup);
            var journal = touched.Select(path => new JournalFile(Path.GetRelativePath(context.Root, path), File.Exists(path))).ToArray();
            foreach (var item in journal.Where(i => i.Existed))
            {
                token.ThrowIfCancellationRequested();
                var source = InstanceContentService.SafeChild(context.Root, item.Path);
                var target = InstanceContentService.SafeChild(backup, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            token.ThrowIfCancellationRequested();
            content.EnsureWritable(context);
            var marker = JournalPath(context.Instance.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new ModificationJournal(backup, journal)), token);
            progress.Report(new(1, 0, "提交组件修改"));
            try
            {
                foreach (var pair in replacements)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                    var temp = pair.Key + ".97launch.tmp";
                    File.Copy(pair.Value, temp, true);
                    File.Move(temp, pair.Key, true);
                }
                foreach (var path in remove) File.Delete(path);
                File.Delete(marker);
                if (request.Name != oldName) newPath = await RenameAsync(context, request.Name, CancellationToken.None);
            }
            catch
            {
                Restore(context.Root, new(backup, journal));
                if (File.Exists(marker)) File.Delete(marker);
                throw;
            }
            progress.Report(new(1, 1, "修改完成，已保留修改前的备份"));
            content.Notify(context);
            return newPath;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning(ex, "清理组件修改临时文件失败：{Path}", work); }
        }
    }

    public static string JournalPath(string instancePath) => Path.Combine(instancePath, "97Launch", "Modification.json");

    public async Task RecoverAsync(InstanceContext context, CancellationToken token)
    {
        Serilog.Log.Information("开始恢复实例修改备份：{Instance}", context.Instance.Name);
        using var operation = await content.AcquireAsync(context, token);
        await Task.Run(() =>
        {
            var path = JournalPath(context.Instance.Path);
            var journal = JsonSerializer.Deserialize<ModificationJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException("修改记录损坏。");
            InstanceContentService.ValidatePath(Path.Combine(context.Root, "97Launch", "backups"), journal.Backup);
            Restore(context.Root, journal);
            File.Delete(path);
        }, token);
        content.Notify(context);
        Serilog.Log.Information("实例修改备份已恢复：{Instance}", context.Instance.Name);
    }

    private static void Restore(string root, ModificationJournal journal)
    {
        foreach (var item in journal.Files)
        {
            var target = InstanceContentService.SafeChild(root, item.Path);
            if (item.Existed)
            {
                var source = InstanceContentService.SafeChild(journal.Backup, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
    }

    private static async Task<string> RenameAsync(InstanceContext context, string name, CancellationToken token)
    {
        var oldPath = context.Instance.Path;
        var oldName = Path.GetFileName(oldPath);
        var target = Path.Combine(context.Root, "versions", name);
        if (oldName == name) return oldPath;
        foreach (var folder in Directory.EnumerateDirectories(Path.Combine(context.Root, "versions")))
        {
            if (GameFolderService.PathsEqual(folder, oldPath)) continue;
            var profile = Path.Combine(folder, Path.GetFileName(folder) + ".json");
            if (File.Exists(profile) && JsonNode.Parse(await File.ReadAllTextAsync(profile, token))?["inheritsFrom"]?.GetValue<string>() == oldName)
                throw new IOException($"实例「{Path.GetFileName(folder)}」依赖此实例，暂不能重命名。");
        }
        if (Directory.Exists(target)) throw new IOException("同名实例已存在。");
        var oldJson = Path.Combine(oldPath, oldName + ".json");
        var text = await File.ReadAllTextAsync(oldJson, token);
        var json = JsonNode.Parse(text)!.AsObject();
        json["id"] = name;
        if (json["jar"]?.GetValue<string>() == oldName) json["jar"] = name;
        token.ThrowIfCancellationRequested();
        Directory.Move(oldPath, target);
        try
        {
            File.Move(Path.Combine(target, oldName + ".json"), Path.Combine(target, name + ".json"));
            if (File.Exists(Path.Combine(target, oldName + ".jar"))) File.Move(Path.Combine(target, oldName + ".jar"), Path.Combine(target, name + ".jar"));
            await File.WriteAllTextAsync(Path.Combine(target, name + ".json"), json.ToJsonString());
            return target;
        }
        catch
        {
            if (File.Exists(Path.Combine(target, name + ".json"))) File.Move(Path.Combine(target, name + ".json"), Path.Combine(target, oldName + ".json"), true);
            if (File.Exists(Path.Combine(target, name + ".jar"))) File.Move(Path.Combine(target, name + ".jar"), Path.Combine(target, oldName + ".jar"), true);
            await File.WriteAllTextAsync(Path.Combine(target, oldName + ".json"), text);
            Directory.Move(target, oldPath);
            throw;
        }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(input, token)).ToLowerInvariant();
    }

    private sealed record JournalFile(string Path, bool Existed);
    private sealed record ModificationJournal(string Backup, JournalFile[] Files);
    private sealed class BuildProgress(IProgress<InstallProgress> target) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => target.Report(new(0, Math.Clamp((value.Stage + value.Progress) / 6d, 0, 1), "准备新版本：" + value.Detail, value.BytesPerSecond));
    }
}
