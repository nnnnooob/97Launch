using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Launcher.App.Services.Accounts;

/// <summary>
/// Microsoft (Mojang) account login, following PCL's six-step flow.
/// </summary>
/// <remarks>
/// <para>
/// The chain is fixed by Microsoft: OAuth for the Microsoft account, Xbox Live for
/// authentication, XSTS for authorisation, then the Minecraft services API for a
/// Minecraft token and profile. Each step's response is the next step's input, so
/// this is one linear method rather than a set of independent operations.
/// </para>
/// <para>
/// Endpoints and the consumer tenant match PCL deliberately: <c>/consumers</c> is
/// what restricts the flow to personal Microsoft accounts, which is what Minecraft
/// accounts are.
/// </para>
/// </remarks>
public sealed class MicrosoftLogin
{
    /// <summary>
    /// Records authentication failures and retries through the shared log pipeline.
    /// </summary>
    private static void Step(string message, Serilog.Events.LogEventLevel kind = Serilog.Events.LogEventLevel.Warning)
    {
        Serilog.Log.Write(kind, "{Message:l}", message);
    }

    /// <summary>
    /// Fallback Azure application (client) id, used when none is configured.
    /// </summary>
    /// <remarks>
    /// Carries the flow as far as the Xbox Live token. Mojang then refuses the
    /// Minecraft token with "Invalid app registration", because it only accepts
    /// applications on its own approved list — a decision on Mojang's side that no
    /// Azure setting can change. The environment variable exists so an approved id
    /// can be supplied without rebuilding, which is how PCL handles the same limit.
    /// </remarks>
    public const string DefaultClientId = "28e30885-6643-4228-98bf-984eb6021d7d";

    private const string ClientIdVariable = "PCL_MS_CLIENT_ID";

    /// <summary>The client id in use, taken from the environment when set.</summary>
    public static string ClientId
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(ClientIdVariable);

