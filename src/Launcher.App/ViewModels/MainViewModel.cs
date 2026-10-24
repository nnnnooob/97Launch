using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Launcher.App.Services.Accounts;

namespace Launcher.App.ViewModels;

/// <summary>
/// Shared shell state: the current page, the signed-in account summary, and
/// the status-bar text.
/// </summary>
/// <remarks>
/// The account summary mirrors <see cref="AccountManager"/> rather than owning any
/// state. Both the sidebar and the launch page display it, and both must agree with
/// what would actually launch, so the manager stays the single source of truth.
/// </remarks>
public partial class MainViewModel : ObservableObject
{
    private readonly AccountManager _accounts;
    private readonly TransferViewModel _transfers;
    private readonly GameSessionManager _sessions;

    public MainViewModel(AccountManager accounts, TransferViewModel transfers, GameSessionManager sessions)
    {
        _accounts = accounts;
        _transfers = transfers;
        _sessions = sessions;
        _sessions.SessionsChanged += (_, _) =>
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher is null) return;
            if (dispatcher.HasThreadAccess) OnPropertyChanged(nameof(LaunchActiveBadge));
            else dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(LaunchActiveBadge)));
        };
        _accounts.Changed += (_, _) => RefreshAccount();
        _transfers.PropertyChanged += (_, _) => RefreshTransferStatus();

        RefreshAccount();
        RefreshTransferStatus();
    }

    [ObservableProperty]
    private string _pageTitle = "启动";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    [NotifyPropertyChangedFor(nameof(AccountDisplayName))]
    private AccountSummary _currentAccount = AccountSummary.SignedOut;

    public bool IsSignedIn => CurrentAccount.IsSignedIn;

    public string AccountDisplayName => CurrentAccount.IsSignedIn
        ? CurrentAccount.DisplayName
        : "未登录";

    public string AccountTypeDisplay => CurrentAccount.IsSignedIn
        ? CurrentAccount.AccountType
        : "点击添加账号";

    /// <summary>Head image for the signed-in account, or a vanilla head when signed out.</summary>
    public string AccountAvatar => CurrentAccount.Avatar;

    public string LaunchActiveBadge => _sessions.ActiveSessions.Count is var count && count > 0
        ? count.ToString() : string.Empty;

    public string TransferActiveBadge => _transfers.ActiveTaskCount > 0
        ? $"↓{_transfers.ActiveTaskCount}"
        : string.Empty;

    public string TransferWaitingBadge => _transfers.WaitingTaskCount > 0
        ? _transfers.WaitingTaskCount.ToString()
        : string.Empty;

    public string TransferErrorBadge => _transfers.FailedTaskCount > 0
        ? $"!{_transfers.FailedTaskCount}"
        : string.Empty;

    public string TransferStatusTooltip =>
        $"正在处理 {_transfers.ActiveTaskCount} 项，排队 {_transfers.WaitingTaskCount} 项，异常 {_transfers.FailedTaskCount} 项";

    private void RefreshTransferStatus()
    {
        OnPropertyChanged(nameof(TransferActiveBadge));
        OnPropertyChanged(nameof(TransferWaitingBadge));
        OnPropertyChanged(nameof(TransferErrorBadge));
        OnPropertyChanged(nameof(TransferStatusTooltip));
    }
    /// <summary>
    /// Version shown in the status bar. x:Bind cannot reach static members, so
    /// the AppInfo constant is surfaced through the view model instead of being
    /// duplicated as a literal in XAML.
    /// </summary>
    public string VersionDisplay => AppInfo.Version;

    /// <summary>
    /// Re-reads the current account from the manager.
    /// </summary>
    /// <remarks>
    /// Called on every change notification rather than diffing: there are a handful
    /// of accounts, and a missed update would leave the sidebar naming a different
    /// account than the one that launches.
    /// </remarks>
    public void RefreshAccount()
    {
        var account = _accounts.Current;

        CurrentAccount = account is null
            ? AccountSummary.SignedOut
            : new AccountSummary
            {
                DisplayName = account.Name,
                AccountType = account.TypeDisplay,
                IsSignedIn = true,
                Avatar = account.Avatar,
            };

        OnPropertyChanged(nameof(AccountAvatar));
    }
}

/// <summary>
/// The account shown in the sidebar and status bar.
/// </summary>
public sealed record AccountSummary
{
    public static AccountSummary SignedOut { get; } = new()
    {
        DisplayName = "未登录",
        AccountType = "点击添加账号",
        IsSignedIn = false,
        Avatar = AccountAvatar.Steve,
    };

    public required string DisplayName { get; init; }

    public required string AccountType { get; init; }

    public bool IsSignedIn { get; init; }

    /// <summary>Head image URI; a vanilla head when signed out.</summary>
    public required string Avatar { get; init; }
}
