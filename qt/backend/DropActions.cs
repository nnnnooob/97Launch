using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Launcher.App;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;

internal sealed record DropChoice(string Choice, bool ApplyAll);

internal static class DropInteraction
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<DropChoice>> Pending = new();
    private static int _nextId;

    internal static async Task<DropChoice> ChooseAsync(string message, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<DropChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = completion;
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        Program.Send(new { type = "question", id, title = "存在同名 Mod", message });
        try { return await completion.Task; }
        finally { Pending.TryRemove(id, out _); }
    }

    internal static void Answer(string command)
    {
        try
        {
            var value = JsonNode.Parse(command)!;
            if (Pending.TryGetValue(value["id"]!.GetValue<int>(), out var completion))
            {
                var choice = value["choice"]?.GetValue<string>() ?? "cancel";
                completion.TrySetResult(new(choice is "replace" or "skip" ? choice : "cancel", value["applyAll"]?.GetValue<bool>() ?? false));
            }
        }
        catch (System.Text.Json.JsonException) { }
    }
}

internal static class DropActions
{
    private static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";

    internal static async Task<object> RunAsync(JsonNode request, CancellationToken token)
    {
        var args = request["args"]!;
        var paths = args["sources"]!.AsArray().Select(v => v!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var errors = new List<string>();
        var packs = new List<object>();
        var files = new List<DroppedFile>();
        var root = Text(args, "root");
        var settings = new SettingsStore(Path.Combine(AppInfo.DataDirectory, "settings.json"));
        var global = DownloadActions.EffectiveSettings(request, settings);
        settings.RequestDefaults = global;
        var local = new InstanceLaunchSettingsStore();
        var content = new InstanceContentService(settings, local, new GameSessionManager());
        InstanceContext? context = null;
        string? contextError = null;
        try
        {
            var entry = request["instance"];
            if (!string.IsNullOrEmpty(Text(entry, "path")))
            {
                var path = Path.GetFullPath(Text(entry, "path"));
                var gameRoot = Path.GetFullPath(Text(entry, "root"));
                InstanceContentService.ValidatePath(gameRoot, path);
                if (!GameFolderService.PathsEqual(Path.GetDirectoryName(path)!, Path.Combine(gameRoot, "versions")) || !Directory.Exists(path))
                    throw new IOException("目标实例已不存在，请刷新实例列表。");
                var loader = Text(entry, "loaderId");
                var instance = new InstanceItem(Text(entry, "name"), path, Text(entry, "version"), loader,
                    loader is "Fabric" or "Forge" or "NeoForge" or "LiteLoader" or "Quilt", "",
                    IsRelease: Text(entry, "releaseType") == "release", MinecraftVersionId: Text(entry, "version"));
                var directory = Path.GetFullPath(Text(args, "gameDirectory"));
                if (!GameFolderService.PathsEqual(directory, path) && !GameFolderService.PathsEqual(directory, gameRoot))
                    throw new IOException("实例资源目录无效。");
                context = new(instance, gameRoot, directory);
            }
        }
        catch (Exception ex) { contextError = ex.Message; }

        Program.Send(new { type = "partial", data = new { phase = "recognizing", count = paths.Length } });
        var iconCache = Path.Combine(Path.GetTempPath(), "97Launch", "drop-icons");
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try { files.Add(DroppedFileReader.Read(path, iconCache)); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
        }
        Program.Send(new { type = "partial", data = new { phase = "recognized" } });

        var modCount = 0;
        var skipped = 0;
        bool? replaceRemaining = null;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            token.ThrowIfCancellationRequested();
            try
            {
                if (file.IsModpack)
                {
                    if (string.IsNullOrWhiteSpace(root)) throw new IOException("请先选择游戏文件夹。");
                    packs.Add(new { path = file.Path, name = file.Name });
                    continue;
                }
                if (context is null) throw new IOException(contextError ?? "请先选择目标实例。");
                if (!context.Instance.IsModdable) throw new IOException("目标实例没有 Mod 加载器，请先安装兼容的加载器。");
                var mod = file.Mod!;
                if (mod.ModLoaders is { Count: > 0 } loaders && !loaders.Contains(context.Instance.Loader, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("加载器不兼容，需要 " + string.Join(" / ", loaders));
                if (mod.GameRequirements is { Count: > 0 } requirements
                    && requirements.All(r => ModDiagnostics.Matches(context.Instance.MinecraftVersionId, r) == false))
                    throw new IOException("游戏版本不兼容，需要 " + string.Join(" 或 ", requirements));
                content.EnsureWritable(context);
                var target = InstanceContentService.SafeChild(context.DirectoryFor(ContentKind.Mods), LocalContentReader.EnabledName(Path.GetFileName(file.Path)));
                var overwrite = false;
                if (File.Exists(target))
                {
                    if (GameFolderService.PathsEqual(file.Path, target)
                        || new FileInfo(file.Path).Length == new FileInfo(target).Length
                        && await InstanceModificationService.HashAsync(file.Path, token) == await InstanceModificationService.HashAsync(target, token))
                    { skipped++; continue; }
                    if (replaceRemaining is null)
                    {
                        var choice = await DropInteraction.ChooseAsync($"「{context.Instance.Name}」已存在 {Path.GetFileName(target)}。替换前会将旧文件移到回收站。", token);
                        if (choice.Choice == "cancel") { skipped += files.Count - index; break; }
                        overwrite = choice.Choice == "replace";
                        if (choice.ApplyAll) replaceRemaining = overwrite;
                    }
                    else overwrite = replaceRemaining.Value;
                    if (!overwrite) { skipped++; continue; }
                }
                await content.ImportAsync(context, ContentKind.Mods, [file.Path], overwrite, token);
                modCount++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file.Path)}：{ex.Message}"); }
        }
        foreach (var error in errors) Serilog.Log.Warning("拖拽导入：{Error}", error);
        return new { modCount, skipped, errors, packs, root, instanceName = context?.Instance.Name, shared = context?.Shared ?? false };
    }
}
