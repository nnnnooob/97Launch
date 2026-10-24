using Launcher.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

public sealed partial class LauncherAuthorizationPanel : UserControl
{
    private readonly LauncherAuthorizationService _authorization = App.Services.GetRequiredService<LauncherAuthorizationService>();

    public LauncherAuthorizationPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { _authorization.Changed += AuthorizationChanged; Refresh(); };
        Unloaded += (_, _) => _authorization.Changed -= AuthorizationChanged;
    }

    private void AuthorizationChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Refresh();
        else DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        AuthorizationBadge.Text = _authorization.Status == LauncherAuthorizationStatus.Offline ? "离线可用" : "已授权";
        var device = _authorization.Device;
        AuthorizationOwner.Text = device is null ? "此设备尚未配对" : $"{device.Nickname}  ·  QQ {device.QqId}";
        AuthorizationDetails.Text = device is null ? "" : $"设备：{device.Name}\n来源群：{device.GroupId}\n配对时间：{DateTimeOffset.FromUnixTimeSeconds(device.CreatedAt).LocalDateTime:yyyy-MM-dd HH:mm}\n最近验证：{_authorization.LastVerifiedAt?.LocalDateTime:yyyy-MM-dd HH:mm}";
        AuthorizationMessage.Text = _authorization.Message;
        VerifyButton.IsEnabled = LogoutButton.IsEnabled = !_authorization.IsBusy;
        VerifyButton.Content = _authorization.IsBusy ? "正在验证…" : "重新验证";
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e) => await _authorization.CheckAsync();

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { Title = "退出本机授权？", Content = "退出后需重新配对才能进入启动器。已安装的游戏、游戏账号和设置都会保留。",
            PrimaryButtonText = "退出授权", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await _authorization.LogoutAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AuthorizationMessage.Text = "无法清除本机授权，请检查数据目录权限后重试。";
        }
    }
}
