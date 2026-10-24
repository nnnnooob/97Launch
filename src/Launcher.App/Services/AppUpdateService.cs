using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App.Services;

public sealed class AppUpdateService
{
    private sealed record Preferences(string Channel = "preview", bool CheckOnStartup = true);
    private readonly LauncherAuthorizationService _authorization;
    private readonly ToastService _toasts;
    private Preferences _preferences = new();
    private CancellationTokenSource? _inlineCancellation;
    private TransferJob? _job;
    internal static bool RestartPending { get; private set; }
    public event EventHandler? Changed;
    public UpdateRelease? Latest { get; private set; }
    public UpdateRelease? Ready { get; private set; }
    public bool Busy { get; private set; }
    public bool Downloading { get; private set; }
    public double Progress { get; private set; }
    public string Status { get; private set; } = "尚未检查更新";
    public string Detail { get; private set; } = "启动时自动检查，也可以手动检查更新。";
    public DateTimeOffset? LastChecked { get; private set; }
    public string Channel => _preferences.Channel;
    public bool CheckOnStartup => _preferences.CheckOnStartup;
    public bool HasUpdate => Latest is not null && Latest.Build > AppInfo.BuildNumber;
    private static string ReadyPath => Path.Combine(UpdateTrust.Root, "ready.json");
    private static string PreferencesPath => Path.Combine(UpdateTrust.Root, "preferences.json");

