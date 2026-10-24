using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

public sealed record LaunchHistoryRow(LaunchHistoryEntry Entry)
{
    public string Name => Entry.InstanceName;
    public string IconPath => Entry.IconPath;
    public string Time => Entry.StartedAt.LocalDateTime.Date == DateTime.Today
        ? Entry.StartedAt.ToLocalTime().ToString("今天 HH:mm") : Entry.StartedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string Status
    {
        get
        {
            var label = Entry.State switch
            {
                LaunchHistoryState.Starting => "正在启动",
                LaunchHistoryState.Running => "运行中",
                LaunchHistoryState.Completed => "已结束",
                LaunchHistoryState.Failed => Entry.RunningAt is null ? "启动失败" : "异常退出",
                LaunchHistoryState.Cancelled => "已取消",
                _ => "记录已中断",
            };
            if (Entry.RunningAt is not { } start || Entry.State == LaunchHistoryState.Interrupted) return label;
            var duration = (Entry.EndedAt ?? DateTimeOffset.Now) - start;
            var text = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟"
                : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} 分钟" : "不足 1 分钟";
            return $"{label} · {text}";
        }
    }
    public string Hint => $"{Entry.InstanceName}\n{Entry.GameVersion}\n{Entry.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{Status}"
        + (Entry.Detail is null ? "" : "\n" + Entry.Detail);
}

public sealed partial class LaunchHistoryPanel : UserControl
{
    private readonly LaunchHistoryStore _history = App.Services.GetRequiredService<LaunchHistoryStore>();
    private readonly GameSessionManager _sessions = App.Services.GetRequiredService<GameSessionManager>();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _selecting;

    public LaunchHistoryPanel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _history.Changed += HistoryChanged; Refresh(); _timer.Start(); };
        Unloaded += (_, _) => { _history.Changed -= HistoryChanged; _timer.Stop(); };
    }

    private void HistoryChanged(object? sender, EventArgs args)
        => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        if (!IsLoaded) return;
        _history.RefreshDetachedSessions(_sessions.ActiveSessions.Select(s => s.Id));
        var entries = _history.Entries;
        var offset = HistoryList.ItemsPanelRoot is null ? 0 : GetScrollViewer(HistoryList)?.VerticalOffset ?? 0;
        HistoryList.ItemsSource = entries.Select(e => new LaunchHistoryRow(e)).ToArray();
        CountText.Text = entries.Count == 0 ? "" : $"{entries.Count} 次";
        EmptyHistory.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (offset > 0) HistoryList.DispatcherQueue.TryEnqueue(() => GetScrollViewer(HistoryList)?.ChangeView(null, offset, null, true));
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (GetScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private async void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_selecting || e.ClickedItem is not LaunchHistoryRow row) return;
        _selecting = true;
        var toasts = App.Services.GetRequiredService<ToastService>();
        try
        {
            var path = row.Entry.InstancePath;
            if (!Directory.Exists(path)) { toasts.Show("实例已不存在", row.Name); return; }
            var instances = App.Services.GetRequiredService<InstancesViewModel>();
            var folderPath = Path.GetDirectoryName(Path.GetDirectoryName(path));
            var folder = instances.Folders.FirstOrDefault(f => GameFolderService.PathsEqual(f.Path, folderPath ?? ""));
            if (folder is null) { toasts.Show("实例文件夹未添加", "请在实例页面重新添加对应游戏文件夹。"); return; }
            if (!GameFolderService.PathsEqual(instances.FolderSelectionPath ?? "", folder.Path))
            {
                instances.SelectedFolder = folder;
                await instances.LoadInstancesAsync();
                if (instances.SelectedFolder != folder) return;
            }
            var target = instances.AvailableInstances.FirstOrDefault(i => GameFolderService.PathsEqual(i.Path, path));
            if (target is null) { toasts.Show("实例暂不可用", "请刷新实例列表后重试。"); return; }
            instances.SelectedInstance = target;
            toasts.Show("已选中实例", target.Name);
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "从历史记录选择实例失败"); toasts.ShowError("无法选中实例", "请在实例页面检查对应文件夹。"); }
        finally { _selecting = false; }
    }
}
