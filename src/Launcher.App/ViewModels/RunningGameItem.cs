using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Microsoft.UI.Xaml;

namespace Launcher.App.ViewModels;

public sealed class RunningGameItem : ObservableObject
{
    private GameSessionInfo _info;

    public RunningGameItem(GameSessionInfo info) => _info = info;

    public Guid Id => _info.Id;
    public string InstanceName => _info.InstanceName;
    public string GameVersion => _info.GameVersion;
    public string IconPath => _info.IconPath;
    public bool IsStarting => _info.State == GameSessionState.Starting;
    public Visibility ProgressVisibility => IsStarting ? Visibility.Visible : Visibility.Collapsed;
    public bool IsProgressIndeterminate => IsStarting && _info.Progress is null;
    public double ProgressValue => Math.Clamp(_info.Progress ?? 0, 0, 1) * 100;
    public string ActionTooltip => IsStarting ? "取消启动" : "关闭游戏";
    public string ActionGlyph => IsStarting ? "\uE711" : "\uE71A";
    public string Detail => IsStarting
        ? $"{_info.Stage}{(_info.Progress is { } progress ? $" · {progress * 100:0}%" : "")}"
        : $"运行中 · PID {_info.ProcessId} · {FormatDuration(DateTimeOffset.Now - (_info.RunningAt ?? _info.StartedAt))}";

    public void Update(GameSessionInfo info)
    {
        _info = info;
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ActionTooltip));
        OnPropertyChanged(nameof(ActionGlyph));
    }

    public void RefreshDuration()
    {
        if (!IsStarting) OnPropertyChanged(nameof(Detail));
    }

    private static string FormatDuration(TimeSpan elapsed)
        => elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分钟"
            : $"{Math.Max(0, (int)elapsed.TotalMinutes)} 分钟";
}