            return string.IsNullOrWhiteSpace(configured) ? DefaultClientId : configured.Trim();
        }
    }

    private const string DeviceCodeUrl =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    /// <summary>
    /// Where a device code is exchanged for tokens.
    /// </summary>
    /// <remarks>
    /// This must match <see cref="DeviceCodeUrl"/>: a code issued by the v2.0 endpoint
    /// is only valid there. Posting it to the older login.live.com token endpoint
    /// returns "The provided value for the input parameter 'device_code' is not valid."
    /// That endpoint is still correct for refresh tokens, which is why the two are
    /// kept apart.
    /// </remarks>
    private const string DeviceTokenUrl =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    private const string Scope = "XboxLive.signin offline_access";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Requests a device code and returns the instructions to show the user.
    /// </summary>
    /// <remarks>
    /// The device code flow is used instead of an embedded browser because it needs
    /// no redirect URI and no client secret, and because Microsoft no longer allows
    /// embedded browsers for this kind of sign-in.
    /// </remarks>
    public async Task<DeviceCodePrompt> StartDeviceCodeAsync(CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["tenant"] = "/consumers",
            ["scope"] = Scope,
        });

        using var response = await Http.PostAsync(DeviceCodeUrl, content, ct);
        var json = await ReadJsonAsync(response, ct);

        return new DeviceCodePrompt
        {
            DeviceCode = json.GetProperty("device_code").GetString() ?? "",
            UserCode = json.GetProperty("user_code").GetString() ?? "",
            VerificationUri = json.GetProperty("verification_uri").GetString() ?? "",
            ExpiresIn = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900,
            Interval = json.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
        };
    }

    /// <summary>
    /// Polls until the user finishes signing in, then returns the OAuth tokens.
    /// </summary>
    /// <exception cref="LoginCanceledException">The user aborted or the code expired.</exception>
    public async Task<(string AccessToken, string RefreshToken)> WaitForDeviceCodeAsync(
        DeviceCodePrompt prompt,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(prompt.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, prompt.Interval));

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = prompt.DeviceCode,
            });

            using var response = await Http.PostAsync(DeviceTokenUrl, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                return (
                    doc.RootElement.GetProperty("access_token").GetString() ?? "",
                    doc.RootElement.GetProperty("refresh_token").GetString() ?? "");
            }

            // authorization_pending means the user has not finished yet, which is
            // the normal case for most of the polling window.
            if (body.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (body.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }

            if (body.Contains("expired_token", StringComparison.OrdinalIgnoreCase)
                || body.Contains("code_expired", StringComparison.OrdinalIgnoreCase))
            {
                throw new LoginCanceledException("登录代码已过期，请重新登录。");
            }

            if (body.Contains("declined", StringComparison.OrdinalIgnoreCase))
            {
                throw new LoginCanceledException("你拒绝了授权申请。");
            }

            throw new LoginException("微软登录失败：" + DescribeOAuthError(body));
        }

        throw new LoginCanceledException("登录超时，请重新登录。");
    }

    /// <summary>
    /// Renews the OAuth tokens from a stored refresh token.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when Microsoft rejects the refresh token for a reason
    /// that can only be fixed by signing in again (password changed, consent
    /// revoked, token expired). PCL treats the same response set the same way.
    /// </remarks>
    public async Task<(string AccessToken, string RefreshToken)?> TryRefreshAsync(
        string refreshToken,
        CancellationToken ct, string? clientId = null)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = string.IsNullOrWhiteSpace(clientId) ? ClientId : clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope,
        });

        using var response = await Http.PostAsync(DeviceTokenUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("微软登录状态刷新失败：HTTP {Status}", (int)response.StatusCode);

            if (body.Contains("must sign in again", StringComparison.OrdinalIgnoreCase)
                || body.Contains("password expired", StringComparison.OrdinalIgnoreCase)
                || body.Contains("is not valid", StringComparison.OrdinalIgnoreCase)
                || body.Contains("expired", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw new LoginException("微软 Token 刷新失败：" + DescribeOAuthError(body));
        }

        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("access_token").GetString() ?? "",
            doc.RootElement.GetProperty("refresh_token").GetString() ?? "");
    }

    /// <summary>
    /// Walks the Xbox Live / XSTS / Minecraft chain and returns the final profile.
    /// </summary>
    /// <param name="progress">
    /// Receives 0..1 as the chain advances. PCL shows a percentage on its login button
    /// for the same reason: the chain is several round trips long and looks frozen
    /// otherwise.
    /// </param>
    public async Task<MicrosoftProfile> LoginWithOAuthAsync(
        string oauthAccessToken,
        CancellationToken ct,
        Action<double>? progress = null)
    {
        progress?.Invoke(0.15);

        var xblToken = await GetXboxTokenAsync(oauthAccessToken, ct);
        progress?.Invoke(0.35);

        var (xstsToken, uhs) = await GetXstsTokenAsync(xblToken, ct);
        progress?.Invoke(0.55);

        var (mcToken, expiresAt) = await GetMinecraftTokenAsync(xstsToken, uhs, ct);
        progress?.Invoke(0.7);

        await EnsureOwnsGameAsync(mcToken, ct);
        progress?.Invoke(0.85);

        var profile = await GetProfileAsync(mcToken, ct);
        progress?.Invoke(1.0);

        return profile with
        {
            AccessToken = mcToken,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>
    /// Step 2: exchanges the Microsoft token for an Xbox Live token.
    /// </summary>
    /// <remarks>
    /// The <c>d=</c> prefix marks the ticket as belonging to a user rather than a
    /// service. XSTS uses <c>t=</c>; user auth uses <c>d=</c>. PCL adds it only when
    /// missing, which is reproduced here.
    /// </remarks>
    private static async Task<string> GetXboxTokenAsync(string oauthAccessToken, CancellationToken ct)
    {
        var ticket = oauthAccessToken.StartsWith("d=", StringComparison.Ordinal)
            ? oauthAccessToken
            : "d=" + oauthAccessToken;

        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = ticket,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        using var response = await PostJsonAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            payload,
            ct);

        var json = await ReadJsonAsync(response, ct, "Xbox Live 认证");
        return json.GetProperty("Token").GetString() ?? "";
    }

    /// <summary>
    /// Step 3: exchanges the Xbox Live token for an XSTS token and the user hash.
    /// </summary>
    /// <remarks>
    /// XSTS reports account-level problems as numeric codes in the response body,
    /// which is why the errors are mapped here rather than at the call site: each
    /// code needs its own explanation and, in two cases, its own repair link.
    /// </remarks>
    private async Task<(string Token, string Uhs)> GetXstsTokenAsync(
        string xblToken,
        CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await Http.PostAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            content,
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // The translated error below carries the service error code; do not log raw responses.
            Step(
                $"微软 Xbox 授权失败：HTTP {(int)response.StatusCode}",
                Serilog.Events.LogEventLevel.Error);

            throw TranslateXstsError(body);
        }


        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("Token").GetString() ?? "";
        var uhs = doc.RootElement.GetProperty("DisplayClaims")
            .GetProperty("xui")[0]
            .GetProperty("uhs")
            .GetString() ?? "";

        return (token, uhs);
    }

    /// <summary>
    /// Turns an XSTS error body into an actionable message.
    /// </summary>
    /// <remarks>
    /// The codes are documented by community reference implementations rather than
    /// by Microsoft, and the wording follows PCL so users get the same advice they
    /// would get there.
    /// </remarks>
    private static Exception TranslateXstsError(string body)
    {
        if (body.Contains("2148916227", StringComparison.Ordinal))
        {
            return new LoginCanceledException("该账号似乎已被微软封禁，无法登录。");
        }

        if (body.Contains("2148916233", StringComparison.Ordinal))
        {
            return new LoginCanceledException(
                "你尚未注册 Xbox 账户，请在 https://signup.live.com 注册后再登录。");
        }

        if (body.Contains("2148916235", StringComparison.Ordinal))
        {
            return new LoginCanceledException(
                "你的网络所在的国家或地区无法登录微软账号。请使用加速器或 VPN 后再试。");
        }

        if (body.Contains("2148916238", StringComparison.Ordinal))
        {
            return new LoginCanceledException(
                "该账号年龄不足，需先在 https://account.live.com/editprof.aspx 修改出生日期。");
        }

        return new LoginException("Xbox 认证失败：" + body);
    }

    /// <summary>
    /// Step 4: exchanges the XSTS token for a Minecraft access token.
    /// </summary>
    /// <remarks>
    /// The reported expiry is pulled back by an hour, as PCL does, so a token that
    /// is about to lapse is treated as already expired instead of failing mid-launch.
    /// </remarks>
    private async Task<(string Token, long ExpiresAt)> GetMinecraftTokenAsync(
        string xstsToken,
        string uhs,
        CancellationToken ct)
    {
        var payload = new
        {
            identityToken = $"XBL3.0 x={uhs};{xstsToken}",
        };

        // PCL sends this step through its retrying request helper. A 403 here is often
        // a momentary risk-control decision rather than a real block, so the attempt is
        // repeated before telling the user their IP looks wrong.
        HttpResponseMessage? response = null;
        var json = JsonSerializer.Serialize(payload);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }

            response?.Dispose();

            // A fresh content object per attempt: an HttpContent whose stream has been
            // read cannot be sent again.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            response = await Http.PostAsync(
                "https://api.minecraftservices.com/authentication/login_with_xbox",
                content,
                ct);

            if (response.IsSuccessStatusCode
                || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                // 429 and 503 will not improve within this loop.
                break;
            }

            Step(
                $"Minecraft 登录服务返回 HTTP {(int)response.StatusCode}，准备第 {attempt + 1} 次重试",
                Serilog.Events.LogEventLevel.Warning);
        }

        using var owned = response!;
        var body = await owned.Content.ReadAsStringAsync(ct);

        if (!owned.IsSuccessStatusCode)
        {
            // Keep the HTTP status without recording the authentication response body.
            Step(
                $"Minecraft 登录失败：HTTP {(int)owned.StatusCode}",
                Serilog.Events.LogEventLevel.Error);

            throw owned.StatusCode switch
            {
                HttpStatusCode.TooManyRequests =>
                    new LoginException("登录尝试太过频繁，请等待几分钟后再试。"),

                // "Invalid app registration" is Mojang's allow-list rejection, not an
                // Azure misconfiguration: since Mojang started reviewing Java game
                // service API integrations, only applications on their approved list
                // may exchange an XSTS token for a Minecraft token. Everything before
                // this point succeeds, which is what makes the message so misleading.
                HttpStatusCode.Forbidden when body.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase) =>
                    new LoginException(
                        "登录失败：此应用未获准访问 Minecraft 服务。\n"
                        + "Mojang 现在要求第三方应用先申请白名单，未获批的应用无法完成正版登录。"),

                HttpStatusCode.Forbidden =>
                    new LoginException(
                        "当前 IP 的登录尝试异常。\n如果你使用了 VPN 或加速器，请把它们关掉或更换节点后再试！"),

                HttpStatusCode.ServiceUnavailable =>
                    new LoginException("Mojang 服务器出现问题，请稍后再试。"),

                _ when body.Contains("ACCOUNT_SUSPENDED", StringComparison.Ordinal) =>
                    new LoginCanceledException("该账号似乎已被微软封禁，无法登录。"),

                _ => new LoginException("获取 Minecraft 令牌失败：" + body),
            };
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        return (token, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn - 3600);
    }

    /// <summary>
    /// Step 5: confirms the account actually owns Minecraft.
    /// </summary>
    /// <remarks>
    /// Without this check a valid Microsoft account that never bought the game
    /// would reach the launch step and fail with a much less clear error.
    /// </remarks>
    private static async Task EnsureOwnsGameAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.minecraftservices.com/entitlements/mcstore");

        request.Headers.Add("Authorization", "Bearer " + accessToken);

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException("检查游戏所有权失败：" + body);
        }

        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0)
        {
            throw new LoginCanceledException(
                "你尚未购买正版 Minecraft，或者 Xbox Game Pass 已到期。");
        }
    }

    /// <summary>
    /// Step 6: reads the Minecraft profile (name, UUID, skins, capes).
    /// </summary>
    private static async Task<MicrosoftProfile> GetProfileAsync(
        string accessToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile");

        request.Headers.Add("Authorization", "Bearer " + accessToken);

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new LoginCanceledException(
                "该账号还没有 Minecraft 玩家档案，请先在 minecraft.net 创建档案后再登录。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException("获取玩家档案失败：" + body);
        }

        using var doc = JsonDocument.Parse(body);

        return new MicrosoftProfile
        {
            Name = doc.RootElement.GetProperty("name").GetString() ?? "",
            Uuid = doc.RootElement.GetProperty("id").GetString() ?? "",
            ProfileJson = body,
        };
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        object payload,
        CancellationToken ct)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        return await Http.PostAsync(url, content, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken ct,
        string step = "请求")
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException($"{step}失败：{body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Pulls the human-readable part out of an OAuth error response.
    /// </summary>
    private static string DescribeOAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error_description", out var description))
            {
                return description.GetString() ?? body;
            }

            if (root.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }

        return body;
    }
}

