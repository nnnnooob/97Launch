using System.Text.Json;
using System.Security.Cryptography;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;

namespace Launcher.App.Services;

public sealed record ModInstallSelection(ModrinthProject Project, ModrinthVersion Version, LocalContent? Original = null, string? OriginalSha1 = null);
public sealed record ManagedModFile(string FileName, string Sha1, string Source, string ProjectId, string VersionId);
public sealed record InstalledProjectFile(LocalContent File, ModrinthVersion? Version, string Sha1);

public sealed class ModInstallService(CommunityResourceService resources, InstanceContentService content, MinecraftInstallService installer)
{
    private const string IndexFile = "97Launch/mod-index.json";
    private const string JournalFile = "97Launch/mod-change.json";
    private sealed record ChangedFile(string Path, string? BeforeHash, string? AfterHash);
    private sealed record ChangeJournal(string Backup, ChangedFile[] Files, bool Completed);

    public static IReadOnlyList<ManagedModFile> ReadIndex(InstanceContext context)
    {
        var path = InstanceContentService.SafeChild(context.GameDirectory, IndexFile);
        try { return File.Exists(path) ? (JsonSerializer.Deserialize<ManagedModFile[]>(File.ReadAllText(path)) ?? [])
            .Where(m => m is not null && !string.IsNullOrEmpty(m.Sha1) && !string.IsNullOrEmpty(m.ProjectId)
                && !string.IsNullOrEmpty(m.VersionId) && m.Source is "Modrinth" or "CurseForge").ToArray() : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }

    public async Task<IReadOnlyList<InstalledProjectFile>> FindInstalledAsync(InstanceContext context, ModrinthProject project,
        IReadOnlyList<ModrinthVersion> versions, CancellationToken token)
    {
        var byHash = versions.SelectMany(v => v.Files.Where(f => !string.IsNullOrEmpty(f.Sha1)).Select(f => (f.Sha1, Version: v)))
            .GroupBy(p => p.Sha1!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);
        var managed = ReadIndex(context).Where(m => m.Source == project.Source && m.ProjectId == project.Id)
            .GroupBy(m => m.Sha1, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledProjectFile>();
        foreach (var file in await content.ScanAsync(context, ContentKind.Mods, token))
        {
            var hash = await InstanceModificationService.HashAsync(file.Path, token);
            if (byHash.TryGetValue(hash, out var version)) result.Add(new(file, version, hash));
            else if (managed.TryGetValue(hash, out var record)) result.Add(new(file, versions.FirstOrDefault(v => v.Id == record.VersionId), hash));
        }
        return result;
    }

    public static bool HasInterruptedChange(string gameDirectory)
    {
        var path = InstanceContentService.SafeChild(gameDirectory, JournalFile);
        if (!File.Exists(path)) return false;
        try { return JsonSerializer.Deserialize<ChangeJournal>(File.ReadAllText(path))?.Completed != true; }
        catch (JsonException) { return true; }
    }

    public async Task DownloadAsync(InstanceContext context, ModrinthProject project, ModrinthVersion selection, string nameFormat,
        IProgress<InstallProgress> progress, CancellationToken token)
    {
        var version = (await resources.GetVersionsAsync(project, token)).FirstOrDefault(v => v.Id == selection.Id)
            ?? throw new IOException("所选版本已不存在，请刷新发布列表。");
        var file = version.PrimaryFile ?? throw new IOException("此版本需要前往项目主页下载。");
        using var operation = await content.AcquireAsync(context, token);
        var destination = InstanceContentService.SafeChild(context.DirectoryFor(ContentKind.Mods),
            ResourceFileName.Format(project.Title, file.Name, nameFormat));
        InstanceContentService.ValidatePath(context.GameDirectory, destination);
        if (File.Exists(destination) && (file.Sha1 is null
            || !file.Sha1.Equals(await InstanceModificationService.HashAsync(destination, token), StringComparison.OrdinalIgnoreCase)))
        {
            var backup = Path.Combine(context.Root, "97Launch", "backups", "downloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            File.Copy(destination, Path.Combine(backup, Path.GetFileName(destination)));
        }
        await installer.DownloadFilesAsync(context.Root, "下载 " + project.Title,
            [new(destination, "mod", file.Sha1, file.Size, CommunityResourceService.DownloadSources(file.Url).ToArray(),
                file.Md5, project.Source, project.Id, version.Id)], progress, token);
        content.Notify(context);
    }

    public async Task InstallAsync(InstanceContext context, IReadOnlyList<ModInstallSelection> selections, string nameFormat,
        IProgress<InstallProgress> progress, CancellationToken token)
    {
        using var operation = await content.AcquireAsync(context, token);
        if (HasInterruptedChange(context.GameDirectory)) throw new IOException("上次 Mod 变更尚未完成，请先在 Mod 页面回退上次变更。");
        var stage = Path.Combine(context.Root, "97Launch", "temp", "mods-" + Guid.NewGuid().ToString("N"));
        var filesDirectory = Path.Combine(stage, "files");
        Directory.CreateDirectory(filesDirectory);
        try
        {
            var requests = new List<DownloadFileRequest>();
            foreach (var selection in selections)
            {
                if (!ResourceVersionSelection.IsCompatible(selection.Version, context.Instance.MinecraftVersionId, context.Instance.Loader.ToLowerInvariant()))
                    throw new InvalidDataException($"{selection.Project.Title} 的所选版本不兼容当前游戏和加载器。");
                progress.Report(new(0, 0, "检查 " + selection.Project.Title + " 与必要前置"));
                requests.AddRange(await resources.ResolveFilesAsync(selection.Project, context.Instance.MinecraftVersionId,
                    context.Instance.Loader.ToLowerInvariant(), filesDirectory, selection.Version.Id, new HashSet<string>(), nameFormat, token));
            }
            foreach (var group in requests.GroupBy(r => (r.ResourceSource, r.ProjectId)))
                if (group.Select(r => r.VersionId).Distinct().Count() > 1)
                    throw new InvalidDataException("所选 Mod 对同一前置要求不同版本，请分开检查兼容性后重试。");
            requests = requests.DistinctBy(r => (r.ResourceSource, r.ProjectId, r.VersionId)).ToList();
            if (requests.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Count)
                throw new InvalidDataException("Mod 文件名冲突，请改用原始文件名。");
            if (requests.Any(r => r.Kind != "mod" || Path.GetExtension(r.Path).ToLowerInvariant() is not (".jar" or ".zip" or ".litemod")))
                throw new InvalidDataException("前置中包含无法作为 Mod 安装的资源，请从对应分类单独安装。");

            var existing = await content.ScanAsync(context, ContentKind.Mods, token);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in existing) hashes[file.Path] = await InstanceModificationService.HashAsync(file.Path, token);
            foreach (var selection in selections.Where(s => s.Original is not null))
                if (!hashes.TryGetValue(selection.Original!.Path, out var hash)
                    || selection.OriginalSha1 is not null && !hash.Equals(selection.OriginalSha1, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("原 Mod 已被修改或移除，请刷新后重新检查更新。");

            foreach (var request in requests)
            foreach (var conflict in request.Conflicts ?? [])
            {
                var conflictingProject = await resources.GetDependencyProjectAsync(request.ResourceSource!, conflict, token);
                var planned = requests.FirstOrDefault(r => r.ResourceSource == request.ResourceSource && r.ProjectId == conflictingProject.Id);
                if (planned is not null)
                {
                    if (string.IsNullOrEmpty(conflict.VersionId) || planned.VersionId == conflict.VersionId)
                        throw new InvalidDataException($"所选 Mod 与「{conflictingProject.Title}」不兼容，无法一同安装。");
                    continue;
                }
                var versions = await resources.GetVersionsAsync(conflictingProject, token);
                var installed = await FindInstalledAsync(context, conflictingProject, versions, token);
                if (installed.Any(f => f.File.Enabled && (string.IsNullOrEmpty(conflict.VersionId) || f.Version?.Id == conflict.VersionId)))
                    throw new InvalidDataException($"所选 Mod 与已启用的「{conflictingProject.Title}」不兼容，请先在 Mod 页面处理冲突。");
            }

            var download = new List<DownloadFileRequest>();
            foreach (var request in requests)
            {
                var same = existing.FirstOrDefault(f => request.Sha1 is not null && hashes[f.Path].Equals(request.Sha1, StringComparison.OrdinalIgnoreCase));
                var primary = selections.Any(s => s.Project.Source == request.ResourceSource && s.Project.Id == request.ProjectId);
                if (same is not null)
                {
                    if (!same.Enabled && !primary) throw new IOException($"必要前置「{same.Name}」已禁用，请先启用后重试。");
                    File.Copy(same.Path, request.Path);
                }
                else download.Add(request);
            }
            await installer.DownloadFilesAsync(context.Root, "下载 Mod 与必要前置", download, progress, token);
            var staged = await Task.Run(() => LocalContentReader.Scan(filesDirectory, ContentKind.Mods, context.Instance.MinecraftVersionId,
                false, Path.Combine(context.Root, "97Launch", "cache", "local-icons"), token), token);
            if (staged.Count != requests.Count || staged.Any(f => f.Error is not null))
                throw new InvalidDataException("下载的 Mod 文件无法读取，已保留原文件。");
            var index = ReadIndex(context).Where(m => existing.Any(f => hashes[f.Path].Equals(m.Sha1, StringComparison.OrdinalIgnoreCase))).ToList();
            var replacements = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var prospective = existing.ToList();
            foreach (var request in requests)
            {
                var fresh = staged.Single(f => GameFolderService.PathsEqual(f.Path, request.Path));
                var actualHash = await InstanceModificationService.HashAsync(request.Path, token);
                if (request.Sha1 is not null && !actualHash.Equals(request.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mod 文件校验失败，原文件未被替换：" + fresh.Name);
                var selection = selections.FirstOrDefault(s => s.Project.Source == request.ResourceSource && s.Project.Id == request.ProjectId);
                var old = existing.Where(f => (fresh.ModId is { Length: > 0 } && f.ModId == fresh.ModId)
                    || index.Any(m => m.Source == request.ResourceSource && m.ProjectId == request.ProjectId && m.Sha1 == hashes[f.Path])
                    || hashes[f.Path].Equals(actualHash, StringComparison.OrdinalIgnoreCase)
                    || selection?.Original?.Path == f.Path).ToArray();
                if (old.Length > 1) throw new IOException($"发现重复 Mod「{fresh.Name}」：{string.Join("、", old.Select(f => f.FileName))}。请先处理重复文件。");
                if (old.FirstOrDefault() is { Enabled: false } disabled && selection is null)
                    throw new IOException($"必要前置「{disabled.Name}」已禁用，请先启用后重试。");
                var previous = old.FirstOrDefault();
                var identical = previous is not null && hashes[previous.Path].Equals(actualHash, StringComparison.OrdinalIgnoreCase);
                var destination = identical ? previous!.Path : InstanceContentService.SafeChild(context.DirectoryFor(ContentKind.Mods),
                    fresh.FileName + (previous is { Enabled: false } ? ".disabled" : ""));
                if (!identical)
                {
                    if (File.Exists(destination) && old.All(f => !GameFolderService.PathsEqual(f.Path, destination)))
                        throw new IOException("已有同名文件，无法确认是否为同一个 Mod：" + Path.GetFileName(destination));
                    if (replacements.ContainsKey(destination)) throw new IOException("多个 Mod 使用同一个目标文件名。");
                    foreach (var file in old)
                    {
                        InstanceContentService.ValidatePath(context.GameDirectory, file.Path);
                        replacements[file.Path] = null; prospective.Remove(file);
                    }
                    replacements[destination] = request.Path;
                    prospective.Add(fresh with { Path = destination, Enabled = previous?.Enabled ?? true });
                }
                index.RemoveAll(m => m.Source == request.ResourceSource && m.ProjectId == request.ProjectId);
                index.Add(new(Path.GetFileName(destination), actualHash, request.ResourceSource!, request.ProjectId!, request.VersionId!));
            }
            var diagnostics = ModDiagnostics.Inspect(prospective, context.Instance.MinecraftVersionId, context.Instance.Loader);
            var baseline = ModDiagnostics.Inspect(existing, context.Instance.MinecraftVersionId, context.Instance.Loader);
            var introduced = prospective.SelectMany(f => diagnostics[f.Path].Except(baseline.GetValueOrDefault(f.Path) ?? [])).Distinct().ToArray();
            if (introduced.Length > 0) throw new InvalidDataException(string.Join("\n", introduced));
            if (replacements.Count == 0) { content.Notify(context); return; }
            var indexStage = Path.Combine(stage, "mod-index.json");
            await File.WriteAllTextAsync(indexStage, JsonSerializer.Serialize(index), token);
            replacements[InstanceContentService.SafeChild(context.GameDirectory, IndexFile)] = indexStage;
            progress.Report(new(0, .97, "备份并应用 Mod 变更"));
            await Task.Run(() => ApplyAsync(context, replacements, hashes, token), token);
            content.Notify(context);
            progress.Report(new(0, 1, "Mod 安装完成，旧文件已备份"));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static async Task ApplyAsync(InstanceContext context, Dictionary<string, string?> replacements,
        Dictionary<string, string> expectedHashes, CancellationToken token)
    {
        var backup = Path.Combine(context.Root, "97Launch", "backups", "mods", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var changed = new List<ChangedFile>();
        foreach (var pair in replacements)
        {
            token.ThrowIfCancellationRequested();
            InstanceContentService.ValidatePath(context.GameDirectory, pair.Key);
            var relative = Path.GetRelativePath(context.GameDirectory, pair.Key);
            var before = File.Exists(pair.Key) ? await InstanceModificationService.HashAsync(pair.Key, token) : null;
            if (!GameFolderService.PathsEqual(pair.Key, InstanceContentService.SafeChild(context.GameDirectory, IndexFile))
                && before != expectedHashes.GetValueOrDefault(pair.Key))
                throw new IOException("下载期间本地文件发生变化，请刷新后重试：" + Path.GetFileName(pair.Key));
            if (before is not null)
            {
                var save = InstanceContentService.SafeChild(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(save)!); File.Copy(pair.Key, save);
            }
            changed.Add(new(relative, before, pair.Value is null ? null : await InstanceModificationService.HashAsync(pair.Value, token)));
        }
        var journal = new ChangeJournal(backup, changed.ToArray(), false);
        var path = InstanceContentService.SafeChild(context.GameDirectory, JournalFile);
        var previous = File.Exists(path) ? await File.ReadAllTextAsync(path, token) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(Path.Combine(backup, "change.json"), JsonSerializer.Serialize(journal), token);
        await WriteJournalAsync(path, journal, token);
        try
        {
            foreach (var pair in replacements)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                if (pair.Value is null) File.Delete(pair.Key);
                else
                {
                    var temp = pair.Key + ".97launch.tmp";
                    try { File.Copy(pair.Value, temp, true); File.Move(temp, pair.Key, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            await WriteJournalAsync(path, journal with { Completed = true }, CancellationToken.None);
        }
        catch
        {
            Restore(context, journal);
            if (previous is null) File.Delete(path); else await File.WriteAllTextAsync(path, previous, CancellationToken.None);
            throw;
        }
    }

    private static async Task WriteJournalAsync(string path, ChangeJournal journal, CancellationToken token)
    {
        var temp = path + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(journal), token); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task RestoreLastAsync(InstanceContext context, CancellationToken token)
    {
        Serilog.Log.Information("开始回退上次 Mod 变更：{Instance}", context.Instance.Name);
        using var operation = await content.AcquireAsync(context, token);
        var path = InstanceContentService.SafeChild(context.GameDirectory, JournalFile);
        if (!File.Exists(path)) throw new IOException("此游戏目录没有可以回退的 Mod 变更。");
        var journal = JsonSerializer.Deserialize<ChangeJournal>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException("备份记录损坏。");
        if (journal.Completed)
            foreach (var file in journal.Files)
            {
                var target = InstanceContentService.SafeChild(context.GameDirectory, file.Path);
                var current = File.Exists(target) ? await InstanceModificationService.HashAsync(target, token) : null;
                if (current != file.AfterHash) throw new IOException("文件在上次变更后又被修改，请手动核对备份：" + file.Path);
            }
        token.ThrowIfCancellationRequested();
        await WriteJournalAsync(path, journal with { Completed = false }, token);
        await Task.Run(() => Restore(context, journal), CancellationToken.None);
        File.Delete(path);
        content.Notify(context);
        Serilog.Log.Information("上次 Mod 变更已回退：{Instance}", context.Instance.Name);
    }

    private static void Restore(InstanceContext context, ChangeJournal journal)
    {
        InstanceContentService.ValidatePath(Path.Combine(context.Root, "97Launch", "backups", "mods"), journal.Backup);
        // Check every backup before touching the live directory.
        foreach (var file in journal.Files.Where(f => f.BeforeHash is not null))
        {
            var source = InstanceContentService.SafeChild(journal.Backup, file.Path);
            if (!File.Exists(source)) throw new IOException("备份文件缺失：" + file.Path);
            using var input = File.OpenRead(source);
            if (Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant() != file.BeforeHash)
                throw new IOException("备份文件缺失或已损坏：" + file.Path);
        }
        foreach (var file in journal.Files)
        {
            var target = InstanceContentService.SafeChild(context.GameDirectory, file.Path);
            if (file.BeforeHash is null) { if (File.Exists(target)) File.Delete(target); }
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(InstanceContentService.SafeChild(journal.Backup, file.Path), target, true); }
        }
    }
}