    public AppUpdateService(LauncherAuthorizationService authorization, ToastService toasts)
    {
        _authorization = authorization; _toasts = toasts;
        try { if (File.Exists(PreferencesPath)) _preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath), UpdateTrust.Json) ?? new(); }
        catch (Exception ex) { Serilog.Log.Warning("读取更新设置失败：{ErrorType}", ex.GetType().Name); }
        if (_preferences.Channel is not ("preview" or "stable")) _preferences = new();
    }

    public void Configure(string channel, bool checkOnStartup)
    {
        if (Busy) return;
        if (channel is not ("preview" or "stable")) throw new ArgumentException("更新通道无效。");
        var changedChannel = channel != Channel;
        _preferences = new(channel, checkOnStartup);
        UpdateTrust.Save(PreferencesPath, _preferences);
        if (changedChannel) { Latest = null; Ready = null; File.Delete(ReadyPath); Status = "更新通道已切换，请重新检查"; Detail = ""; }
        Notify();
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(UpdateTrust.Root);
        try
        {
            if (File.Exists(ReadyPath))
            {
                var release = JsonSerializer.Deserialize<UpdateRelease>(await File.ReadAllTextAsync(ReadyPath), UpdateTrust.Json)!;
                if (release.Build > AppInfo.BuildNumber && (Channel == "preview" || release.Channel == "stable"))
                {
                    await UpdateTrust.VerifyFileAsync(release, UpdateTrust.FilePath(release));
                    Ready = Latest = release; Status = "更新已下载，等待重启"; Detail = $"v{release.Version} · 构建 {release.Build}";
                }
                else File.Delete(ReadyPath);
            }
        }
        catch (Exception ex) { File.Delete(ReadyPath); Serilog.Log.Warning("已下载更新不可用：{ErrorType}", ex.GetType().Name); }
        if (UpdateBootstrap.StartupTicket is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(UpdateBootstrap.ResultPath) && DateTime.UtcNow < deadline) await Task.Delay(200);
        }
        try
        {
            if (File.Exists(UpdateBootstrap.ResultPath))
            {
                var result = JsonSerializer.Deserialize<UpdateResult>(await File.ReadAllTextAsync(UpdateBootstrap.ResultPath), UpdateTrust.Json)!;
                _toasts.Show(result.Success ? "应用已更新" : "更新未完成", result.Success ? "当前版本：v" + result.Version : result.Message);
                Status = result.Success ? "更新成功" : "已恢复上一版本"; Detail = result.Message;
                File.Delete(UpdateBootstrap.ResultPath);
            }
        }
        catch (Exception ex) { Serilog.Log.Warning("读取更新结果失败：{ErrorType}", ex.GetType().Name); }
        Cleanup(); Notify();
        if (CheckOnStartup) await CheckAsync(silent: true);
    }

    public async Task CheckAsync(bool silent = false)
    {
        if (Busy) return;
        Busy = true; Status = "正在检查更新"; Notify();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var response = await _authorization.SendUpdateRequestAsync($"/api/v1/updates/check?channel={Channel}&architecture={UpdateTrust.Architecture}&build={AppInfo.BuildNumber}", timeout.Token);
            response.EnsureSuccessStatusCode();
            var check = await ReadAsync<UpdateCheck>(response, timeout.Token);
            if (check.Release is { } latest) UpdateTrust.Verify(latest);
            Latest = check.Available ? check.Release : null;
            if (Latest is not null && silent && File.Exists(UpdateBootstrap.FailedBuildPath) &&
                JsonSerializer.Deserialize<int>(await File.ReadAllTextAsync(UpdateBootstrap.FailedBuildPath)) == Latest.Build)
            { Latest = null; Status = "已跳过上次启动失败的构建"; Detail = "可以手动检查后重试，或等待后续版本。"; return; }
            if (Ready is not null && Ready.Id != check.Release?.Id) { Ready = null; File.Delete(ReadyPath); }
            LastChecked = DateTimeOffset.Now;
            Status = Ready is not null ? "更新已下载，等待重启" : Latest is not null ? "发现新版本" : check.Release is null ? "当前通道尚未发布版本" : "已是当前通道的最新版本";
            Detail = Latest is { } r ? $"v{r.Version} · 构建 {r.Build} · {r.Size / 1048576d:0.0} MiB" : "检查时间：" + LastChecked.Value.ToString("HH:mm:ss");
            Serilog.Log.Information("检查应用更新完成：{Status}，当前构建 {Build}", Status, AppInfo.BuildNumber);
            if (silent && Latest is not null && Ready is null) _toasts.Show("发现新版 97Launch", $"v{Latest.Version}，可在设置 → 软件更新查看。");
        }
        catch (Exception ex)
        {
            Status = "暂时无法检查更新"; Detail = "请检查网络后重试；当前版本可以继续使用。";
            Serilog.Log.Warning("检查应用更新失败：{ErrorType}", ex.GetType().Name);
        }
        finally { Busy = false; Notify(); }
    }

    public async Task StartDownloadAsync()
    {
        if (Busy || Latest is not { } release) return;
        UpdateTrust.Verify(release);
        Busy = Downloading = true; Progress = 0; Status = "等待下载更新"; Notify();
        if (_authorization.IsAuthorized)
        {
            try
            {
                var transfer = App.Services.GetRequiredService<TransferViewModel>();
                _job = transfer.EnqueueOperation("更新 97Launch · " + release.Version, release.Version, UpdateTrust.Root,
                    ["下载新版", "校验新版"], (progress, token) => DownloadAsync(release, progress, token), applicationUpdate: true);
                _job.PropertyChanged += (sender, e) =>
                {
                    if (e.PropertyName == nameof(TransferJob.State) && sender is TransferJob job && ReferenceEquals(job, _job) &&
                        job.State is TransferJobState.Canceled or TransferJobState.Failed)
                    { Busy = Downloading = false; Status = job.State == TransferJobState.Canceled ? "更新下载已取消，可继续下载" : "更新下载失败，可重试"; Notify(); }
                };
                App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Transfer);
            }
            catch { Busy = Downloading = false; Notify(); throw; }
        }
        else
        {
            _inlineCancellation = new();
            try { await DownloadAsync(release, new Progress<InstallProgress>(), _inlineCancellation.Token); }
            catch (OperationCanceledException) { Status = "更新下载已取消，可继续下载"; }
            catch (Exception ex) { Status = "更新下载失败"; Detail = ex.Message; }
            finally { _inlineCancellation.Dispose(); _inlineCancellation = null; Busy = Downloading = false; Notify(); }
        }
    }

    public void CancelDownload()
    {
        _inlineCancellation?.Cancel();
        if (_job is not null) App.Services.GetRequiredService<TransferViewModel>().CancelJob(_job);
    }

    private async Task DownloadAsync(UpdateRelease release, IProgress<InstallProgress> progress, CancellationToken token)
    {
        Busy = Downloading = true; Ready = null; Status = "正在下载更新"; Notify();
        try
        {
            release = await PublishedAsync(release, token);
            var artifact = UpdateTrust.Verify(release);
            var path = UpdateTrust.FilePath(release);
            var part = path + ".part";
            Directory.CreateDirectory(UpdateTrust.Root);
            if (File.Exists(part) && new FileInfo(part).Length > release.Size) File.Delete(part);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (offset == release.Size) break;
                    using var response = await _authorization.SendUpdateRequestAsync(release.DownloadUrl, token, offset, '"' + artifact.Sha256 + '"');
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.From != offset || range.Length != release.Size) throw new InvalidDataException("更新续传范围无效。");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                    else throw new InvalidDataException("更新下载响应无效。");
                    if (response.Content.Headers.ContentLength != release.Size - offset) throw new InvalidDataException("更新下载长度不符。");
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 65536, true);
                    var watch = Stopwatch.StartNew(); var received = offset; var initial = offset;
                    var limit = App.Services.GetRequiredService<SettingsStore>().Load().DownloadSpeedLimitKb;
                    var buffer = new byte[65536]; var lastReport = 0L;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(45), token);
                        if (read == 0) break;
                        if (received + read > release.Size) throw new InvalidDataException("更新文件超出声明大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), token); received += read;
                        if (limit > 0)
                        {
                            var delay = (received - initial) * 1000d / (limit * 1024d) - watch.ElapsedMilliseconds;
                            if (delay > 1) await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
                        }
                        if (watch.ElapsedMilliseconds - lastReport >= 150 || received == release.Size)
                        {
                            lastReport = watch.ElapsedMilliseconds;
                            Progress = received * 100d / release.Size;
                            Detail = $"{received / 1048576d:0.0} / {release.Size / 1048576d:0.0} MiB";
                            progress.Report(new(0, received / (double)release.Size, Detail, (long)((received - initial) / Math.Max(.1, watch.Elapsed.TotalSeconds)))); Notify();
                        }
                    }
                    await output.FlushAsync(token);
                    if (received != release.Size) throw new IOException("更新下载中断，将继续未完成的部分。");
                    break;
                }
                catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested && ex is HttpRequestException or IOException or TimeoutException)
                { await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 2), token); }
            }
            Status = "正在校验更新"; progress.Report(new(1, 0, "校验签名、哈希与程序版本")); Notify();
            try { await UpdateTrust.VerifyFileAsync(release, part, token); }
            catch (InvalidDataException) { File.Delete(part); throw; }
            release = await PublishedAsync(release, token);
            File.Move(part, path, true); UpdateTrust.Save(ReadyPath, release);
            Ready = Latest = release; Status = "更新已下载，等待重启"; Detail = $"v{release.Version} · 构建 {release.Build}";
            progress.Report(new(1, 1, "校验通过，点击重启并更新"));
        }
        finally { Busy = Downloading = false; Notify(); }
    }

    private async Task<UpdateRelease> PublishedAsync(UpdateRelease release, CancellationToken token)
    {
        using var response = await _authorization.SendUpdateRequestAsync("/api/v1/updates/" + release.Id, token);
        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) throw new InvalidOperationException("该版本已撤回，请重新检查更新。");
        response.EnsureSuccessStatusCode();
        var current = await ReadAsync<UpdateRelease>(response, token); UpdateTrust.Verify(current);
        if (current.Id != release.Id || current.Manifest != release.Manifest) throw new InvalidDataException("发布清单发生变化，请重新检查更新。");
        return current;
    }

    public async Task ApplyAsync()
    {
        if (Busy || Ready is not { } release) return;
        if (App.Services.GetRequiredService<GameSessionManager>().ActiveSessions.Count > 0 ||
            App.Services.GetRequiredService<TransferViewModel>().RemainingTaskCount > 0)
            throw new InvalidOperationException("游戏或传输任务仍在运行，请完成后再重启更新。");
        Busy = true; RestartPending = true; Status = "正在准备重启更新"; Notify();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            release = await PublishedAsync(release, timeout.Token);
            if (release.Build <= AppInfo.BuildNumber) throw new InvalidOperationException("此更新不高于当前构建，请重新检查。");
            await UpdateBootstrap.PrepareAsync(release);
            if (App.Services.GetRequiredService<GameSessionManager>().ActiveSessions.Count > 0 ||
                App.Services.GetRequiredService<TransferViewModel>().RemainingTaskCount > 0)
                throw new InvalidOperationException("仍有运行中的游戏或任务，请稍后更新。");
            UpdateBootstrap.CommitPreparedUpdate();
            App.RequestExitForUpdate();
        }
        finally { RestartPending = false; Busy = false; Notify(); }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > 32768) throw new InvalidDataException("更新元数据过大。");
        using var body = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[4096];
        while (true) { var n = await stream.ReadAsync(buffer, token); if (n == 0) break; if (body.Length + n > 32768) throw new InvalidDataException("更新元数据过大。"); body.Write(buffer, 0, n); }
        return JsonSerializer.Deserialize<T>(body.ToArray(), UpdateTrust.Json) ?? throw new InvalidDataException("更新元数据为空。");
    }

    private static void Cleanup()
    {
        try
        {
            var sessions = Path.Combine(UpdateTrust.Root, "sessions");
            if (!Directory.Exists(sessions)) return;
            foreach (var dir in new DirectoryInfo(sessions).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(2))
            {
                if (!Guid.TryParseExact(dir.Name, "N", out _) || dir.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    dir.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
                foreach (var name in new[] { "plan.json", "ready", "helper-ready", "commit", "previous.exe", "97Launch-update.exe" }) File.Delete(Path.Combine(dir.FullName, name));
                if (!Directory.EnumerateFileSystemEntries(dir.FullName).Any()) dir.Delete();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
