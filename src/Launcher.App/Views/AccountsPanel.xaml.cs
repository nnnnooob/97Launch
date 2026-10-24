using Launcher.App.Services;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Launcher.App.Views;

/// <summary>
/// Account list and add-account actions, hosted in a flyout from the shell's
/// account chip.
/// </summary>
public sealed partial class AccountsPanel : UserControl
{
    public AccountsViewModel ViewModel { get; }

    public AccountsPanel()
    {
        ViewModel = App.Services.GetRequiredService<AccountsViewModel>();
        InitializeComponent();

        ViewModel.Refresh();
        ViewModel.UpdateOfflinePreview("");

        // Restore the tab the user left on. The panel is rebuilt every time the flyout
        // is opened, so without this the choice would snap back to offline each time.
        SelectTab(ViewModel.ActiveTab);
    }

    /// <summary>
    /// Checks one pill and shows its pane.
    /// </summary>
    /// <param name="tab">0 offline, 1 Microsoft, 2 third-party.</param>
    private void SelectTab(int tab)
    {
        ViewModel.ActiveTab = tab;

        TabOffline.IsChecked = tab == 0;
        TabMicrosoft.IsChecked = tab == 1;
        TabServer.IsChecked = tab == 2;

        PaneOffline.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaneMicrosoft.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        PaneServer.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;

        // The server pane serves both authlib and Nide8; the address decides which,
        // so it keeps the placeholder explaining both forms.
        if (tab == 2)
        {
            ServerBox.PlaceholderText = "认证服务器地址，或统一通行证服务器 ID";
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }

        // ToggleButtons do not group themselves, so only the clicked one stays checked.
        SelectTab(ReferenceEquals(clicked, TabMicrosoft) ? 1
            : ReferenceEquals(clicked, TabServer) ? 2
            : 0);
    }

    private void OfflineName_Changed(object sender, TextChangedEventArgs e)
        => ViewModel.UpdateOfflinePreview(OfflineNameBox.Text);

    private void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineNameBox.Text.Trim();

        if (name.Length == 0)
        {
            OfflineNameBox.Focus(FocusState.Programmatic);
            return;
        }

        var account = ViewModel.AddOffline(name, slim: false);

        RefreshShellAccount();

