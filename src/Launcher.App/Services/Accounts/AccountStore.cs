using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Launcher.App.Services.Accounts;

/// <summary>
/// Reads and writes the saved account list.
/// </summary>
/// <remarks>
/// <para>
/// Secrets are encrypted with DPAPI scoped to the current Windows user, so a copied
/// file cannot be decrypted on another machine or by another account. PCL encrypts
/// the equivalent settings and stores them in the registry; a file plus DPAPI gives
/// the same protection without touching the registry.
/// </para>
/// <para>
/// A file that fails to parse is moved aside rather than deleted: losing every
/// account because of one malformed byte would be far worse than a stale file left
/// on disk.
/// </para>
/// </remarks>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public AccountStore(string path)
    {
        _path = path;
    }

    /// <summary>Where the account file lives.</summary>
    public string Path => _path;

    /// <summary>
    /// Loads the saved state. Returns an empty state when nothing is stored yet.
    /// </summary>
    public AccountState Load()
    {
        if (!File.Exists(_path))
        {
            return new AccountState();
        }

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<AccountState>(json, Options) ?? new AccountState();

            foreach (var account in state.Accounts)
            {
                Unprotect(account);
            }

            return state;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取账号文件失败，已备份并重新开始");
            TryMoveAside();

            return new AccountState();
        }
    }

    /// <summary>
    /// Writes the state, encrypting secrets first.
    /// </summary>
    /// <remarks>
    /// The accounts are copied before protecting so the caller's in-memory objects
    /// keep their plaintext passwords: encrypting in place would leave the running
    /// session holding ciphertext and break "re-login" until the next restart.
    /// </remarks>
    public void Save(AccountState state)
    {
        try
        {
            var copy = new AccountState
            {
                ActiveAccountId = state.ActiveAccountId,
                Accounts = state.Accounts.Select(Clone).ToList(),
            };

            foreach (var account in copy.Accounts)
            {
                Protect(account);
            }

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write to a temp file and replace, so an interrupted save cannot leave
            // a half-written account list behind.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, Options), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存账号文件失败");
        }
    }

    private static Account Clone(Account account) => new()
    {
        Id = account.Id,
        Type = account.Type,
        Name = account.Name,
        Uuid = account.Uuid,
        LoginName = account.LoginName,
        Password = account.Password,
        Server = account.Server,
        RefreshToken = account.RefreshToken,
        AccessToken = account.AccessToken,
        ClientToken = account.ClientToken,
        ExpiresAt = account.ExpiresAt,
        ProfileJson = account.ProfileJson,
        CreatedAt = account.CreatedAt,
        AvatarPath = account.AvatarPath,
    };

    private static void Protect(Account account)
    {
        account.Password = ProtectString(account.Password);
        account.RefreshToken = ProtectString(account.RefreshToken);
        account.AccessToken = ProtectString(account.AccessToken);
    }

    private static void Unprotect(Account account)
    {
        account.Password = UnprotectString(account.Password);
        account.RefreshToken = UnprotectString(account.RefreshToken);
        account.AccessToken = UnprotectString(account.AccessToken);
    }

    /// <summary>
    /// DPAPI-encrypts a value and returns it as base64, or the input unchanged when
    /// encryption is unavailable.
    /// </summary>
    /// <remarks>
    /// Empty values are left empty so the file stays readable. Values that are
    /// already plaintext (from a hand-edited file) simply load as-is, which keeps a
    /// damaged file from locking the user out of their own accounts.
    /// </remarks>
    private static string ProtectString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            return "enc:" + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "账号字段加密失败，将以明文保存");
            return value;
        }
    }

    private static string UnprotectString(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("enc:", StringComparison.Ordinal))
        {
            return value ?? "";
        }

        try
        {
            var bytes = Convert.FromBase64String(value[4..]);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "账号字段解密失败，已忽略");
            return "";
        }
    }

    private void TryMoveAside()
    {
        try
        {
            var backup = _path + $".{DateTime.Now:yyyyMMddHHmmss}.bak";
            File.Move(_path, backup, overwrite: true);
            Log.Information("账号文件已备份到 {Path}", backup);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "备份账号文件失败");
        }
    }
}

/// <summary>
/// Everything persisted about accounts: the list plus which one is selected.
/// </summary>
public sealed class AccountState
{
    /// <summary>Id of the account that launches the game.</summary>
    public string ActiveAccountId { get; set; } = "";

    public List<Account> Accounts { get; set; } = [];
}
