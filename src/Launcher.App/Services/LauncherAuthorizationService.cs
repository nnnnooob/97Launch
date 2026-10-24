using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.App.Services;

public enum LauncherAuthorizationStatus { Unpaired, Checking, Authorized, Offline, NetworkRequired, Revoked }

public sealed record AuthorizedDevice(string Id, string QqId, string Nickname, string GroupId,
    string Name, string Platform, string AppVersion, long CreatedAt, long LastSeenAt);

// These credentials authorize this launcher, independently of Minecraft accounts.
public sealed class LauncherAuthorizationService
{
    private sealed record SavedAuthorization(string Server, string DeviceToken, AuthorizedDevice Device, DateTimeOffset VerifiedAt);
    private sealed record RedeemResponse(string DeviceToken, AuthorizedDevice Device);
    private sealed record DeviceResponse(bool Authorized, AuthorizedDevice Device);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string _path = Path.Combine(AppInfo.DataDirectory, "authorization.dat");
    private readonly HttpClient _http;
    private bool _usePrivateCa;
    private SavedAuthorization? _saved;
    private CancellationTokenSource? _operation;
    private int _generation;
    private bool _initialized;
    private Uri? _server;

    public LauncherAuthorizationService() => _http = CreateHttpClient();

    public event EventHandler? Changed;
    public LauncherAuthorizationStatus Status { get; private set; } = LauncherAuthorizationStatus.Unpaired;
    public bool IsAuthorized => Status is LauncherAuthorizationStatus.Authorized or LauncherAuthorizationStatus.Offline;
    public bool IsBusy { get; private set; }
    public bool HasCredential => _saved is not null;
    public AuthorizedDevice? Device => _saved?.Device;
    public DateTimeOffset? LastVerifiedAt => _saved?.VerifiedAt;
    public string Message { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var configPath = Path.Combine(AppInfo.DataDirectory, "authorization-server.json");
            if (!File.Exists(configPath))
                configPath = Path.Combine(AppContext.BaseDirectory, "authorization-server.json");
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var address = config.RootElement.GetProperty("base_url").GetString();
            _usePrivateCa = config.RootElement.TryGetProperty("private_ca", out var privateCa) && privateCa.GetBoolean();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var server) || !string.IsNullOrEmpty(server.UserInfo) ||
                !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment) ||
                server.AbsolutePath != "/" ||
                (server.Scheme != "https" && !(server.Scheme == "http" && IPAddress.TryParse(server.Host, out var ip) && IPAddress.IsLoopback(ip))))
                throw new InvalidDataException("Invalid authorization endpoint");
            _server = server;
            if (File.Exists(_path))
            {
                var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
                try { _saved = JsonSerializer.Deserialize<SavedAuthorization>(plaintext, Json); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                if (_saved is null || _saved.Server != _server.AbsoluteUri ||
                    _saved.Device is null || !Regex.IsMatch(_saved.DeviceToken ?? "", "^97d_[A-Za-z0-9_-]{43}$"))
                {
                    _saved = null;
                    SetStatus(LauncherAuthorizationStatus.Unpaired, "授权信息或服务地址已变更，请重新配对。");
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _saved = null;
            // Do not log exception messages: credential input must never enter the log.
            Serilog.Log.Warning("启动器授权信息读取失败：{ErrorType}", ex.GetType().Name);
            SetStatus(LauncherAuthorizationStatus.Unpaired, _server is null
                ? "授权服务配置无效，请联系管理员检查服务地址。" : "本机授权信息无法读取，请重新配对。");
            return;
        }
        if (_saved is not null) await CheckAsync();
        else SetStatus(LauncherAuthorizationStatus.Unpaired, "");
    }

    public async Task PairAsync(string input)
    {
        if (IsBusy) return;
        var code = Regex.Replace(input, @"\s+", "");
        if (!Regex.IsMatch(code, "^97p_[A-Za-z0-9_-]{43}$"))
        {
            SetStatus(Status, "请粘贴完整的配对 Token，并保留大小写。");
            return;
        }
        if (_server is null) { SetStatus(Status, "授权服务配置无效，请联系管理员。"); return; }
        var (generation, cancellation) = BeginOperation();
        SetStatus(LauncherAuthorizationStatus.Checking, "正在连接这台设备…");
        try
        {
            var installationId = ReadInstallationId();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, "api/v1/pairing/redeem"))
            {
                Content = JsonContent.Create(new { code, installation_id = installationId, device_name = Environment.MachineName,
                    platform = "windows", app_version = AppInfo.Version }, options: Json)
            };
            using var response = await _http.SendAsync(request, cancellation);
            if (generation != _generation) return;
            if (!response.IsSuccessStatusCode)
            {
                var message = await PairErrorAsync(response, cancellation);
                if (generation == _generation) SetStatus(LauncherAuthorizationStatus.Unpaired, message);
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<RedeemResponse>(Json, cancellation);
            if (generation != _generation) return;
            if (result?.Device is null || !Regex.IsMatch(result.DeviceToken ?? "", "^97d_[A-Za-z0-9_-]{43}$"))
                throw new JsonException();
            var saved = new SavedAuthorization(_server.AbsoluteUri, result.DeviceToken!, result.Device, DateTimeOffset.UtcNow);
            Save(saved);
            _saved = saved;
            SetStatus(LauncherAuthorizationStatus.Authorized, "设备已完成配对。");
        }
        catch (OperationCanceledException) when (generation != _generation) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (generation != _generation) return;
            Serilog.Log.Warning("启动器配对未完成：{ErrorType}", ex.GetType().Name);
            SetStatus(LauncherAuthorizationStatus.Unpaired, ex is IOException or UnauthorizedAccessException or CryptographicException
                ? "无法保存设备授权，请检查数据目录权限后重新获取 Token。"
                : "无法完成配对，请检查网络和授权服务后重试；Token 若已被兑换，请重新申请。");
        }
        finally { FinishOperation(generation); }
    }

    public async Task CheckAsync()
    {
        if (IsBusy || _saved is null || _server is null) return;
        var credential = _saved;
        var (generation, cancellation) = BeginOperation();
        if (!IsAuthorized) SetStatus(LauncherAuthorizationStatus.Checking, "正在验证本机授权…");
        try
        {
            using var request = DeviceRequest(HttpMethod.Get, "api/v1/devices/me", credential);
            using var response = await _http.SendAsync(request, cancellation);
            if (generation != _generation) return;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Invalidate();
                return;
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>(Json, cancellation);
            if (generation != _generation) return;
            if (result?.Device is null) throw new JsonException();
            if (!result.Authorized) { Invalidate(); return; }
            var verified = credential with { Device = result.Device, VerifiedAt = DateTimeOffset.UtcNow };
            Save(verified);
            _saved = verified;
            SetStatus(LauncherAuthorizationStatus.Authorized, "设备授权正常。");
            // Keep /devices/me authoritative; heartbeat updates server-side connection details.
            using var heartbeat = DeviceRequest(HttpMethod.Post, "api/v1/devices/heartbeat", verified);
            heartbeat.Content = JsonContent.Create(new { app_version = AppInfo.Version });
            using var heartbeatResponse = await _http.SendAsync(heartbeat, cancellation);
            if (generation == _generation && heartbeatResponse.StatusCode is (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                Invalidate();
            }
        }
        catch (OperationCanceledException) when (generation != _generation) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (generation != _generation) return;
            if (Status == LauncherAuthorizationStatus.Revoked) return;
            var age = DateTimeOffset.UtcNow - (_saved ?? credential).VerifiedAt;
            var offline = age >= TimeSpan.Zero && age <= TimeSpan.FromHours(72);
            SetStatus(offline ? LauncherAuthorizationStatus.Offline : LauncherAuthorizationStatus.NetworkRequired,
                offline ? "暂时无法联网验证，正在使用本机授权缓存。" : "暂时无法验证授权，请联网后重试；不必重新申请 Token。");
            Serilog.Log.Warning("启动器授权校验暂未完成：{ErrorType}", ex.GetType().Name);
        }
        finally { FinishOperation(generation); }
    }

    public async Task LogoutAsync()
    {
        var credential = _saved;
        CancelPending();
        // Local deletion must succeed before reporting logout; keep the installation UUID.
        File.Delete(_path);
        _saved = null;
        SetStatus(LauncherAuthorizationStatus.Unpaired, "已退出本机授权，请重新配对。");
        var generation = _generation;
        if (credential is null || _server is null) return;
        try
        {
            using var request = DeviceRequest(HttpMethod.Post, "api/v1/devices/logout", credential);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _http.SendAsync(request, cancellation.Token);
            if (generation != _generation) return;
            if (!response.IsSuccessStatusCode && response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
                SetStatus(Status, "本机已退出，服务端撤销未确认；可在后台撤销旧设备。");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (generation == _generation) SetStatus(Status, "本机已退出，服务端暂时无法连接；可在后台撤销旧设备。");
        }
    }

    public void CancelPending()
    {
        _generation++;
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
        IsBusy = false;
        if (Status == LauncherAuthorizationStatus.Checking)
            SetStatus(_saved is null ? LauncherAuthorizationStatus.Unpaired : LauncherAuthorizationStatus.NetworkRequired, "已取消验证。");
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RequireAuthorization()
    {
        if (Status == LauncherAuthorizationStatus.Offline && LastVerifiedAt is { } verified &&
            (DateTimeOffset.UtcNow < verified || DateTimeOffset.UtcNow - verified > TimeSpan.FromHours(72)))
            SetStatus(LauncherAuthorizationStatus.NetworkRequired, "本机离线授权已到期，请联网验证后继续。");
        if (!IsAuthorized) throw new InvalidOperationException("请先完成启动器设备配对。");
    }

    private void Invalidate()
    {
        _saved = null;
        SetStatus(LauncherAuthorizationStatus.Revoked, "本机授权已失效或被撤销，请重新获取 Token 配对。");
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning("无法删除已失效的本机授权文件：{ErrorType}", ex.GetType().Name);
        }
    }

    private (int Generation, CancellationToken Token) BeginOperation()
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        IsBusy = true;
        var generation = ++_generation;
        Changed?.Invoke(this, EventArgs.Empty);
        return (generation, _operation.Token);
    }

    private void FinishOperation(int generation)
    {
        if (generation != _generation) return;
        _operation?.Dispose();
        _operation = null;
        IsBusy = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(LauncherAuthorizationStatus status, string message)
    {
        if (Status != status || Message != message)
            Serilog.Log.Write(status is LauncherAuthorizationStatus.Revoked or LauncherAuthorizationStatus.NetworkRequired or LauncherAuthorizationStatus.Offline
                ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information,
                "启动器设备授权：{Status}，{Message}", status switch
                {
                    LauncherAuthorizationStatus.Unpaired => "未配对", LauncherAuthorizationStatus.Checking => "验证中",
                    LauncherAuthorizationStatus.Authorized => "授权有效", LauncherAuthorizationStatus.Offline => "离线缓存",
                    LauncherAuthorizationStatus.NetworkRequired => "需要联网", _ => "授权已撤销"
                }, message);
        Status = status;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private HttpRequestMessage DeviceRequest(HttpMethod method, string path, SavedAuthorization saved)
    {
        var request = new HttpRequestMessage(method, new Uri(_server!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", saved.DeviceToken);
        return request;
    }

    internal async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path,
        CancellationToken cancellation, Action<HttpRequestMessage>? configure = null)
    {
        RequireAuthorization();
        var saved = _saved ?? throw new InvalidOperationException("请先完成启动器设备配对。");
        if (_server is null || !Uri.TryCreate(_server, path, out var uri)
            || uri.Scheme != _server.Scheme || uri.Host != _server.Host || uri.Port != _server.Port
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal))
            throw new InvalidDataException("服务器接口地址无效。");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", saved.DeviceToken);
        configure?.Invoke(request);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (_saved?.DeviceToken != saved.DeviceToken)
        {
            response.Dispose();
            throw new OperationCanceledException("设备授权已变更。");
        }
        var revoked = response.StatusCode == HttpStatusCode.Unauthorized;
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentLength is <= 16384)
        {
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                revoked = error.RootElement.GetProperty("error").GetProperty("code").GetString() == "device_revoked";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        if (revoked)
        {
            response.Dispose();
            Invalidate();
            throw new InvalidOperationException("本机设备授权已失效，请重新配对。");
        }
        return response;
    }

    private void Save(SavedAuthorization authorization)
    {
        Directory.CreateDirectory(AppInfo.DataDirectory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(authorization, Json);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path + ".tmp", encrypted);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string ReadInstallationId()
    {
        var path = Path.Combine(AppInfo.DataDirectory, "installation-id.txt");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing)) return existing.ToString();
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path + ".tmp", id);
        File.Move(path + ".tmp", path, overwrite: true);
        return id;
    }

    private HttpClient CreateHttpClient()
    {
        using var pem = typeof(LauncherAuthorizationService).Assembly.GetManifestResourceStream("Launcher.AuthorizationRoot.pem")
            ?? throw new InvalidOperationException("Authorization trust anchor is missing");
        using var reader = new StreamReader(pem);
        var root = X509Certificate2.CreateFromPem(reader.ReadToEnd());
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
        {
            // Private CA trust is explicitly enabled and scoped to the configured service origin.
            if (!_usePrivateCa || _server is null || request.RequestUri is not { Scheme: "https" } uri ||
                uri.Host != _server.Host || uri.Port != _server.Port)
                return errors == SslPolicyErrors.None;
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // This private CA has no CRL/OCSP service.
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(certificate);
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<string> PairErrorAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var code = error.RootElement.TryGetProperty("error", out var details) && details.TryGetProperty("code", out var value) ? value.GetString() : "";
        return code switch
        {
            "code_invalid" => "Token 无效、已使用或已过期，请在群里重新申请。",
            "pairing_denied" => "此申请已不符合授权规则，请联系管理员。",
            "device_limit" => "设备数量已达上限，请联系管理员撤销旧设备。",
            "installation_conflict" => "这台设备已关联其他 QQ，请联系管理员处理。",
            "rate_limited" => "尝试过于频繁，请稍后重试。",
            _ => "授权服务暂时无法处理配对，请稍后重试。"
        };
    }
}
