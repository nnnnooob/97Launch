using System.Numerics;
using Launcher.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Launcher.App.Views;

public sealed partial class WelcomePairingView : UserControl
{
    private readonly LauncherAuthorizationService _authorization = App.Services.GetRequiredService<LauncherAuthorizationService>();
    private readonly UISettings _ui = new();
    private readonly AccessibilitySettings _accessibility = new();
    private bool _showPairing;

    public WelcomePairingView()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.Version;
        WelcomeCard.PointerMoved += Card_PointerMoved;
        WelcomeCard.PointerExited += (_, _) => MoveScene(Vector3.Zero);
        Loaded += (_, _) => { _authorization.Changed += Authorization_Changed; Refresh(); AnimateEntrance(); };
        Unloaded += (_, _) => { _authorization.Changed -= Authorization_Changed; MoveScene(Vector3.Zero); TokenInput.Text = ""; };
    }

    private void Authorization_Changed(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Refresh();
        else DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var busy = _authorization.IsBusy;
        var retry = _authorization.HasCredential && !_showPairing;
        var status = _authorization.Status;
        if (_authorization.IsAuthorized) { TokenInput.Text = ""; _showPairing = false; }
        TokenLabel.Visibility = TokenInput.Visibility = retry ? Visibility.Collapsed : Visibility.Visible;
        TokenInput.IsEnabled = !busy;
        PairButton.IsEnabled = !busy;
        PairButton.IsLaunching = busy;
        PairButtonText.Text = busy ? "正在验证…" : retry ? "重新连接" : "配对并进入";
        PairButton.SetValue(AutomationProperties.NameProperty, PairButtonText.Text);
        PairProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PairProgress.IsActive = busy;
        PairArrow.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HelpButton.Visibility = retry ? Visibility.Collapsed : Visibility.Visible;
        RePairButton.Visibility = retry && !busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _authorization.Message;
        StatusText.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            status is (LauncherAuthorizationStatus.Unpaired or LauncherAuthorizationStatus.Revoked) && !string.IsNullOrEmpty(_authorization.Message)
                ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
    }

    private async void PairButton_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        if (_authorization.IsBusy) return;
        if (_authorization.HasCredential && !_showPairing) await _authorization.CheckAsync();
        else await _authorization.PairAsync(TokenInput.Text);
    }

    private async void TokenInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SubmitAsync();
    }

    private void TokenInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_authorization.IsBusy && StatusText is not null) { StatusText.Text = ""; StatusText.Visibility = Visibility.Collapsed; }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _authorization.CancelPending();
    private void RePairButton_Click(object sender, RoutedEventArgs e) { _showPairing = true; Refresh(); TokenInput.Focus(FocusState.Programmatic); }

    private async void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        await new ContentDialog
        {
            Title = "获取配对 Token",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
                Text = "1. 打开已允许使用 97Launch 的 QQ 群。\n\n2. @群里的机器人，发送“获取配对Token”。\n\n3. 打开机器人私聊，完整复制 97p_ 开头的 Token，粘贴到这里。\n\n私聊发送失败时，请添加机器人好友后重新申请。Token 区分大小写，5 分钟内有效，仅可使用一次，请勿转发。" },
            CloseButtonText = "知道了", XamlRoot = XamlRoot
        }.ShowAsync();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 860 && !_accessibility.HighContrast;
        SceneColumn.Width = new GridLength(wide ? 49 : 0, GridUnitType.Star);
        FormColumn.Width = new GridLength(wide ? 51 : 1, GridUnitType.Star);
        ScenePanel.Visibility = wide && !_accessibility.HighContrast ? Visibility.Visible : Visibility.Collapsed;
        WelcomeCard.MaxWidth = wide ? 1040 : 480;
        FormPanel.Margin = wide ? new Thickness(40, 30, 34, 24) : new Thickness(38, 28, 38, 24);
    }

    private void ScenePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScenePanel.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(WelcomeCard);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        geometry.CornerRadius = new Vector2(22);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }

    private void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_ui.AnimationsEnabled || _accessibility.HighContrast || ScenePanel.Visibility != Visibility.Visible ||
            e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
        var point = e.GetCurrentPoint(WelcomeCard).Position;
        MoveScene(new Vector3((float)(point.X / Math.Max(WelcomeCard.ActualWidth, 1) - .5) * 10,
            (float)(point.Y / Math.Max(WelcomeCard.ActualHeight, 1) - .5) * 10, 0));
    }

    private void MoveScene(Vector3 offset)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(SceneImage, true);
        var visual = ElementCompositionPreview.GetElementVisual(SceneImage);
        visual.StopAnimation("Translation");
        if (!_ui.AnimationsEnabled || !IsLoaded)
        {
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, offset);
        animation.Duration = TimeSpan.FromMilliseconds(180);
        // Translation leaves layout positioning owned by XAML.
        visual.StartAnimation("Translation", animation);
    }

    private void AnimateEntrance()
    {
        if (!_ui.AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(WelcomeCard);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(350);
        visual.StartAnimation("Opacity", fade);
    }
}
