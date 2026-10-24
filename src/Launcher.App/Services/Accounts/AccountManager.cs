using Serilog;

namespace Launcher.App.Services.Accounts;

/// <summary>
/// Owns the account list: loading, saving, adding, removing and selecting.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from the view models so that both the account panel and the launch
/// page read the same state, and so the login flows stay testable without any UI.
/// </para>
/// <para>
/// Every mutation saves immediately. Accounts are few and small, and a launcher that
/// loses the account list because the process was killed before a save is a much
/// worse outcome than a few extra file writes.
/// </para>
/// </remarks>
public sealed class AccountManager
{
    private readonly AccountStore _store;
    private readonly MicrosoftLogin _microsoft;
    private readonly AuthlibLogin _authlib;
    private readonly SkinService _skins;

    private AccountState _state = new();

    public AccountManager(
        AccountStore store,
        MicrosoftLogin microsoft,
        AuthlibLogin authlib,
        SkinService skins)
    {
        _store = store;
        _microsoft = microsoft;
        _authlib = authlib;
        _skins = skins;

        Reload();
    }

    /// <summary>Raised after the list or the selection changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Account> Accounts => _state.Accounts;

    /// <summary>The account the game launches with, or null when none is selected.</summary>
    public Account? Current =>
        _state.Accounts.FirstOrDefault(a => a.Id == _state.ActiveAccountId)
        ?? _state.Accounts.FirstOrDefault();

    public bool HasAccounts => _state.Accounts.Count > 0;

    /// <summary>Where the account file lives, shown in the settings page.</summary>
    public string StorePath => _store.Path;

    /// <summary>
    /// The Microsoft login service, exposed so the device code dialog can poll it.
    /// </summary>
    /// <remarks>
    /// Polling lives in the dialog because cancelling the dialog has to cancel the
    /// wait; routing that through the manager would mean holding the token source in
    /// two places.
    /// </remarks>
    public MicrosoftLogin Microsoft => _microsoft;

