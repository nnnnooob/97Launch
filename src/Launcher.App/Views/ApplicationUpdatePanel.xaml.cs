using Launcher.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

public sealed partial class ApplicationUpdatePanel : UserControl
{
    private readonly AppUpdateService _updates = App.Services.GetRequiredService<AppUpdateService>();
    private bool _refreshing;
    public ApplicationUpdatePanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { _updates.Changed += Changed; Refresh(); };
        Unloaded += (_, _) => _updates.Changed -= Changed;
    }
    private void Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);
    private void Refresh()
    {
        _refreshing = true;
        CurrentVersion.Text = $"{AppInfo.Version} · 构建 {AppInfo.BuildNumber} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        StatusText.Text = _updates.Status; DetailText.Text = _updates.Detail;
        CheckButton.IsEnabled = ChannelBox.IsEnabled = StartupCheck.IsEnabled = !_updates.Busy;
        ChannelBox.SelectedIndex = _updates.Channel == "stable" ? 0 : 1;
        StartupCheck.IsChecked = _updates.CheckOnStartup;
        UpdateProgress.Visibility = _updates.Busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = _updates.Busy && !_updates.Downloading;
        UpdateProgress.Value = _updates.Progress;
        ActionButton.Visibility = _updates.Ready is not null || _updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.IsEnabled = !_updates.Busy;
        ActionButton.Content = _updates.Ready is not null ? "重启并更新" : "下载更新";
        CancelButton.Visibility = _updates.Downloading ? Visibility.Visible : Visibility.Collapsed;
        var release = _updates.Ready ?? _updates.Latest;
        ReleaseCard.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is not null) { ReleaseTitle.Text = "更新说明 · v" + release.Version; ReleaseNotes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "此版本暂无更新说明。" : release.Notes; }
        _refreshing = false;
    }
    private async void Check_Click(object sender, RoutedEventArgs e) { ErrorBar.IsOpen = false; await _updates.CheckAsync(); }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        try { if (_updates.Ready is not null) await _updates.ApplyAsync(); else await _updates.StartDownloadAsync(); }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _updates.CancelDownload();
    private void Channel_Changed(object sender, SelectionChangedEventArgs e) => SavePreferences();
    private void Startup_Click(object sender, RoutedEventArgs e) => SavePreferences();
    private void SavePreferences()
    {
        if (_refreshing || !IsLoaded) return;
        try { _updates.Configure(ChannelBox.SelectedIndex == 0 ? "stable" : "preview", StartupCheck.IsChecked == true); }
        catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
    }
}