/// <summary>
/// What to show the user while the device code flow is pending.
/// </summary>
public sealed class DeviceCodePrompt
{
    public string DeviceCode { get; init; } = "";

    /// <summary>The short code the user types at <see cref="VerificationUri"/>.</summary>
    public string UserCode { get; init; } = "";

    public string VerificationUri { get; init; } = "";

    public int ExpiresIn { get; init; }

    public int Interval { get; init; }
}

/// <summary>
/// The Minecraft profile returned by the services API.
/// </summary>
public sealed record MicrosoftProfile
{
    public required string Name { get; init; }

    public required string Uuid { get; init; }

    public required string ProfileJson { get; init; }

    public string AccessToken { get; init; } = "";

    public long ExpiresAt { get; init; }
}

/// <summary>A login attempt that failed for a reason worth reporting.</summary>
public class LoginException : Exception
{
    public LoginException(string message) : base(message)
    {
    }
}

/// <summary>
/// A login attempt the user ended, or that ended for a reason that needs no retry.
/// </summary>
/// <remarks>
/// Separated from <see cref="LoginException"/> so callers can report a failure as a
/// warning without treating a deliberate cancellation as an error.
/// </remarks>
public sealed class LoginCanceledException : Exception
{
    public LoginCanceledException(string message) : base(message)
    {
    }
}