    /// <summary>Re-reads the file from disk.</summary>
    public void Reload()
    {
        _state = _store.Load();

        // Drop a dangling selection so Current never has to fall back silently.
        if (_state.Accounts.All(a => a.Id != _state.ActiveAccountId))
        {
            _state.ActiveAccountId = _state.Accounts.FirstOrDefault()?.Id ?? "";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds an offline account, or returns the existing one with the same name.
    /// </summary>
    /// <remarks>
    /// An offline UUID is a pure function of the name, so two offline accounts with
    /// the same name would be the same player; storing both would only confuse the
    /// picker.
    /// </remarks>
    public Account AddOffline(string name, bool slim = false)
    {
        name = name.Trim();

        if (string.IsNullOrEmpty(name))
        {
            throw new LoginException("用户名不能为空。");
        }

        var uuid = OfflineUuid.FromName(name);

        if (slim)
        {
            uuid = OfflineUuid.WithModel(uuid, slim: true);
        }

        var existing = _state.Accounts.FirstOrDefault(a =>
            a.Type == LoginType.Legacy && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Uuid = uuid;
            Save();
            Log.Information("已更新离线账号：{Name}", existing.Name);
            return existing;
        }

        var account = new Account
        {
            Type = LoginType.Legacy,
            Name = name,
            Uuid = uuid,
            LoginName = name,
        };

        return Insert(account);
    }

    /// <summary>
    /// Signs in with Microsoft and stores the resulting account.
    /// </summary>
    /// <param name="promptShown">
    /// Called with the device code as soon as it is available, so the UI can show the
    /// code before the (potentially long) wait for the user to finish signing in.
    /// Returns the tokens obtained by the dialog, or null when cancelled.
    /// </param>
    public async Task<Account?> AddMicrosoftAsync(
        Func<DeviceCodePrompt, CancellationToken, Task<(string AccessToken, string RefreshToken)?>> promptShown,
        CancellationToken ct,
        Action<double>? progress = null)
    {
        var prompt = await _microsoft.StartDeviceCodeAsync(ct);

        var tokens = await promptShown(prompt, ct);
        if (tokens is null)
        {
            return null;
        }

        return await CompleteMicrosoftAsync(tokens.Value.AccessToken, tokens.Value.RefreshToken, ct, progress);
    }

    /// <summary>
    /// Finishes a Microsoft sign-in once the OAuth tokens are known.
    /// </summary>
    /// <remarks>
    /// Split out so the device code dialog can own the polling, which keeps cancel
    /// responsive and lets the dialog report errors next to the code.
    /// </remarks>
    public async Task<Account> CompleteMicrosoftAsync(
        string oauthAccessToken,
        string refreshToken,
        CancellationToken ct,
        Action<double>? progress = null)
    {
        var profile = await _microsoft.LoginWithOAuthAsync(oauthAccessToken, ct, progress);

        var existing = _state.Accounts.FirstOrDefault(a =>
            a.Type == LoginType.Ms
            && string.Equals(a.Uuid, profile.Uuid, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Name = profile.Name;
            existing.RefreshToken = refreshToken;
            existing.AccessToken = profile.AccessToken;
            existing.ExpiresAt = profile.ExpiresAt;
            existing.ProfileJson = profile.ProfileJson;
            Save();
            await FetchSkinAsync(existing, ct);
            return existing;
        }

        var created = Insert(new Account
        {
            Type = LoginType.Ms,
            Name = profile.Name,
            Uuid = profile.Uuid,
            RefreshToken = refreshToken,
            AccessToken = profile.AccessToken,
            ExpiresAt = profile.ExpiresAt,
            ProfileJson = profile.ProfileJson,
            ClientToken = profile.Uuid,
        });

        await FetchSkinAsync(created, ct);
        return created;
    }

    /// <summary>
    /// Signs in with authlib-injector or Nide8 and stores the resulting account.
    /// </summary>
    public async Task<Account> AddServerAccountAsync(
        LoginType type,
        string server,
        string userName,
        string password,
        bool rememberPassword,
        Func<IReadOnlyList<string>, Task<int>> selectProfile,
        CancellationToken ct)
    {
        var baseUrl = type == LoginType.Nide
            ? AuthlibLogin.NideUrl(server)
            : AuthlibLogin.NormalizeAuthlibUrl(server);

        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new LoginException("服务器地址不能为空。");
        }

        // Reuse a stored token when this account is already known, so re-adding it
        // does not need the password again.
        var existing = _state.Accounts.FirstOrDefault(a =>
            a.Type == type && string.Equals(a.Server, baseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.LoginName, userName, StringComparison.OrdinalIgnoreCase));

        var result = await _authlib.LoginAsync(
            existing,
            baseUrl,
            userName,
            password,
            selectProfile,
            ct);

        var target = existing ?? new Account
        {
            Type = type,
            Server = baseUrl,
            LoginName = userName,
        };

        target.Name = result.Name;
        target.Uuid = result.Uuid;
        target.AccessToken = result.AccessToken;
        target.ClientToken = result.ClientToken;

        // Server tokens are opaque; treat them as good for a day, which is what the
        // validate-first ladder makes cheap to re-establish anyway.
        target.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();

        if (rememberPassword)
        {
            target.Password = password;
        }

        if (existing is null)
        {
            var inserted = Insert(target);
            await FetchSkinAsync(inserted, ct);
            return inserted;
        }

        Save();
        await FetchSkinAsync(target, ct);
        return target;
    }

    /// <summary>
    /// Refreshes a Microsoft account's token, if it has a refresh token.
    /// </summary>
    /// <remarks>
    /// Silent on failure by design: a launch attempt that needs the user's attention
    /// should report it, but a background refresh should not raise dialogs.
    /// </remarks>
    public async Task<bool> TryRefreshMicrosoftAsync(Account account, CancellationToken ct)
    {
        if (account.Type != LoginType.Ms || string.IsNullOrEmpty(account.RefreshToken))
        {
            return false;
        }

        try
        {
            var tokens = await _microsoft.TryRefreshAsync(account.RefreshToken, ct);

            if (tokens is null)
            {
                Log.Information("微软账号需要重新登录：{Name}", account.Name);
                return false;
            }

            var profile = await _microsoft.LoginWithOAuthAsync(tokens.Value.AccessToken, ct);

            account.Name = profile.Name;
            account.Uuid = profile.Uuid;
            account.RefreshToken = tokens.Value.RefreshToken;
            account.AccessToken = profile.AccessToken;
            account.ExpiresAt = profile.ExpiresAt;
            account.ProfileJson = profile.ProfileJson;

            Save();
            Log.Information("微软账号登录状态刷新成功：{Name}", account.Name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新微软账号失败：{Name}", account.Name);
            return false;
        }
    }

    /// <summary>
    /// Re-authenticates a saved Microsoft account from its refresh token.
    /// </summary>
    /// <remarks>
    /// This is what PCL does when a saved account is picked from its dropdown: no new
    /// browser authorization, just the stored refresh token exchanged for a fresh
    /// session. Throws when the token is gone or refused, which is the caller's cue to
    /// fall back to a full sign-in.
    /// </remarks>
    public async Task<Account> ReloginMicrosoftAsync(string name, CancellationToken ct)
    {
        var account = _state.Accounts.FirstOrDefault(a =>
            a.Type == LoginType.Ms && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            throw new LoginException($"找不到已保存的账号「{name}」。");
        }

        if (string.IsNullOrEmpty(account.RefreshToken))
        {
            throw new LoginCanceledException("该账号没有保存的登录状态，需要重新授权。");
        }

        var tokens = await _microsoft.TryRefreshAsync(account.RefreshToken, ct);

        if (tokens is null)
        {
            throw new LoginCanceledException("登录状态已失效，需要重新授权。");
        }

        var profile = await _microsoft.LoginWithOAuthAsync(tokens.Value.AccessToken, ct);

        account.Name = profile.Name;
        account.Uuid = profile.Uuid;
        account.RefreshToken = tokens.Value.RefreshToken;
        account.AccessToken = profile.AccessToken;
        account.ExpiresAt = profile.ExpiresAt;
        account.ProfileJson = profile.ProfileJson;

        Save();
        await FetchSkinAsync(account, ct);

        return account;
    }

    /// <summary>
    /// Fetches and caches an account's head image, then notifies listeners.
    /// </summary>
    /// <remarks>
    /// Deliberately non-fatal: a launcher that refused to add an account because its
    /// skin could not be downloaded would be trading a working sign-in for a
    /// cosmetic. A failure simply leaves the account on its vanilla fallback head.
    /// </remarks>
    private async Task FetchSkinAsync(Account account, CancellationToken ct)
    {
        try
        {
            var path = await _skins.GetHeadAsync(account, ct);

            if (!string.IsNullOrEmpty(path))
            {
                account.AvatarPath = path;
                Save();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "获取头像失败：{Name}", account.Name);
        }
    }

    /// <summary>
    /// Refreshes the head images for every stored account, in the background.
    /// </summary>
    /// <remarks>
    /// Used on startup: the cache is usually warm, so this is a few file checks, and
    /// accounts whose skin changed since the last run pick it up without the user
    /// having to re-login.
    /// </remarks>
    public async Task RefreshSkinsAsync(CancellationToken ct)
    {
        foreach (var account in _state.Accounts.ToList())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (account.Type == LoginType.Legacy)
            {
                continue;
            }

            await FetchSkinAsync(account, ct);
        }
    }

    /// <summary>
    /// Makes an account the one that launches the game.</summary>
    public void SetCurrent(Account account)
    {
        if (_state.Accounts.All(a => a.Id != account.Id))
        {
            return;
        }

        var changed = _state.ActiveAccountId != account.Id;
        _state.ActiveAccountId = account.Id;
        Save();
        if (changed) Log.Information("已切换游戏账号：{Name}（{Type}）", account.Name, account.Type);
    }

    public void Remove(Account account)
    {
        _state.Accounts.RemoveAll(a => a.Id == account.Id);

        if (_state.ActiveAccountId == account.Id)
        {
            _state.ActiveAccountId = _state.Accounts.FirstOrDefault()?.Id ?? "";
        }

        Save();
        Log.Information("已移除游戏账号：{Name}（{Type}）", account.Name, account.Type);
    }

    /// <summary>
    /// Stores an account, putting it at the front of the list.
    /// </summary>
    /// <remarks>
    /// Newest first matches the list the user just used, and matches PCL, which
    /// moves the account it just logged in with to the head of its list.
    /// </remarks>
    private Account Insert(Account account)
    {
        _state.Accounts.Insert(0, account);

        if (string.IsNullOrEmpty(_state.ActiveAccountId))
        {
            _state.ActiveAccountId = account.Id;
        }

        Save();
        if (account.Type == LoginType.Legacy) Log.Information("已添加离线账号：{Name}", account.Name);
        return account;
    }

    private void Save()
    {
        _store.Save(_state);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
