using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;

namespace Launcher.App.Services;

public sealed record ServerPack(string Id, string Name, string Version, long Size, string Sha256,
    string Format, string Minecraft, string Loader, string Filename = "", string DownloadUrl = "",
    string DownloadLease = "", long LeaseExpiresAt = 0);

public sealed record CatalogServer(string Id, string Name, string Description, string Host, int Port,
    string Address, string State, int? Players, int? MaxPlayers, string Minecraft, string Loader,
    string Motd, long? CheckedAt, string IconUrl, ServerPack? Pack, bool CanJoin, bool HasRelatedInstance, int Order);

public sealed record ServerDirectory(CatalogServer[] Items, long CheckedAt, bool Stale, int RefreshAfter);

public sealed class ServerCatalogService(LauncherAuthorizationService authorization, LocalModpackService installer)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static string DefaultIcon => Path.Combine(AppContext.BaseDirectory, "Assets", "Servers", "default-server.png");
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public Task<ServerDirectory> ListAsync(CancellationToken token) => ReadAsync<ServerDirectory>("api/v1/servers", HttpMethod.Get, token);
    public Task<CatalogServer> GetAsync(string id, CancellationToken token) =>
        ReadAsync<CatalogServer>($"api/v1/servers/{ValidId(id)}", HttpMethod.Get, token);
    public async Task<ServerPack> GetPackAsync(string id, CancellationToken token)
    {
        var pack = await ReadAsync<ServerPack>($"api/v1/servers/{ValidId(id)}/pack", HttpMethod.Post, token);
        if (!Guid.TryParse(pack.Id, out _) || !Regex.IsMatch(pack.Sha256 ?? "", "^[0-9a-f]{64}$")
            || pack.Size is <= 0 or > 32L * 1024 * 1024 * 1024 || pack.Format is not ("modrinth" or "curseforge")
            || pack.DownloadUrl != $"/api/v1/packs/{pack.Id}/download"
            || !Regex.IsMatch(pack.DownloadLease ?? "", "^[A-Za-z0-9_-]{43}$"))
            throw new InvalidDataException("服务器返回的整合包信息不完整。");
        return pack;
    }

    private static string ValidId(string id) => Guid.TryParse(id, out var value)
        ? value.ToString() : throw new InvalidDataException("服务器标识无效。");

    private async Task<T> ReadAsync<T>(string path, HttpMethod method, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await authorization.SendAuthorizedAsync(method, path, deadline.Token);
        await EnsureSuccessAsync(response, deadline.Token);
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var data = new MemoryStream();
        var buffer = new byte[16384];
        int length;
        while ((length = await input.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (data.Length + length > 2 * 1024 * 1024) throw new InvalidDataException("服务器目录数据过大。");
            data.Write(buffer, 0, length);
        }
        return JsonSerializer.Deserialize<T>(data.ToArray(), Json) ?? throw new InvalidDataException("服务器响应为空。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "下载通道繁忙，请稍后重试。",
            HttpStatusCode.NotFound => "服务器已下架，或暂无相关实例。",
            HttpStatusCode.Forbidden => "下载凭证已过期或无访问权限，请重新获取相关实例。",
            _ => "服务器暂时不可用，请稍后刷新。"
        };
        if (response.Content.Headers.ContentLength is <= 16384)
        {
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var text = error.RootElement.GetProperty("error").GetProperty("message").GetString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length < 250) message = text;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        throw new IOException(message);
    }

    public async Task<string> IconAsync(CatalogServer server, CancellationToken token)
    {
        var path = Path.Combine(AppInfo.DataDirectory, "cache", "server-icons", ValidId(server.Id) + ".png");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(5)) return path;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await authorization.SendAuthorizedAsync(HttpMethod.Get, $"api/v1/servers/{ValidId(server.Id)}/icon", deadline.Token);
            await EnsureSuccessAsync(response, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (data.Length + read > 200 * 1024) return DefaultIcon;
                data.Write(buffer, 0, read);
            }
            var png = data.ToArray();
            if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16,4)) != 64
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20,4)) != 64) return DefaultIcon;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".tmp", png, deadline.Token);
            File.Move(path + ".tmp", path, true);
            return path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        { return DefaultIcon; }
    }

    public static async Task<int?> LatencyAsync(CatalogServer server, CancellationToken token)
    {
        if (!server.CanJoin) return null;
        try
        {
            ValidateAddress(server);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(server.Host, server.Port, timeout.Token);
            return (int)Math.Min(9999, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidDataException) { return null; }
    }

    internal static void ValidateAddress(CatalogServer server)
    {
        if (server.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(server.Host) || server.Host.Length > 253
            || Uri.CheckHostName(server.Host) == UriHostNameType.Unknown || server.Host.Any(char.IsWhiteSpace))
            throw new InvalidDataException("服务器连接地址无效，请联系管理员。");
    }

    public async Task InstallAsync(string serverId, ServerPack approvedPack, string root, string name,
        IProgress<InstallProgress> progress, CancellationToken token)
    {
        await DownloadGate.WaitAsync(token);
        try
        {
            var pack = approvedPack;
            Serilog.Log.Information("准备服务器整合包：{Pack} / {Version}，目标实例：{Instance}，大小 {Size} 字节", pack.Name, pack.Version, name, pack.Size);
            var cache = Path.Combine(root, "97Launch", "cache", "server-packs");
            Directory.CreateDirectory(cache);
            var archive = Path.Combine(cache, pack.Sha256 + (pack.Format == "modrinth" ? ".mrpack" : ".zip"));
            var partial = archive + ".part";
            if (!await IsValidAsync(archive, pack, token))
            {
                // The queued version is immutable. Refresh an expired lease only if the administrator still associates that exact version.
                if (pack.LeaseExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30)
                {
                    var refreshed = await GetPackAsync(serverId, token);
                    if (refreshed.Id != pack.Id || refreshed.Sha256 != pack.Sha256)
                        throw new IOException("关联整合包已更新，请重新获取相关实例后安装。");
                    pack = refreshed;
                }
                await DownloadAsync(pack, partial, progress, token);
                progress.Report(new(0, 1, "正在校验整合包 SHA-256"));
                if (!await IsValidAsync(partial, pack, token))
                {
                    File.Delete(partial);
                    throw new InvalidDataException("整合包校验不通过，已移除不完整文件，请重试。");
                }
                File.Move(partial, archive, true);
                Serilog.Log.Information("服务器整合包下载及 SHA-256 校验完成：{Pack} / {Version}", pack.Name, pack.Version);
            }
            else Serilog.Log.Information("服务器整合包已命中校验通过的本地缓存：{Pack} / {Version}", pack.Name, pack.Version);
            await installer.InstallAsync(archive, root, name, new PackProgress(progress), token);
        }
        finally { DownloadGate.Release(); }
    }

    private async Task DownloadAsync(ServerPack pack, string path, IProgress<InstallProgress> progress, CancellationToken token)
    {
        if (File.Exists(path) && new FileInfo(path).Length > pack.Size) File.Delete(path);
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset > 0) Serilog.Log.Information("继续下载服务器整合包：{Pack}，已完成 {Offset} / {Size} 字节", pack.Name, offset, pack.Size);
        if (offset == pack.Size) return;
        for (var attempt = 0; ; attempt++)
        {
            authorization.RequireAuthorization();
            using var response = await authorization.SendAuthorizedAsync(HttpMethod.Get, pack.DownloadUrl, token, request =>
            {
                request.Headers.Add("X-Download-Lease", pack.DownloadLease);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue('"' + pack.Sha256 + '"'));
                }
            });
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 20)
            {
                var seconds = Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 15, 1, 60);
                progress.Report(new(0, offset / (double)pack.Size, $"下载通道繁忙，{seconds:0} 秒后重试"));
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
                continue;
            }
            await EnsureSuccessAsync(response, token);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange is not { From: { } start, Length: { } total } || start != offset || total != pack.Size)
                    throw new InvalidDataException("服务器返回的续传区间不正确。");
            }
            else offset = 0;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
            var timer = Stopwatch.StartNew();
            long previous = offset;
            var buffer = new byte[65536];
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                var read = await input.ReadAsync(buffer, deadline.Token);
                if (read == 0) break;
                if (offset + read > pack.Size) throw new InvalidDataException("整合包响应超出声明的大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                offset += read;
                if (timer.ElapsedMilliseconds >= 300)
                {
                    progress.Report(new(0, offset / (double)pack.Size, $"下载客户端整合包 · {offset / 1048576d:0.0} / {pack.Size / 1048576d:0.0} MiB",
                        (long)((offset - previous) / timer.Elapsed.TotalSeconds)));
                    previous = offset; timer.Restart();
                }
            }
            await output.FlushAsync(token);
            if (offset != pack.Size) throw new IOException("整合包下载中断，可在传输页面重试续传。");
            return;
        }
    }

    private static async Task<bool> IsValidAsync(string path, ServerPack pack, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != pack.Size) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PackProgress(IProgress<InstallProgress> target) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress p) => target.Report(p with { Stage = p.Stage + 1 });
    }
}