        OfflineNameBox.Text = "";
    }

    /// <summary>
    /// Starts a Microsoft sign-in and shows PCL's helper dialog for the code.
    /// </summary>
    /// <remarks>
    /// No inline code: the dialog opens the page, copies the code, and polls, which is
    /// what PCL's MyMsgLogin does. Closing the dialog cancels the login.
    /// </remarks>
    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearMicrosoftError();
        MsLoginButton.IsEnabled = false;

        try
        {
            var account = await ViewModel.LoginMicrosoftAsync(ShowDeviceCodeAsync);

            RefreshShellAccount();

            if (account is not null)
            {
                ViewModel.ClearMicrosoftError();
            }
        }
        catch (OperationCanceledException)
        {
            ViewModel.ClearMicrosoftError();
        }
        catch (LoginCanceledException ex)
        {
            ViewModel.MicrosoftError = ex.Message;
        }
        catch (LoginException ex)
        {
            ViewModel.MicrosoftError = ex.Message;
        }
        catch (Exception ex)
        {
            ViewModel.MicrosoftError = DescribeNetworkFailure(ex);
        }
        finally
        {
            MsLoginButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Shows the device code dialog and returns its OAuth tokens to the manager.
    /// </summary>
    /// <remarks>
    /// The dialog owns the polling, so the tokens come back here rather than from the
    /// manager's own wait. The manager finishes the Xbox and profile steps once.
    /// </remarks>
    private async Task<(string AccessToken, string RefreshToken)?> ShowDeviceCodeAsync(
        MicrosoftLogin login, DeviceCodePrompt prompt, CancellationToken cancellationToken)
    {
        var dialog = new DeviceCodeDialog(login, prompt, cancellationToken)
        {
            XamlRoot = XamlRoot,
        };

        var shown = dialog.ShowAsync();
        try
        {
            return await dialog.ResultAsync();
        }
        finally
        {
            // Also close on polling failures so a retry can open a new dialog.
            dialog.Hide();
            await shown;
        }
    }

    private string _serverPassword = "";

    private void ServerPassword_Changed(object sender, RoutedEventArgs e)
        => _serverPassword = ServerPassBox.Password;

    /// <summary>
    /// Logs in to the server named in the address box, in place.
    /// </summary>
    /// <remarks>
    /// The server field decides which of PCL's two tabs this is: a bare short string
    /// is a Nide8 server id, anything with a dot or a scheme is an authlib address.
    /// Both run the same form, exactly as PCL's two near-identical panels do.
    /// </remarks>
    private async void LoginServer_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        var userName = ServerNameBox.Text.Trim();

        if (server.Length == 0)
        {
            ServerBox.Focus(FocusState.Programmatic);
            return;
        }

        if (userName.Length == 0)
        {
            ServerNameBox.Focus(FocusState.Programmatic);
            return;
        }

        ViewModel.ClearServerError();
        ServerLoginButton.IsEnabled = false;

        try
        {
            await ViewModel.LoginServerAsync(
                LooksLikeUrl(server) ? LoginType.Auth : LoginType.Nide,
                server,
                userName,
                _serverPassword,
                RememberBox.IsChecked == true,
                SelectProfileAsync);

            RefreshShellAccount();

            ServerPassBox.Password = "";
            _serverPassword = "";
            ViewModel.ClearServerError();
        }
        catch (LoginCanceledException ex)
        {
            ViewModel.ServerError = ex.Message;
        }
        catch (LoginException ex)
        {
            ViewModel.ServerError = ex.Message;
        }
        catch (Exception ex)
        {
            ViewModel.ServerError = DescribeNetworkFailure(ex);
        }
        finally
        {
            ServerLoginButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Asks which character to use when a server returns more than one.
    /// </summary>
    /// <remarks>
    /// A ContentDialog is used here rather than an inline list because the flyout has
    /// no room for a second form, and the answer is needed before the login can
    /// finish, so the flow has to stop for it anyway.
    /// </remarks>
    private async Task<int> SelectProfileAsync(IReadOnlyList<string> names)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = names,
            MaxHeight = 240,
            SelectedIndex = 0,
        };

        var dialog = new ContentDialog
        {
            Title = "选择使用的角色",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "该账号下有多个角色，请选择本次使用的角色。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    list,
                },
            },
            PrimaryButtonText = "使用该角色",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? Math.Max(0, list.SelectedIndex) : 0;
    }

    /// <summary>
    /// Opens the server's registration page, as PCL's 注册账号 link does.
    /// </summary>
    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();

        if (server.Length == 0)
        {
            ServerBox.Focus(FocusState.Programmatic);
            return;
        }

        var url = LooksLikeUrl(server)
            ? AuthlibLogin.NormalizeAuthlibUrl(server).Replace("/authserver", "/auth/register", StringComparison.OrdinalIgnoreCase)
            : $"https://auth.mc-user.com:233/{server.Trim().Trim('/')}/auth/register";

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "打开注册页面失败");
        }
    }

    private static bool LooksLikeUrl(string value)
        => value.Contains('.', StringComparison.Ordinal)
           || value.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a socket failure into advice, not a raw exception message.
    /// </summary>
    /// <remarks>
    /// "由于目标计算机积极拒绝" is what .NET says when nothing is listening on the
    /// far end, and in practice it means a proxy was configured and has since exited:
    /// the proxy address is baked into the message, which is useless to the user. The
    /// same failure appears when the network is simply down.
    /// </remarks>
    private static string DescribeNetworkFailure(Exception ex)
    {
        var message = ex.Message;

        if (message.Contains("127.0.0.1:", StringComparison.Ordinal)
            || message.Contains("积极拒绝", StringComparison.Ordinal)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
        {
            return "登录失败：无法连接到服务器。\n"
                   + "如果你刚关闭了 VPN 或代理软件，请重启本程序后再试一次。";
        }

        if (ex is HttpRequestException or TaskCanceledException)
        {
            return "登录失败：网络连接不可用。\n请检查网络后重试；如果使用了代理，请确认代理仍在运行。";
        }

        return "登录失败：" + message;
    }

    /// <summary>Pushes the current account to the shell's chip and status bar.</summary>
    private static void RefreshShellAccount()
        => App.Services.GetRequiredService<MainViewModel>().RefreshAccount();

    private void SelectAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountRow row)
        {
            return;
        }

        ViewModel.SetCurrent(row.Account);

        RefreshShellAccount();
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountRow row)
        {
            return;
        }

        ViewModel.Remove(row.Account);

        RefreshShellAccount();
    }
}
