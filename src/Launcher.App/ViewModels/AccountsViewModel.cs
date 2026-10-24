using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services.Accounts;

namespace Launcher.App.ViewModels;

/// <summary>
/// Account list and login state for the account panel.
/// </summary>
/// <remarks>
/// Wraps <see cref="AccountManager"/> rather than talking to the store directly, so
/// the panel and the launch page always observe the same account list. The login
/// methods here own the cancellable operation and the progress text, which are UI
/// concerns the manager has no business knowing about.
/// </remarks>
public partial class AccountsViewModel : ObservableObject
{
    private readonly AccountManager _manager;
    private CancellationTokenSource? _loginCts;

    public AccountsViewModel(AccountManager manager)
    {
        _manager = manager;
        _manager.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The stored accounts, newest first, as display rows.</summary>
    public ObservableCollection<AccountRow> Accounts { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    public bool IsEmpty => Accounts.Count == 0;

    /// <summary>True while a login flow is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(MicrosoftButtonText))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// Login progress, 0..1.
    /// </summary>
    /// <remarks>
    /// PCL shows this as a percentage on its own login button while the chain runs,
    /// because the Xbox and profile round trips take long enough that a static button
    /// looks hung.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(MicrosoftButtonText))]
    private double _progress;

    public string ProgressText => $"{Math.Round(Progress * 100)}%";

    /// <summary>
    /// Caption of the Microsoft login button: its normal label, or the percentage while
    /// a login is running.
    /// </summary>
    public string MicrosoftButtonText => IsBusy ? ProgressText : "添加微软账号";

    /// <summary>Progress text shown in the add-account dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    /// <summary>The device code to show during a Microsoft login, empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    private string _deviceCode = "";

    /// <summary>Where the user should type <see cref="DeviceCode"/>.</summary>
    [ObservableProperty]
    private string _verificationUri = "";

    public bool HasDeviceCode => !string.IsNullOrEmpty(DeviceCode);

    /// <summary>
    /// Which add-account tab is showing: 0 offline, 1 Microsoft, 2 third-party.
    /// </summary>
    /// <remarks>
    /// Kept on the view model because the panel is rebuilt whenever the flyout opens,
    /// and the choice should survive that: picking 正版 once and reopening the panel
    /// used to land back on 离线 every time.
    /// </remarks>
    [ObservableProperty]
    private int _activeTab;

    /// <summary>
    /// Failure text for the Microsoft pane.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMicrosoftError))]
    private string _microsoftError = "";

    public bool HasMicrosoftError => !string.IsNullOrEmpty(MicrosoftError);

    /// <summary>
    /// Failure text for the third-party pane.
    /// </summary>
    /// <remarks>
    /// Each login form keeps its own error. One shared field meant a failed Microsoft
    /// attempt left a red message under the offline form too, which read as though it
    /// belonged to whichever tab was open.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    private string _serverError = "";

    public bool HasServerError => !string.IsNullOrEmpty(ServerError);

    /// <summary>
    /// The message to tell the user for a server login failure.
    /// </summary>
    /// <remarks>
    /// Authlib servers report codes that each mean something specific, so they are
    /// named rather than collapsed into one generic sentence.
    /// </remarks>
    public static string DescribeServerFailure(Exception ex) => ex switch
    {
        LoginCanceledException canceled => canceled.Message,
        LoginException login => login.Message,
        _ => "登录失败：" + ex.Message,
    };

    /// <summary>Clears the Microsoft pane's message.</summary>
    public void ClearMicrosoftError()
    {
        MicrosoftError = "";
        StatusText = "";
    }

    /// <summary>Clears the third-party pane's message.</summary>
    public void ClearServerError()
    {
        ServerError = "";
        StatusText = "";
    }

    /// <summary>Whether a login is running, for the per-pane progress line.</summary>
    public bool HasStatus => IsBusy;

    /// <summary>
    /// The offline UUID preview for the name currently typed in the panel.
    /// </summary>
    /// <remarks>
    /// Shown live under the name box so the user can see the UUID is derived from the
    /// name rather than assigned randomly, which is what makes the same name produce
    /// the same player on every machine.
    /// </remarks>
    [ObservableProperty]
    private string _offlinePreview = "输入用户名后显示将要使用的 UUID";

    /// <summary>
    /// The head the typed offline name will produce.
    /// </summary>
    /// <remarks>
    /// PCL shows a skin preview above the name box, and it resolves as you type. The
    /// same here: since the model is derived from the name, the preview is the only
    /// feedback that the name maps to the model you expect.
    /// </remarks>
    [ObservableProperty]
    private string _offlineHeadPreview = AccountAvatar.Steve;

    /// <summary>Recomputes <see cref="OfflinePreview"/> for a name being typed.</summary>
    public void UpdateOfflinePreview(string name)
    {
        name = name.Trim();

        OfflinePreview = name.Length == 0
            ? "输入用户名后显示将要使用的 UUID"
            : OfflineUuid.ToDashed(OfflineUuid.FromName(name));

        OfflineHeadPreview = AccountAvatar.ForOfflineName(name);
    }

    public Account? Current => _manager.Current;

    /// <summary>
    /// Rebuilds the observable list from the manager.
    /// </summary>
    /// <remarks>
    /// Built in full and then swapped in one pass, rather than clearing and adding row
    /// by row: a partially populated collection renders intermediate states, which is
    /// exactly how a stale "current" badge ended up on screen alongside a new one. All
    /// rows also share a single value of "which account is current", so they cannot
    /// disagree with each other.
    /// </remarks>
    public void Refresh()
    {
        var current = _manager.Current;

        var rows = _manager.Accounts
            .Select(a => new AccountRow(a, ReferenceEquals(a, current)))
            .ToList();

        Accounts.Clear();

        foreach (var row in rows)
        {
            Accounts.Add(row);
        }

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Current));

        AccountSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the current account changes, for the shell to mirror.</summary>
    public event EventHandler? AccountSelectionChanged;

    public void SetCurrent(Account account) => _manager.SetCurrent(account);

    public void Remove(Account account) => _manager.Remove(account);

    /// <summary>
    /// Replaces the current account list with the one loaded from disk.
    /// </summary>
    public void ReloadFromDisk()
    {
        _manager.Reload();
    }

    /// <summary>Adds an offline account and makes it current.</summary>
    public Account AddOffline(string name, bool slim)
    {
        var account = _manager.AddOffline(name, slim);
        _manager.SetCurrent(account);
        return account;
    }

    /// <summary>
    /// Runs the Microsoft device code flow.
    /// </summary>
    /// <remarks>
    /// The prompt is handed to <paramref name="showPrompt"/>, which opens the device
    /// code dialog and owns the polling; returning null cancels the sign-in. The
    /// whole flow is not awaited up front because the dialog has to be on screen while
    /// the user completes the browser step.
    /// </remarks>
    public async Task<Account?> LoginMicrosoftAsync(
        Func<MicrosoftLogin, DeviceCodePrompt, CancellationToken, Task<(string AccessToken, string RefreshToken)?>> showPrompt)
    {
        var cts = BeginLogin("正在连接微软服务器……");
        Serilog.Log.Information("开始添加微软正版账号");

        try
        {
            var account = await _manager.AddMicrosoftAsync(
                (prompt, token) => showPrompt(_manager.Microsoft, prompt, token),
                cts.Token,
                p => Progress = p);

            if (account is null)
            {
                Serilog.Log.Information("已取消添加微软正版账号");
                return null;
            }

            _manager.SetCurrent(account);
            StatusText = $"登录成功：{account.Name}";
            Serilog.Log.Information("微软正版账号添加成功：{Name}", account.Name);

            return account;
        }
        catch (OperationCanceledException) { Serilog.Log.Information("微软正版账号添加已取消"); throw; }
        catch (LoginCanceledException ex) { Serilog.Log.Warning("微软正版账号添加未完成：{Reason}", ex.Message); throw; }
        catch (Exception ex) { Serilog.Log.Error(ex, "微软正版账号添加失败"); throw; }
        finally
        {
            EndLogin();
        }
    }

    /// <summary>Runs an authlib or Nide8 login.</summary>
    public async Task<Account> LoginServerAsync(
        LoginType type,
        string server,
        string userName,
        string password,
        bool rememberPassword,
        Func<IReadOnlyList<string>, Task<int>> selectProfile)
    {
        var cts = BeginLogin("正在登录……");
        Serilog.Log.Information("开始添加第三方游戏账号：{Type}", type);

        try
        {
            var account = await _manager.AddServerAccountAsync(
                type,
                server,
                userName,
                password,
                rememberPassword,
                selectProfile,
                cts.Token);

            _manager.SetCurrent(account);
            StatusText = $"登录成功：{account.Name}";
            Serilog.Log.Information("第三方账号登录成功：{Name}", account.Name);

            return account;
        }
        catch (OperationCanceledException) { Serilog.Log.Information("第三方账号登录已取消"); throw; }
        catch (LoginCanceledException ex) { Serilog.Log.Warning("第三方账号登录未完成：{Reason}", ex.Message); throw; }
        catch (Exception ex) { Serilog.Log.Error(ex, "第三方账号登录失败"); throw; }
        finally
        {
            EndLogin();
        }
    }

    /// <summary>
    /// Re-authenticates a saved Microsoft account without a new authorization.
    /// </summary>
    public async Task<Account> ReloginMicrosoftAsync(string name)
    {
        var cts = BeginLogin($"正在登录 {name}……");
        Serilog.Log.Information("开始刷新微软账号登录状态：{Name}", name);

        try
        {
            var account = await _manager.ReloginMicrosoftAsync(name, cts.Token);
            _manager.SetCurrent(account);
            StatusText = $"登录成功：{account.Name}";
            Serilog.Log.Information("微软账号登录状态已刷新：{Name}", account.Name);

            return account;
        }
        catch (OperationCanceledException) { Serilog.Log.Information("刷新微软账号登录状态已取消"); throw; }
        catch (Exception ex) { Serilog.Log.Warning(ex, "刷新微软账号登录状态失败：{Name}", name); throw; }
        finally
        {
            EndLogin();
        }
    }

    /// <summary>Aborts an in-flight login.</summary>
    public void CancelLogin() => _loginCts?.Cancel();

    private CancellationTokenSource BeginLogin(string status)
    {
        _loginCts?.Cancel();
        _loginCts = new CancellationTokenSource();

        IsBusy = true;
        Progress = 0;
        StatusText = status;
        DeviceCode = "";
        VerificationUri = "";

        return _loginCts;
    }

    private void EndLogin()
    {
        IsBusy = false;
        DeviceCode = "";
    }
}
