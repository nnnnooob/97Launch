using System.Text.Json;
using Launcher.App.Services.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Web.WebView2.Core;
using Windows.UI.ViewManagement;

namespace Launcher.App.Controls;

public sealed partial class SkinModelView : UserControl
{
    private const string ViewerUrl = "https://skin.97launch.local/index.html";
    private readonly AccountManager _accounts = App.Services.GetRequiredService<AccountManager>();
    private readonly SkinService _skins = App.Services.GetRequiredService<SkinService>();
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private WebView2? _web;
    private CancellationTokenSource? _skinRequest;
    private Window? _window;
    private bool _ready;
    private bool _pageActive = true;
    private bool _windowActive = true;
    private bool _contrastSubscribed;
    private string? _shownAccountId;

    public SkinModelView()
    {
        InitializeComponent();
        Loaded += LoadedView;
        Unloaded += UnloadedView;
        ActualThemeChanged += (_, _) => _ = UpdateActivityAsync();
    }

    private async void LoadedView(object sender, RoutedEventArgs e)
    {
        _accounts.Changed += AccountsChanged;
        _window = App.MainWindow;
        if (_window is not null) _window.Activated += WindowActivated;
        try { _accessibility.HighContrastChanged += ContrastChanged; _contrastSubscribed = true; }
        catch (System.Runtime.InteropServices.COMException) { }
        if (_web is not null) return;
        var web = new WebView2 { DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _web = web;
        ViewerHost.Children.Insert(0, web);
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(AppInfo.DataDirectory, "cache", "skin-viewer-webview"), null);
            if (!ReferenceEquals(_web, web)) return;
            await web.EnsureCoreWebView2Async(environment);
            if (!ReferenceEquals(_web, web)) return;
            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("skin.97launch.local",
                Path.Combine(AppContext.BaseDirectory, "Assets", "SkinViewer"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != ViewerUrl;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += async (_, args) =>
            {
                if (!ReferenceEquals(_web, web)) return;
                try
                {
                    _ready = args.IsSuccess && await core.ExecuteScriptAsync("!!window.viewerReady") == "true";
                    if (!_ready) { ShowFallback("当前设备暂时无法显示 3D 皮肤"); return; }
                    ResetButton.IsEnabled = true;
                    Fallback.Visibility = Visibility.Collapsed;
                    await UpdateActivityAsync();
                    await UpdateSkinAsync();
                }
                catch (Exception ex) { HandleViewerError(ex); }
            };
            web.CoreProcessFailed += (_, _) => ShowFallback("皮肤预览已中断，重新进入主页可恢复");
            web.Source = new Uri(ViewerUrl);
        }
        catch (Exception ex) { if (ReferenceEquals(_web, web)) HandleViewerError(ex); }
    }

    private void UnloadedView(object sender, RoutedEventArgs e)
    {
        _accounts.Changed -= AccountsChanged;
        if (_window is not null) _window.Activated -= WindowActivated;
        _window = null;
        if (_contrastSubscribed)
        {
            try { _accessibility.HighContrastChanged -= ContrastChanged; }
            catch (System.Runtime.InteropServices.COMException) { }
            _contrastSubscribed = false;
        }
        _skinRequest?.Cancel();
        _skinRequest?.Dispose();
        _skinRequest = null;
        _ready = false;
        ResetButton.IsEnabled = false;
        _shownAccountId = null;
        var web = _web;
        _web = null;
        if (web is not null) { web.Close(); ViewerHost.Children.Remove(web); }
        FallbackText.Text = "正在准备皮肤模型";
        Fallback.Visibility = Visibility.Visible;
    }

    private void AccountsChanged(object? sender, EventArgs args)
        => DispatcherQueue.TryEnqueue(() => _ = UpdateSkinAsync());

    private void WindowActivated(object sender, WindowActivatedEventArgs e)
    {
        _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        _ = UpdateActivityAsync();
    }

    private void ContrastChanged(AccessibilitySettings sender, object args)
        => DispatcherQueue.TryEnqueue(() => _ = UpdateActivityAsync());

    public void SetPageActive(bool active)
    {
        _pageActive = active;
        _ = UpdateActivityAsync();
    }

    private async Task UpdateActivityAsync()
    {
        var core = _ready ? _web?.CoreWebView2 : null;
        if (core is null) return;
        try
        {
            var state = JsonSerializer.Serialize(new
            {
                active = _pageActive && _windowActive && IsLoaded,
                animate = _settings.AnimationsEnabled && !_accessibility.HighContrast,
                dark = ActualTheme == ElementTheme.Dark,
            });
            await core.ExecuteScriptAsync($"window.setActivity({state})");
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "更新皮肤动画状态失败"); }
    }

    private async Task UpdateSkinAsync()
    {
        if (!IsLoaded) return;
        var account = _accounts.Current;
        _skinRequest?.Cancel();
        _skinRequest?.Dispose();
        var request = _skinRequest = new CancellationTokenSource();
        var slim = account is not null && AccountAvatar.ForOfflineName(account.Name) == AccountAvatar.Alex;
        var fallback = slim ? "alex.png" : "steve.png";
        var fallbackModel = slim ? "slim" : "default";
        FallbackHead.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(slim ? AccountAvatar.Alex : AccountAvatar.Steve));
        if (!_ready || _web?.CoreWebView2 is not { } core) return;
        try
        {
            var accountId = account?.Id ?? "default";
            if (_shownAccountId != accountId)
            {
                await core.ExecuteScriptAsync($"window.setSkin({JsonSerializer.Serialize(new { image = fallback, model = fallbackModel, fallback, fallbackModel })})");
                _shownAccountId = accountId;
            }
            if (account is null) return;
            var bytes = await _skins.GetSkinAsync(account, request.Token);
            if (bytes is null || request.IsCancellationRequested || !ReferenceEquals(request, _skinRequest)) return;
            var skin = JsonSerializer.Serialize(new { image = "data:image/png;base64," + Convert.ToBase64String(bytes), model = "auto", fallback, fallbackModel });
            await core.ExecuteScriptAsync($"window.setSkin({skin})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Serilog.Log.Debug(ex, "加载玩家皮肤失败，使用内置皮肤"); }
    }

    private void ShowFallback(string message)
    {
        _ready = false;
        ResetButton.IsEnabled = false;
        FallbackText.Text = message;
        Fallback.Visibility = Visibility.Visible;
    }

    private void HandleViewerError(Exception ex)
    {
        Serilog.Log.Warning(ex, "初始化玩家皮肤预览失败");
        ShowFallback("皮肤模型暂不可用，重新进入主页可重试");
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_ready || _web?.CoreWebView2 is not { } core) return;
            if (_settings.AnimationsEnabled && !_accessibility.HighContrast)
            {
                var visual = ElementCompositionPreview.GetElementVisual(ResetIcon);
                visual.CenterPoint = new System.Numerics.Vector3((float)ResetIcon.ActualWidth / 2, (float)ResetIcon.ActualHeight / 2, 0);
                var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 0);
                animation.InsertKeyFrame(1, 360);
                animation.Duration = TimeSpan.FromMilliseconds(280);
                visual.StartAnimation("RotationAngleInDegrees", animation);
            }
            await core.ExecuteScriptAsync("window.resetPose()");
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "复位皮肤模型失败"); }
    }
}
