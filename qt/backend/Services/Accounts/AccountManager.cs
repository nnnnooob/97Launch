using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Launcher.App.Services.Accounts;

// The Qt account owner persists this encrypted patch, merging with its current list.
public sealed class AccountManager
{
    public Account? Current { get; }
    public AccountManager(string id)
    {
        var path = Path.Combine(AppInfo.DataDirectory, "accounts.json");
        var state = JsonSerializer.Deserialize<AccountState>(File.ReadAllText(path))
            ?? throw new IOException("无法读取游戏账号。");
        Current = state.Accounts.SingleOrDefault(a => a.Id == id)
            ?? throw new IOException("所选账号已不存在，请重新选择。");
        Current.AccessToken = Unprotect(Current.AccessToken);
        Current.RefreshToken = Unprotect(Current.RefreshToken);
        Current.Password = Unprotect(Current.Password);
    }
    private static string Unprotect(string value) => !value.StartsWith("enc:", StringComparison.Ordinal) ? value
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[4..]), null, DataProtectionScope.CurrentUser));
    private static string Protect(string value) => "enc:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public async Task<bool> TryRefreshMicrosoftAsync(Account account, CancellationToken token)
    {
        var service = new MicrosoftLogin();
        var tokens = await service.TryRefreshAsync(account.RefreshToken, token, account.MicrosoftClientId);
        if (tokens is null) return false;
        var profile = await service.LoginWithOAuthAsync(tokens.Value.AccessToken, token);
        if (!profile.Uuid.Equals(account.Uuid, StringComparison.OrdinalIgnoreCase))
            throw new IOException("刷新后的游戏角色发生变化，请重新登录。");
        account.Name = profile.Name;
        account.AccessToken = profile.AccessToken;
        if (tokens.Value.RefreshToken.Length > 0) account.RefreshToken = tokens.Value.RefreshToken;
        account.ExpiresAt = profile.ExpiresAt;
        account.ProfileJson = profile.ProfileJson;
        Program.Send(new { type = "accountRefreshed", data = new {
            account.Id, account.Uuid, account.Name, account.ExpiresAt, account.ProfileJson,
            AccessToken = Protect(account.AccessToken), RefreshToken = Protect(account.RefreshToken) } });
        return true;
    }
}
