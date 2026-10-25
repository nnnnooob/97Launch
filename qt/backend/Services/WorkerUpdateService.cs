using System.Diagnostics;
using System.Net;
using System.Text.Json;
namespace Launcher.App.Services;
internal sealed class WorkerUpdateService(LauncherAuthorizationService authorization, SettingsStore settings) {
    private readonly LauncherAuthorizationService _authorization=authorization;
    private readonly SettingsStore _settings=settings;
    private sealed record Preferences(string Channel="preview",bool CheckOnStartup=true);
    private Preferences _preferences=new();
    private static string ReadyPath=>Path.Combine(UpdateTrust.Root,"ready.json");
    private static string LatestPath=>Path.Combine(UpdateTrust.Root,"latest.json");
    private static string PreferencesPath=>Path.Combine(UpdateTrust.Root,"preferences.json");
    public bool Busy, Downloading;
    public double Progress;
    public string Status="尚未检查更新",Detail="";
    public UpdateRelease? Latest,Ready;
    private void Notify() { }
    public async Task LoadAsync(CancellationToken token,bool startup=false,bool waitForResult=false) {
        try { if(File.Exists(PreferencesPath)) _preferences=JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(PreferencesPath,token),UpdateTrust.Json)??new(); }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) { Detail="无法读取更新偏好，暂用测试版通道。"; }
        if(_preferences.Channel is not ("preview" or "stable"))_preferences=new();
        try {
            if(File.Exists(LatestPath)){
                Latest=JsonSerializer.Deserialize<UpdateRelease>(await File.ReadAllTextAsync(LatestPath,token),UpdateTrust.Json);
                if(Latest is not null){UpdateTrust.Verify(Latest);if(Latest.Build<=AppInfo.BuildNumber||(_preferences.Channel=="stable"&&Latest.Channel!="stable"))Latest=null;}
            }
            if(File.Exists(ReadyPath)){
                var ready=JsonSerializer.Deserialize<UpdateRelease>(await File.ReadAllTextAsync(ReadyPath,token),UpdateTrust.Json);
                if(ready is not null&&ready.Build>AppInfo.BuildNumber&&(_preferences.Channel=="preview"||ready.Channel=="stable")) {await UpdateTrust.VerifyFileAsync(ready,UpdateTrust.FilePath(ready),token);Ready=Latest=ready;Status="更新已下载，等待重启";}
                else File.Delete(ReadyPath);
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){Ready=Latest=null;Status="更新缓存不可用，请重新检查";Detail=ex.Message;}
        if(waitForResult){
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(!File.Exists(UpdateBootstrap.ResultPath)&&DateTime.UtcNow<deadline)await Task.Delay(200,token);
        }
        if(File.Exists(UpdateBootstrap.ResultPath)) {
            try {
                var result=JsonSerializer.Deserialize<UpdateResult>(await File.ReadAllTextAsync(UpdateBootstrap.ResultPath,token),UpdateTrust.Json);
                if(result is not null){Status=result.Success?"更新成功":"已恢复上一版本";Detail=result.Message;}
                if(startup)File.Delete(UpdateBootstrap.ResultPath);
            }catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) { Detail="无法读取上次更新结果。"; }
        }
        if(startup)Cleanup();
    }
    public object Snapshot()=>new {status=Status,detail=Detail,latest=Latest,ready=Ready is not null,channel=_preferences.Channel,startupCheck=_preferences.CheckOnStartup,busy=false,downloading=false,progress=Progress,currentVersion=AppInfo.Version,currentBuild=AppInfo.BuildNumber,error=""};
    public void Configure(string channel,bool startup){
        if(channel is not ("preview" or "stable"))throw new ArgumentException("更新通道无效。");
        if(channel!=_preferences.Channel){Latest=Ready=null;File.Delete(ReadyPath);File.Delete(LatestPath);Status="更新通道已切换，请重新检查";}
        _preferences=new(channel,startup);UpdateTrust.Save(PreferencesPath,_preferences);
    }
    public async Task CheckAsync(CancellationToken token,bool silent=false){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response=await _authorization.SendUpdateRequestAsync($"/api/v1/updates/check?channel={_preferences.Channel}&architecture={UpdateTrust.Architecture}&build={AppInfo.BuildNumber}",timeout.Token);
        response.EnsureSuccessStatusCode();var check=await ReadAsync<UpdateCheck>(response,timeout.Token);
        if(check.Release is not null)UpdateTrust.Verify(check.Release);
        Latest=check.Available?check.Release:null;
        if(Latest is not null&&silent&&File.Exists(UpdateBootstrap.FailedBuildPath)&&
            JsonSerializer.Deserialize<int>(await File.ReadAllTextAsync(UpdateBootstrap.FailedBuildPath,token))==Latest.Build){
            Latest=null;File.Delete(LatestPath);Status="已跳过上次启动失败的构建";Detail="可以手动检查后重试，或等待后续版本。";return;
        }
        if(Latest is not null)UpdateTrust.Save(LatestPath,Latest);else File.Delete(LatestPath);
        if(Ready is not null&&Ready.Id!=check.Release?.Id){Ready=null;File.Delete(ReadyPath);}
        Status=Ready is not null?"更新已下载，等待重启":Latest is not null?"发现新版本":check.Release is null?"当前通道尚未发布版本":"已是当前通道的最新版本";
        Detail=Latest is {} release?$"v{release.Version} · 构建 {release.Build} · {release.Size/1048576d:0.0} MiB":"检查时间："+DateTime.Now.ToString("HH:mm:ss");
    }
    public async Task ApplyAsync(int parentId,CancellationToken token){
        var release=Ready??throw new IOException("尚无校验通过的更新，请先下载更新。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(25));
        release=await PublishedAsync(release,timeout.Token);
        if(release.Build<=AppInfo.BuildNumber)throw new InvalidOperationException("此更新不高于当前构建，请重新检查。");
        UpdateBootstrap.HostParentId=int.TryParse(Environment.GetEnvironmentVariable("LAUNCH_PORTABLE_PID"),out var portablePid)?portablePid:parentId;
        await UpdateBootstrap.PrepareAsync(release);
        UpdateBootstrap.CommitPreparedUpdate();
    }
    public async Task DownloadAsync(UpdateRelease release, IProgress<InstallProgress> progress, CancellationToken token)
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
                    var limit = _settings.Load().DownloadSpeedLimitKb;
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > 32768) throw new InvalidDataException("更新元数据过大。");
        using var body = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[4096];
        while (true) { var n = await stream.ReadAsync(buffer, token); if (n == 0) break; if (body.Length + n > 32768) throw new InvalidDataException("更新元数据过大。"); body.Write(buffer, 0, n); }
        return JsonSerializer.Deserialize<T>(body.ToArray(), UpdateTrust.Json) ?? throw new InvalidDataException("更新元数据为空。");
    }

    private static void Cleanup(){
        try{
            var sessions=Path.Combine(UpdateTrust.Root,"sessions");
            if(!Directory.Exists(sessions))return;
            foreach(var directory in new DirectoryInfo(sessions).GetDirectories().OrderByDescending(d=>d.LastWriteTimeUtc).Skip(2)){
                if(!Guid.TryParseExact(directory.Name,"N",out _)||directory.Attributes.HasFlag(FileAttributes.ReparsePoint)||directory.LastWriteTimeUtc>DateTime.UtcNow.AddDays(-1))continue;
                foreach(var name in new[]{"plan.json","ready","helper-ready","commit","previous.exe","97Launch-update.exe"})File.Delete(Path.Combine(directory.FullName,name));
                if(!Directory.EnumerateFileSystemEntries(directory.FullName).Any())directory.Delete();
            }
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }

}
