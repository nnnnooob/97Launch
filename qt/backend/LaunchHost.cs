using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.App;
using Launcher.App.Services;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LaunchHost
{
    public static bool Active { get; private set; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly LaunchHistoryStore History = new();
    private static readonly GameSessionManager Sessions = new(NullLogger<GameSessionManager>.Instance, History);
    private static readonly Dictionary<Guid, IDisposable> Leases = [];
    private static CancellationTokenSource? _launch;
    private static Task _work = Task.CompletedTask;
    private static TaskCompletionSource<string>? _selection;
    private static Guid? _starting;
    private static bool _preparing;
    private static readonly object Gate = new();
    private static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";

    public static async Task<int> RunAsync(string pipeName)
    {
        Active = true;
        if (!pipeName.StartsWith("97LaunchQt-launch-", StringComparison.Ordinal) || pipeName.Length > 100) return 1;
        Directory.CreateDirectory(AppInfo.DataDirectory);
        FileStream singleton;
        try { singleton = new FileStream(Path.Combine(AppInfo.DataDirectory, pipeName + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 0; }
        using var ownership = singleton;
        Sessions.SessionsChanged += (_, _) => Snapshot();
        Sessions.GameCrashed += (_, crash) => Program.Send(new { type = "crash", data = crash });
        try
        {
            do
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var idle = new CancellationTokenSource();
                idle.CancelAfter(TimeSpan.FromSeconds(15));
                try { await pipe.WaitForConnectionAsync(idle.Token); }
                catch (OperationCanceledException) { if (Sessions.ActiveSessions.Count == 0 && _work.IsCompleted) return 0; else continue; }
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                Program.LaunchOutput = writer;
                Snapshot();
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (line.Length > 2 * 1024 * 1024) throw new IOException("启动指令过大。");
                        var request = JsonNode.Parse(line) ?? throw new IOException("启动指令无效。");
                        switch (Text(request, "action"))
                        {
                            case "start":
                                if (!_work.IsCompleted) { Program.Send(new { type = "error", error = "启动任务正在运行。" }); Program.Send(new { type = "prepared" }); Snapshot(); break; }
                                _launch?.Dispose(); _launch = new();
                                _preparing = true;
                                _work = Task.Run(() => StartAsync(request, _launch.Token));
                                break;
                            case "stop":
                                if (Guid.TryParse(Text(request, "id"), out var id))
                                { if (_starting == id) _launch?.Cancel(); Sessions.Stop(id); }
                                break;
                            case "stopAll":
                                _launch?.Cancel(); foreach (var session in Sessions.ActiveSessions) Sessions.Stop(session.Id);
                                break;
                            case "cancel": _launch?.Cancel(); break;
                            case "answer": DownloadInteraction.Answer(line); break;
                            case "selection": _selection?.TrySetResult(Text(request, "path")); break;
                            case "snapshot": Snapshot(); break;
                        }
                    }
                }
                catch (IOException) { }
                finally
                {
                    // A running game keeps its log monitor after the launcher closes.
                    Program.LaunchOutput = TextWriter.Null;
                    _launch?.Cancel();
                    await _work;
                }
            } while (Sessions.ActiveSessions.Count > 0);
            return 0;
        }
        finally { Program.LaunchOutput = TextWriter.Null; }
    }

    private static void Snapshot()
    {
        lock (Gate)
        {
            var sessions = Sessions.ActiveSessions;
            foreach (var id in Leases.Keys.Where(id => sessions.All(s => s.Id != id)).ToArray())
            { Leases[id].Dispose(); Leases.Remove(id); }
            Program.Send(new { type = "sessions", data = sessions, starting = _starting, preparing = _preparing });
        }
    }

    private static async Task StartAsync(JsonNode request, CancellationToken token)
    {
        Guid? sessionId = null;
        IDisposable? lease = null;
        var crashed = false;
        try
        {
            var entry = request["instance"] ?? throw new IOException("请选择实例。");
            var path = Path.GetFullPath(Text(entry, "path"));
            var root = Path.GetFullPath(Text(entry, "root"));
            InstanceContentService.ValidatePath(root, path);
            if (!GameFolderService.PathsEqual(Path.GetDirectoryName(path)!, Path.Combine(root, "versions")))
                throw new IOException("实例目录无效。");
            var found = (await new InstanceScanner(NullLogger<InstanceScanner>.Instance).ScanAsync(root, token))
                .SingleOrDefault(i => GameFolderService.PathsEqual(i.Path, path)) ?? throw new IOException("所选实例已不存在。");
            var instance = new InstanceItem(found.Name, found.Path, found.VanillaName, found.LoaderDisplay,
                found.IsModdable, Text(entry, "icon"), found.Kind == InstanceKind.Error ? found.Info : null,
                found.Kind is not (InstanceKind.Snapshot or InstanceKind.Old or InstanceKind.Fool), MinecraftVersionId: found.VanillaName);
            var authorization = new LauncherAuthorizationService();
            using var registration = token.Register(authorization.CancelPending);
            authorization.Changed += (_, _) => { if (!token.IsCancellationRequested && !authorization.IsBusy) Program.Send(new {
                type = "authorization", data = SettingsActions.AuthorizationState(authorization) }); };
            token.ThrowIfCancellationRequested();
            await authorization.InitializeAsync(); token.ThrowIfCancellationRequested(); authorization.RequireAuthorization();
            var settings = new SettingsStore(Path.Combine(AppInfo.DataDirectory, "settings.json"));
            var global = DownloadActions.EffectiveSettings(request, settings);
            global.SelectedGameFolderPath = root; settings.RequestDefaults = global;
            var local = new InstanceLaunchSettingsStore();
            var folders = new GameFolderService { Root = root };
            var accounts = new AccountManager(Text(request, "accountId"));
            CatalogServer? server = null;
            var serverId = Text(request, "serverId");
            if (serverId.Length > 0)
            {
                Program.Send(new { type = "stage", detail = "正在检查服务器状态" });
                server = await new ServerCatalogService(authorization, null!).GetAsync(serverId, token);
                ServerCatalogService.ValidateAddress(server);
                if (!server.CanJoin) throw new IOException("服务器尚未就绪或正在维护，请稍后再试。");
                var mismatch = Version.TryParse(server.Minecraft, out var expected)
                    && Version.TryParse(instance.MinecraftVersionId, out var actual) && expected != actual;
                var required = Loader(server.Loader);
                mismatch |= required.Length > 0 && required != Loader(instance.Loader);
                if (mismatch) await DownloadInteraction.ConfirmAsync("实例可能不兼容",
                    $"当前实例：{instance.Name}\n{instance.MinecraftVersionId} · {instance.Loader}\n\n服务器建议：{server.Minecraft} · {server.Loader}\n仍要启动并加入吗？", "继续启动", token);
                _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Program.Send(new { type = "selectionCheck" });
                var selected = await _selection.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                _selection = null;
                if (!GameFolderService.PathsEqual(selected, path)) throw new IOException("所选实例已发生变化，请重新点击进入服务器。");
            }
            lease = GameDirectoryLease.Acquire(MinecraftLaunchService.ResolveGameDirectory(instance, root, local.Load(path, global), global), false);
            sessionId = Sessions.Begin(instance); _starting = sessionId;
            lock (Gate) { Leases[sessionId.Value] = lease; lease = null; }
            Snapshot();
            var downloads = new PclFileDownloadManager();
            var java = new JavaRuntimeService(settings, folders, downloads, new JavaRuntimeChangeNotifier());
            var launcher = new MinecraftLaunchService(java, accounts, settings, local, folders, downloads,
                Sessions, NullLogger<MinecraftLaunchService>.Instance, authorization);
            var progress = new LaunchReporter(update => { Sessions.Report(sessionId.Value, update); });
            var result = await launcher.StartAsync(instance, progress, sessionId.Value, token, server);
            Program.Send(new { type = "started", data = result, path });
        }
        catch (OperationCanceledException)
        {
            if (sessionId is { } id) { Sessions.Stop(id); History.End(id, LaunchHistoryState.Cancelled); }
            Program.Send(new { type = "canceled" });
        }
        catch (Exception ex)
        {
            crashed = true;
            if (sessionId is { } id) { Sessions.Stop(id); History.End(id, LaunchHistoryState.Failed, "启动未完成，可查看日志了解详情"); }
            Program.Send(new { type = "error", error = LogPrivacy.Sanitize(ex is HttpRequestException
                ? "无法连接启动所需的服务，请检查网络后重试。" : ex.Message) });
        }
        finally
        {
            lease?.Dispose(); _selection = null;
            if (sessionId is { } id) Sessions.EndBeforeProcess(id);
            _starting = null; _preparing = false; Snapshot();
            Program.Send(new { type = "prepared", failed = crashed });
        }
    }
    private static string Loader(string value) => new[] { "neoforge", "fabric", "quilt", "forge" }
        .FirstOrDefault(name => value.Contains(name, StringComparison.OrdinalIgnoreCase)) ?? "";
    private sealed class LaunchReporter(Action<LaunchProgress> report) : IProgress<LaunchProgress>
    { public void Report(LaunchProgress value) => report(value); }
}
