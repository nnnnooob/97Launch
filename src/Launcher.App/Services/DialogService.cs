using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Services;

public sealed class UserCanceledOperationException() : OperationCanceledException("用户取消了操作。");

/// <summary>
/// Modal dialog contract. Every placeholder button routes through here so the
/// "not implemented yet" message stays consistent.
/// </summary>
public interface IDialogService
{
    Task ShowNotImplementedAsync(string featureName, XamlRoot root);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText, XamlRoot root);

    Task ShowGameCrashAsync(GameCrashInfo crash, XamlRoot root);
    Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken = default);
}

public sealed class DialogService : IDialogService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken = default)
    {
        await _dialogGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var registration = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            var result = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { _dialogGate.Release(); }
    }

    public async Task ShowNotImplementedAsync(string featureName, XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = "功能开发中",
            Content = $"「{featureName}」将在后续版本中实现。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };

        await _dialogGate.WaitAsync();
        try { await dialog.ShowAsync(); }
        finally { _dialogGate.Release(); }
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        await _dialogGate.WaitAsync();
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialogGate.Release(); }
    }

    public async Task ShowGameCrashAsync(GameCrashInfo crash, XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = $"{crash.InstanceName} 意外退出",
            Content = (crash.Reason ?? $"游戏进程已退出，退出码 {crash.ExitCode}。")
                + "\n可以查看日志或崩溃报告定位原因。",
            CloseButtonText = "关闭",
            XamlRoot = root,
        };
        if (crash.ProcessLogDirectory is not null || crash.LogPath is not null) dialog.PrimaryButtonText = "查看日志";
        if (crash.CrashReportPath is not null) dialog.SecondaryButtonText = "查看崩溃报告";
        await _dialogGate.WaitAsync();
        try
        {
            var result = await dialog.ShowAsync();
            var path = result switch
            {
                ContentDialogResult.Primary => crash.ProcessLogDirectory ?? crash.LogPath,
                ContentDialogResult.Secondary => crash.CrashReportPath,
                _ => null,
            };
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        finally { _dialogGate.Release(); }
    }
}
