using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Launcher.App.Services.Accounts;

/// <summary>
/// Authlib-injector and Nide8 login, following PCL's validate → refresh →
/// authenticate ladder.
/// </summary>
/// <remarks>
/// <para>
/// Both account types speak the same API; only the base URL differs. Nide8 is one
/// hosted instance of that API, and authlib-injector servers are self-hosted, so one
/// implementation covers both.
/// </para>
/// <para>
/// The three requests are tried in order of cost: <c>validate</c> only confirms a
/// token, <c>refresh</c> renews it, and <c>authenticate</c> sends the password. A
/// stored password is therefore never transmitted while a token still works.
/// </para>
/// </remarks>
public sealed class AuthlibLogin
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>The Nide8 base URL for a given server id.</summary>
    public static string NideUrl(string serverId)
        => $"https://auth.mc-user.com:233/{serverId.Trim().Trim('/')}/authserver";

    /// <summary>
    /// Normalises an authlib server address into an <c>.../authserver</c> URL.
    /// </summary>
    /// <remarks>
    /// Users paste anything from a bare domain to a full authserver URL. Accepting
    /// all of those and appending the missing part is friendlier than rejecting the
    /// input, and mirrors how PCL builds the same string.
    /// </remarks>
    public static string NormalizeAuthlibUrl(string server)
    {
        var value = server.Trim().TrimEnd('/');

        if (value.Length == 0)
        {
            return "";
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!value.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
        {
            value += "/authserver";
        }

        return value;
    }

    /// <summary>
    /// Looks up the server's display name, for the account's server label.
    /// </summary>
    /// <remarks>
    /// Authlib servers advertise their branding at the root of the base URL rather
    /// than under <c>/authserver</c>. A failure here is not fatal: the account still
    /// logs in, it just shows the raw address instead of a name.
    /// </remarks>
    public static async Task<string> TryGetServerNameAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            var root = baseUrl.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase)
                ? baseUrl[..^"/authserver".Length]
                : baseUrl;

            using var response = await Http.GetAsync(root, ct);

            if (!response.IsSuccessStatusCode)
            {
                return "";
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("serverName", out var name))
            {
                return name.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取认证服务器名称失败");
        }

        return "";
    }

    /// <summary>
    /// Logs in, reusing an existing token when possible.
    /// </summary>
    /// <param name="existing">The stored account, or null for a first login.</param>
    /// <param name="baseUrl">Normalised authserver URL.</param>
    /// <param name="userName">Login name (email).</param>
    /// <param name="password">Password; may be empty when a token still works.</param>
    /// <param name="selectProfile">
    /// Invoked when the server returns several characters and none matches the
    /// cached UUID. Receives the candidate names and returns the chosen index.
    /// </param>
    public async Task<AuthlibResult> LoginAsync(
        Account? existing,
        string baseUrl,
        string userName,
        string password,
        Func<IReadOnlyList<string>, Task<int>> selectProfile,
        CancellationToken ct)
    {
        var requestUser = false;

        // 1. Validate: cheapest path, only usable when a token is already cached.
        if (existing is not null
            && !string.IsNullOrEmpty(existing.AccessToken)
            && !string.IsNullOrEmpty(existing.ClientToken)
            && !string.IsNullOrEmpty(existing.Uuid))
        {
            try
            {
                await ValidateAsync(baseUrl, existing, ct);

                return new AuthlibResult
                {
                    Name = existing.Name,
                    Uuid = existing.Uuid,
                    AccessToken = existing.AccessToken,
                    ClientToken = existing.ClientToken,
                    Reused = true,
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "验证登录失败，尝试刷新");
            }

            // 2. Refresh: renews the token without sending the password.
            try
            {
                return await RefreshAsync(baseUrl, existing, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "刷新登录失败，尝试重新登录");
            }
        }

        // 3. Authenticate: the only step that needs the password.
        if (string.IsNullOrEmpty(userName))
        {
            throw new LoginException("账号不能为空。");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new LoginException("密码不能为空。");
        }

        var result = await AuthenticateAsync(
            baseUrl,
            userName,
            password,
            existing?.Uuid ?? "",
            selectProfile,
            ct);

        return result with { Reused = false };
    }

    private static async Task ValidateAsync(string baseUrl, Account account, CancellationToken ct)
    {
        var payload = new
        {
            accessToken = account.AccessToken,
            clientToken = account.ClientToken,
            requestUser = true,
        };

        using var response = await PostAsync(baseUrl + "/validate", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // A successful validate replies with an empty body, so only the status
        // code carries the result.
        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException("验证登录失败：" + body);
        }
    }

    private static async Task<AuthlibResult> RefreshAsync(
        string baseUrl,
        Account account,
        CancellationToken ct)
    {
        var payload = new
        {
            selectedProfile = new
            {
                name = account.Name,
                id = account.Uuid,
            },
            accessToken = account.AccessToken,
            requestUser = true,
        };

        using var response = await PostAsync(baseUrl + "/refresh", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException(DescribeServerError(body, TypeOf(baseUrl), response.StatusCode));
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("selectedProfile", out var profile))
        {
            throw new LoginException($"选择的角色 {account.Name} 无效！");
        }

        return new AuthlibResult
        {
            Name = profile.GetProperty("name").GetString() ?? account.Name,
            Uuid = profile.GetProperty("id").GetString() ?? account.Uuid,
            AccessToken = root.GetProperty("accessToken").GetString() ?? "",
            ClientToken = root.TryGetProperty("clientToken", out var c)
                ? c.GetString() ?? ""
                : "",
            Reused = true,
        };
    }

    /// <summary>
    /// Authenticates with a password and picks a character profile.
    /// </summary>
    /// <remarks>
    /// The server may return several characters. The cached UUID wins when it is
    /// still valid, so a returning user is not asked again; otherwise the caller is
    /// asked to choose. Only when a character had to be chosen does a follow-up
    /// <c>refresh</c> exist, because that is how the API records the selection.
    /// </remarks>
    private async Task<AuthlibResult> AuthenticateAsync(
        string baseUrl,
        string userName,
        string password,
        string cachedUuid,
        Func<IReadOnlyList<string>, Task<int>> selectProfile,
        CancellationToken ct)
    {
        var payload = new
        {
            agent = new { name = "Minecraft", version = 1 },
            username = userName,
            password,
            requestUser = true,
        };

        using var response = await PostAsync(baseUrl + "/authenticate", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException(DescribeServerError(body, TypeOf(baseUrl), response.StatusCode));
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("accessToken").GetString() ?? "";
        var clientToken = root.TryGetProperty("clientToken", out var ct2)
            ? ct2.GetString() ?? ""
            : "";

        var availableProfiles = root.TryGetProperty("availableProfiles", out var profiles)
            && profiles.ValueKind == JsonValueKind.Array
                ? profiles
                : default;

        if (availableProfiles.ValueKind != JsonValueKind.Array
            || availableProfiles.GetArrayLength() == 0)
        {
            throw new LoginCanceledException("你还没有创建角色，请在创建角色后再试！");
        }

        string selectedName;
        string selectedId;
        var needRefresh = false;

        var hasSelected = root.TryGetProperty("selectedProfile", out var selected)
                          && selected.ValueKind == JsonValueKind.Object;

        if (hasSelected && availableProfiles.GetArrayLength() == 1)
        {
            selectedName = selected.GetProperty("name").GetString() ?? "";
            selectedId = selected.GetProperty("id").GetString() ?? "";
        }
        else
        {
            // Prefer the character used last time so a multi-character account does
            // not prompt on every login.
            string? matchedName = null;
            string? matchedId = null;

            foreach (var profile in availableProfiles.EnumerateArray())
            {
                var id = profile.GetProperty("id").GetString() ?? "";

                if (!string.IsNullOrEmpty(cachedUuid)
                    && string.Equals(id, cachedUuid, StringComparison.OrdinalIgnoreCase))
                {
                    matchedName = profile.GetProperty("name").GetString() ?? "";
                    matchedId = id;
                }
            }

            if (matchedName is null)
            {
                var names = availableProfiles
                    .EnumerateArray()
                    .Select(p => p.GetProperty("name").GetString() ?? "")
                    .ToList();

                var index = await selectProfile(names);
                index = Math.Clamp(index, 0, names.Count - 1);

                matchedName = names[index];
                matchedId = availableProfiles[index].GetProperty("id").GetString() ?? "";
            }

            selectedName = matchedName;
            selectedId = matchedId!;
            needRefresh = true;
        }

        var result = new AuthlibResult
        {
            Name = selectedName,
            Uuid = selectedId,
            AccessToken = accessToken,
            ClientToken = clientToken,
            Reused = false,
        };

        if (needRefresh)
        {
            try
            {
                var refreshed = await RefreshWithAsync(
                    baseUrl,
                    selectedName,
                    selectedId,
                    accessToken,
                    ct);

                result = result with
                {
                    AccessToken = refreshed.AccessToken,
                    ClientToken = refreshed.ClientToken,
                };
            }
            catch (Exception ex)
            {
                // The authenticate response is already usable; failing the whole
                // login because the follow-up refresh failed would be wrong.
                Log.Warning(ex, "选择角色后的刷新失败，沿用登录返回的令牌");
            }
        }

        return result;
    }

    private static async Task<AuthlibResult> RefreshWithAsync(
        string baseUrl,
        string name,
        string uuid,
        string accessToken,
        CancellationToken ct)
    {
        var payload = new
        {
            selectedProfile = new { name, id = uuid },
            accessToken,
            requestUser = true,
        };

        using var response = await PostAsync(baseUrl + "/refresh", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new LoginException(DescribeServerError(body, TypeOf(baseUrl), response.StatusCode));
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new AuthlibResult
        {
            Name = name,
            Uuid = uuid,
            AccessToken = root.GetProperty("accessToken").GetString() ?? "",
            ClientToken = root.TryGetProperty("clientToken", out var c)
                ? c.GetString() ?? ""
                : "",
            Reused = true,
        };
    }

    private static async Task<HttpResponseMessage> PostAsync(
        string url,
        object payload,
        CancellationToken ct)
    {
        // Accept-Language is a request header, so it has to go on the message rather
        // than the content: HttpContent.Headers rejects it as a misused header name.
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN");

        return await Http.SendAsync(request, ct);
    }

    /// <summary>
    /// Classifies an authlib base URL as Nide8 or generic authlib.
    /// </summary>
    /// <remarks>
    /// The two servers differ in which HTTP codes they return and in what the user
    /// should do about a failure, so the error text needs to know who answered.
    /// </remarks>
    private static LoginType TypeOf(string baseUrl)
        => baseUrl.Contains("auth.mc-user.com", StringComparison.OrdinalIgnoreCase)
            ? LoginType.Nide
            : LoginType.Auth;

    /// <summary>
    /// Turns an authlib error response into a message the user can act on.
    /// </summary>
    /// <remarks>
    /// A 403 from these servers covers several unrelated causes, so the wording lists
    /// them rather than guessing, matching what PCL shows. Each login method reports
    /// its own codes so the text always belongs to the form the user is looking at.
    /// </remarks>
    private static string DescribeServerError(string body, LoginType type, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            foreach (var key in new[] { "errorMessage", "error_message", "error" })
            {
                if (doc.RootElement.TryGetProperty(key, out var message))
                {
                    var text = message.GetString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return "登录失败：" + text;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the status-based messages.
        }

        // Nide8 answers 401 for a wrong password and 403 for a blocked or unregistered
        // account; authlib servers use 403 for all of them. The lists come from PCL so
        // the same code produces the same advice as it does there.
        if (status is HttpStatusCode.Unauthorized)
        {
            return "登录失败：账号或密码错误。";
        }

        if (status is HttpStatusCode.Forbidden)
        {
            return type == LoginType.Nide
                ? "登录失败，以下为可能的原因：\n"
                  + " - 输入的账号或密码错误。\n"
                  + " - 登录尝试过于频繁，导致被暂时屏蔽。请不要操作，等待 10 分钟后再试。\n"
                  + " - 账号尚未绑定到该服务器。"
                : "登录失败，以下为可能的原因：\n"
                  + " - 输入的账号或密码错误。\n"
                  + " - 登录尝试过于频繁，导致被暂时屏蔽。请等待 10 分钟后再试。\n"
                  + " - 只注册了账号，但没有在皮肤站新建角色。";
        }

        if (status is HttpStatusCode.TooManyRequests)
        {
            return "登录尝试太过频繁，请等待几分钟后再试。";
        }

        if (status is HttpStatusCode.NotFound)
        {
            return "服务器地址无效，或该地址不是 authlib-injector 认证服务器。";
        }

        return $"登录失败（HTTP {(int)status}）：{body}";
    }
}

/// <summary>The outcome of an authlib or Nide login.</summary>
public sealed record AuthlibResult
{
    public required string Name { get; init; }

    public required string Uuid { get; init; }

    public required string AccessToken { get; init; }

    public required string ClientToken { get; init; }

    /// <summary>True when an existing token was reused rather than a password sent.</summary>
    public bool Reused { get; init; }
}
