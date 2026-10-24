using Launcher.App.ViewModels;
using Launcher.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Hosting;
using System.ComponentModel;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace Launcher.App.Views;

public sealed partial class TransferPage : Page
{
    public TransferViewModel ViewModel { get; }
    private readonly UISettings _uiSettings = new();
    private bool _animationRunning;
    private bool _animationsSubscribed;

    public TransferPage()
    {
        ViewModel = App.Services.GetRequiredService<TransferViewModel>();
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                _uiSettings.AnimationsEnabledChanged += AnimationsEnabledChanged;
                _animationsSubscribed = true;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        UpdateAnimation();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_animationsSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= AnimationsEnabledChanged;
            _animationsSubscribed = false;
        }
        StopAnimation();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferViewModel.IsTransferring)) UpdateAnimation();
    }

    private void AnimationsEnabledChanged(UISettings sender, object args)
        => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) UpdateAnimation(); });

    private void UpdateAnimation()
    {
        var animate = IsLoaded && ViewModel.IsTransferring && _uiSettings.AnimationsEnabled;
        if (animate == _animationRunning) return;
        if (!animate) { StopAnimation(); return; }

        ElementCompositionPreview.SetIsTranslationEnabled(DownloadPacket, true);
        var packet = ElementCompositionPreview.GetElementVisual(DownloadPacket);
        packet.Properties.InsertVector3("Translation", Vector3.Zero);
        var compositor = packet.Compositor;
        var fall = compositor.CreateVector3KeyFrameAnimation();
        fall.InsertKeyFrame(0, new Vector3(0, -8, 0));
        fall.InsertKeyFrame(1, new Vector3(0, 27, 0));
        fall.Duration = TimeSpan.FromSeconds(1.8);
        fall.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        packet.StartAnimation("Translation", fall);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(.15f, 1);
        fade.InsertKeyFrame(.65f, 1);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = fall.Duration;
        fade.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        packet.StartAnimation("Opacity", fade);

        var pulse = compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, .25f);
        pulse.InsertKeyFrame(.65f, 1);
        pulse.InsertKeyFrame(1, .25f);
        pulse.Duration = fall.Duration;
        pulse.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        ElementCompositionPreview.GetElementVisual(DownloadLight).StartAnimation("Opacity", pulse);
        _animationRunning = true;
    }

    private void StopAnimation()
    {
        if (!_animationRunning) return;
        var packet = ElementCompositionPreview.GetElementVisual(DownloadPacket);
        packet.StopAnimation("Translation");
        packet.StopAnimation("Opacity");
        packet.Properties.InsertVector3("Translation", Vector3.Zero);
        packet.Opacity = 1;
        var light = ElementCompositionPreview.GetElementVisual(DownloadLight);
        light.StopAnimation("Opacity");
        light.Opacity = 1;
        _animationRunning = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ClearTerminalJobs();
        base.OnNavigatedFrom(e);
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TransferJob job })
        {
            ViewModel.CancelJob(job);
        }
    }

    private void LocateOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TransferJob { OutputFilePath: { } path } }) return;
        try { InstanceContentService.Open(path, true); }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法定位导出文件", ex.Message); }
    }

    private void RetryTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TransferJob job })
        {
            ViewModel.RetryJob(job);
        }
    }
    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { await App.Services.GetRequiredService<AppUpdateService>().ApplyAsync(); }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("暂时无法更新", ex.Message); }
    }

}
