namespace Launcher.App.Services;

public sealed record ToastMessage(string Title, string Detail, bool IsError = false);

/// <summary>Non-modal feedback shared by resource download entry points.</summary>
public sealed class ToastService
{
    public event Action<ToastMessage>? MessageRequested;
    private DateTime _lastDownload;
    private int _batchCount;

    public void DownloadAccepted(string resource, string target, int queuedAhead)
    {
        var now = DateTime.UtcNow;
        _batchCount = now - _lastDownload < TimeSpan.FromSeconds(2) ? _batchCount + 1 : 1;
        _lastDownload = now;
        var title = _batchCount > 1 ? $"已加入 {_batchCount} 项下载任务"
            : queuedAhead > 0 ? "已加入下载队列" : "开始下载";
        var detail = _batchCount > 1 ? "任务将按顺序处理，可在传输页面查看进度。"
            : queuedAhead > 0 ? $"{resource} → {target}，前方还有 {queuedAhead} 项任务"
            : $"{resource} → {target}";
        MessageRequested?.Invoke(new(title, detail));
        Serilog.Log.Information("{Title}：{Detail}", title, detail);
    }

    public void ShowError(string title, string detail)
    {
        _batchCount = 0;
        _lastDownload = default;
        MessageRequested?.Invoke(new(title, detail, true));
        Serilog.Log.Error("{Title}：{Detail}", title, detail);
    }

    public void Show(string title, string detail)
    {
        MessageRequested?.Invoke(new(title, detail));
        Serilog.Log.Information("{Title}：{Detail}", title, detail);
    }
}
