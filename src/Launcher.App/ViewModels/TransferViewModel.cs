using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App.ViewModels;

public enum TransferJobState { Active, Queued, Failed, Completed, Canceled }
public enum TransferStepState { Complete, Active, Waiting, Failed, Canceled }

/// <summary>FIFO transfer queue and the live state shown by the transfer page.</summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly MinecraftInstallService _installer;
    private readonly InstancesViewModel _instances;
    private readonly PendingInstanceSelection _pendingSelection;
    private readonly SettingsStore _settings;
    private readonly INavigationService _navigation;
    private readonly ToastService _toasts;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _activeCancellation;
    private bool _isProcessing;

    public ObservableCollection<TransferJob> Jobs { get; } = [];

    public double CurrentTaskProgress =>
        Jobs.FirstOrDefault(job => job.State == TransferJobState.Active)?.Progress / 100d ?? 0;
    public string CurrentTaskProgressText
    {
        get
        {
            var percent = CurrentTaskProgress * 100;
            var whole = Math.Floor(percent);
            var fraction = Math.Floor((percent - whole) * 100);
            return percent > 99.9999 ? "100 %" : $"{whole:0}.{fraction:00} %";
        }
    }
    public int RemainingTaskCount => Jobs.Count(job => job.State is TransferJobState.Active or TransferJobState.Queued);
    public int WaitingTaskCount => Jobs.Count(job => job.State == TransferJobState.Queued);
    public int ActiveTaskCount => Jobs.Count(job => job.State == TransferJobState.Active);
    public int FailedTaskCount => Jobs.Count(job => job.State == TransferJobState.Failed);
    public bool IsTransferring => ActiveTaskCount > 0;
    public Visibility ActiveProgressVisibility => IsTransferring ? Visibility.Visible : Visibility.Collapsed;
    public string SummaryTitle => IsTransferring ? "正在准备你的游戏" : FailedTaskCount > 0 ? "部分任务未完成"
        : RemainingTaskCount > 0 ? "等待开始" : Jobs.Count == 0 ? "暂无传输任务"
        : Jobs.Any(job => job.State == TransferJobState.Completed) ? "传输已完成" : "传输已结束";
    public string SummaryDetail => IsTransferring ? Jobs.First(job => job.State == TransferJobState.Active).Title
        : FailedTaskCount > 0 ? "查看下方错误信息，点击重试继续下载。"
        : Jobs.Count == 0 ? "下载游戏、模组或整合包后，可以在这里查看进度。" : "可以返回实例页面，继续你的游戏。";
    public string QueueSummary => $"进行中 {ActiveTaskCount}  ·  等待 {WaitingTaskCount}  ·  已完成 {Jobs.Count(job => job.State == TransferJobState.Completed)}"
        + (FailedTaskCount > 0 ? $"  ·  失败 {FailedTaskCount}" : string.Empty);
    public string SpeedText => Jobs.FirstOrDefault(job => job.State == TransferJobState.Active)?.SpeedText ?? "0 B/s";
    public Visibility ContentVisibility => Jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyStateVisibility => Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public TransferViewModel(
        MinecraftInstallService installer,
        InstancesViewModel instances,
        PendingInstanceSelection pendingSelection,
        SettingsStore settings,
        INavigationService navigation,
        ToastService toasts)
    {
        _installer = installer;
        _instances = instances;
        _pendingSelection = pendingSelection;
        _settings = settings;
        _navigation = navigation;
        _toasts = toasts;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Jobs.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
                foreach (TransferJob job in args.NewItems)
                    Serilog.Log.Information("传输任务已排队：{Title}，目标实例：{Instance}", job.Title, job.InstanceName);
            RefreshSummary();
        };
    }

    public void ClearTerminalJobs()
    {
        foreach (var job in Jobs.Where(job => job.State is
                     TransferJobState.Completed or TransferJobState.Failed or TransferJobState.Canceled).ToList())
        {
            Jobs.Remove(job);
        }
    }

    public void EnqueueInstall(
        string version,
        string instanceName,
        string gameDirectory,
        string? loader = null,
        string? loaderVersion = null,
        string? liteLoaderVersion = null,
        string? optiFineVersion = null,
        string? fabricApiVersion = null,
        string? optiFabricVersion = null)
    {
        App.Services.GetRequiredService<LauncherAuthorizationService>().RequireAuthorization();
        var components = new[]
        {
            loader is null ? null : $"{loader} {loaderVersion}",
            liteLoaderVersion is null ? null : "LiteLoader",
            optiFineVersion is null ? null : "OptiFine",
            fabricApiVersion is null ? null : "Fabric API",
            optiFabricVersion is null ? null : "OptiFabric",
        }.Where(item => item is not null).ToArray();
        var job = new TransferJob(
            components.Length == 0 ? $"安装 Minecraft {version}" : $"安装 {string.Join(" + ", components)} {version}",
            $"实例：{instanceName} · {version}",
            version,
            instanceName,
            gameDirectory,
            loader,
            loaderVersion,
            liteLoaderVersion,
            optiFineVersion,
            fabricApiVersion,
            optiFabricVersion)
        {
            ReturnToLaunchOnCompletion = true,
        };
        Jobs.Add(job);
        RefreshSummary();
        if (!_isProcessing) _ = ProcessQueueAsync();
    }

    public TransferJob EnqueueFileDownload(
        string title,
        string target,
        string gameDirectory,
        IReadOnlyList<DownloadFileRequest> files,
        string resourceName,
        string notificationTarget,
        string? authorizedDestinationPath = null)
    {
        var job = new TransferJob(title, target, gameDirectory, files, authorizedDestinationPath);
        AcceptResourceJob(job, resourceName, notificationTarget);
        return job;
    }

    public TransferJob EnqueueResourceInstall(string title, string target, string gameDirectory, string instanceName,
        Func<IProgress<InstallProgress>, CancellationToken, Task> install, string resourceName, string notificationTarget)
    {
        var job = new TransferJob(title, target, gameDirectory, instanceName, install)
        {
            ReturnToLaunchOnCompletion = true,
        };
        AcceptResourceJob(job, resourceName, notificationTarget);
        return job;
    }

    private void AcceptResourceJob(TransferJob job, string resourceName, string target)
    {
        App.Services.GetRequiredService<LauncherAuthorizationService>().RequireAuthorization();
        var ahead = RemainingTaskCount;
        Jobs.Add(job);
        RefreshSummary();
        // Announce acceptance before processing: a synchronous failure must remain visible.
        _toasts.DownloadAccepted(resourceName, target, ahead);
        if (!_isProcessing) _ = ProcessQueueAsync();
    }

    public bool HasPendingModification(string instanceId) => Jobs.Any(job => job.SourceInstanceId is not null
        && (job.State is TransferJobState.Active or TransferJobState.Queued)
        && (job.SourceInstanceId == instanceId || InstanceIdentity.ForPath(Path.Combine(job.GameDirectory, "versions", job.InstanceName)) == instanceId));

    public TransferJob EnqueueOperation(string title, string instanceName, string root,
        string[] stages, Func<IProgress<InstallProgress>, CancellationToken, Task> action, bool refreshInstances = false,
        string? sourceInstanceId = null, bool returnToLaunchOnCompletion = false, string? outputFilePath = null)
    {
        App.Services.GetRequiredService<LauncherAuthorizationService>().RequireAuthorization();
        if (sourceInstanceId is not null && HasPendingModification(sourceInstanceId))
            throw new InvalidOperationException("此实例已有修改任务，请在传输页面查看进度。");
        var job = new TransferJob(title, "实例：" + instanceName, root, instanceName, action)
        {
            IsMaintenance = true, RefreshInstancesOnCompletion = refreshInstances,
            SourceInstanceId = sourceInstanceId, ReturnToLaunchOnCompletion = returnToLaunchOnCompletion,
            OutputFilePath = outputFilePath,
        };
        job.Steps.Clear();
        foreach (var stage in stages) job.Steps.Add(new(stage, 1, TransferStepState.Waiting, "等待中"));
        Jobs.Add(job);
        RefreshSummary();
        if (!_isProcessing) _ = ProcessQueueAsync();
        return job;
    }

    public void CancelJob(TransferJob job)
    {
        if (job.State == TransferJobState.Active)
        {
            _activeCancellation?.Cancel();
        }
        else if (job.State == TransferJobState.Queued)
        {
            job.SetState(TransferJobState.Canceled, "已取消排队任务");
            RefreshSummary();
        }
        else
        {
            Jobs.Remove(job);
        }
    }

    public void RetryJob(TransferJob job)
    {
        App.Services.GetRequiredService<LauncherAuthorizationService>().RequireAuthorization();
        if (job.State != TransferJobState.Failed) return;
        Serilog.Log.Information("请求重试传输任务：{Title}，实例：{Instance}", job.Title, job.InstanceName);
        if (job.SourceInstanceId is not null && HasPendingModification(job.SourceInstanceId))
        {
            _toasts.ShowError("暂时无法重试", "此实例已有修改任务，请等待该任务结束。");
            return;
        }
        Jobs.Remove(job);
        job.ResetForRetry();
        Jobs.Add(job);
        RefreshSummary();
        if (!_isProcessing) _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        if (_isProcessing) return;
        _isProcessing = true;
        try
        {
            while (Jobs.FirstOrDefault(job => job.State == TransferJobState.Queued) is { } job)
            {
                _activeCancellation = new CancellationTokenSource();
                var token = _activeCancellation.Token;
                job.SetState(TransferJobState.Active, "准备下载");
                RefreshSummary();
                var loggedStages = new HashSet<int>();
                var progress = new DispatchProgress<InstallProgress>(_dispatcher, update =>
                {
                    if (job.State != TransferJobState.Active) return;
                    if (update.Stage >= 0 && update.Stage < job.Steps.Count && loggedStages.Add(update.Stage))
                        Serilog.Log.Information("传输阶段：{Title} → {Stage}", job.Title, job.Steps[update.Stage].Name);
                    job.Apply(update);
                    RefreshSummary();
                });

                try
                {
                    if (job.ResourceInstall is { } resourceInstall)
                    {
                        await resourceInstall(progress, token);
                    }
                    else if (job.FileDownloads is { } files)
                    {
                        await _installer.DownloadFilesAsync(job.GameDirectory, job.Title, files, progress, token, job.AuthorizedDestinationPath);
                    }
                    else
                    {
                        await _installer.InstallAsync(
                            new InstallRequest(job.Version, job.InstanceName, job.GameDirectory, job.Loader, job.LoaderVersion,
                                job.LiteLoaderVersion, job.OptiFineVersion, job.FabricApiVersion, job.OptiFabricVersion), progress, token);
                    }
                    if (job.FileDownloads is null && (!job.IsMaintenance || job.RefreshInstancesOnCompletion))
                    {
                        var selectResult = job.SourceInstanceId is not null
                            ? _instances.SelectedInstance?.Id == job.SourceInstanceId
                            : _settings.Load().SelectNewInstance || job.IsMaintenance;
                        await _instances.ReloadFoldersAsync();
                        if (selectResult)
                        {
                            var instancePath = Path.Combine(job.GameDirectory, "versions", job.InstanceName);
                            var instanceId = InstanceIdentity.ForPath(instancePath);
                            var folder = _instances.Folders.FirstOrDefault(item =>
                                GameFolderService.PathsEqual(item.Path, job.GameDirectory));
                            if (folder is not null)
                            {
                                var sameFolder = GameFolderService.PathsEqual(_instances.FolderSelectionPath ?? string.Empty, folder.Path);
                                _instances.SelectedFolder = folder;
                                if (!sameFolder) await _instances.LoadInstancesAsync();
                            }
                            if (_instances.AvailableInstances.FirstOrDefault(i => i.Id == instanceId) is { } updated)
                            {
                                _instances.SelectedInstance = updated;
                                Serilog.Log.Information("安装完成，已选中实例：{Instance}", updated.Name);
                            }
                            else
                                _pendingSelection.Request(instanceId);
                        }
                    }
                    job.SetState(TransferJobState.Completed,
                        job.IsMaintenance ? "操作完成" : job.FileDownloads is null ? "安装完成" : "下载完成");
                    if (job.IsMaintenance) _toasts.Show("操作完成", job.Title);
                }
                catch (OperationCanceledException ex) when (token.IsCancellationRequested || ex is UserCanceledOperationException)
                {
                    job.SetState(TransferJobState.Canceled, "已取消");
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "传输任务失败：{Title}，实例：{Instance}", job.Title, job.InstanceName);
                    job.Fail(ex.Message);
                    if (job.IsResourceJob) _toasts.ShowError(job.IsMaintenance ? "操作失败" : "下载失败", $"{job.Title}未能完成，可在传输页面查看详情。");
                }
                finally
                {
                    _activeCancellation.Dispose();
                    _activeCancellation = null;
                    RefreshSummary();
                }
                if (job.State == TransferJobState.Completed && job.ReturnToLaunchOnCompletion)
                    _navigation.Navigate(AppPage.Launch);
            }
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RemainingTaskCount));
        OnPropertyChanged(nameof(WaitingTaskCount));
        OnPropertyChanged(nameof(ActiveTaskCount));
        OnPropertyChanged(nameof(FailedTaskCount));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(CurrentTaskProgress));
        OnPropertyChanged(nameof(CurrentTaskProgressText));
        OnPropertyChanged(nameof(ContentVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(IsTransferring));
        OnPropertyChanged(nameof(ActiveProgressVisibility));
        OnPropertyChanged(nameof(SummaryTitle));
        OnPropertyChanged(nameof(SummaryDetail));
        OnPropertyChanged(nameof(QueueSummary));
    }

    private sealed class DispatchProgress<T>(DispatcherQueue dispatcher, Action<T> apply) : IProgress<T>
    {
        private readonly object _sync = new();
        private T? _latest;
        private bool _hasLatest;
        private bool _isScheduled;

        public void Report(T value)
        {
            lock (_sync)
            {
                _latest = value;
                _hasLatest = true;
                if (_isScheduled) return;
                _isScheduled = true;
            }

            if (!dispatcher.TryEnqueue(Drain))
            {
                lock (_sync)
                {
                    _latest = default;
                    _hasLatest = false;
                    _isScheduled = false;
                }
            }
        }

        private void Drain()
        {
            T? latest;
            lock (_sync)
            {
                if (!_hasLatest)
                {
                    _isScheduled = false;
                    return;
                }

                latest = _latest;
                _latest = default;
                _hasLatest = false;
                _isScheduled = false;
            }

            apply(latest!);
        }
    }
}

public sealed partial class TransferJob : ObservableObject
{
    private static readonly (string Name, double Weight)[] VanillaStages =
    [
        ("检查版本信息", 3),
        ("下载客户端与依赖库", 14),
        ("下载资源索引", 3),
        ("下载游戏资源", 14),
    ];
    private readonly IReadOnlyList<DownloadFileRequest>? _fileDownloads;

    public TransferJob(
        string title,
        string target,
        string version,
        string instanceName,
        string gameDirectory,
        string? loader = null,
        string? loaderVersion = null,
        string? liteLoaderVersion = null,
        string? optiFineVersion = null,
        string? fabricApiVersion = null,
        string? optiFabricVersion = null)
    {
        Title = title;
        Target = target;
        Version = version;
        InstanceName = instanceName;
        GameDirectory = gameDirectory;
        Loader = loader;
        LoaderVersion = loaderVersion;
        LiteLoaderVersion = liteLoaderVersion;
        OptiFineVersion = optiFineVersion;
        FabricApiVersion = fabricApiVersion;
        OptiFabricVersion = optiFabricVersion;
        var stages = VanillaStages.ToList();
        if (loader is not null || liteLoaderVersion is not null || optiFineVersion is not null
            || fabricApiVersion is not null || optiFabricVersion is not null)
            stages.Add(("安装所选加载器和组件", 6));
        stages.Add(("整理实例文件", 0.01));
        Steps = new ObservableCollection<TransferStep>(stages.Select(stage =>
            new TransferStep(stage.Name, stage.Weight, TransferStepState.Waiting, "等待中")));
    }

    public TransferJob(string title, string target, string gameDirectory, IReadOnlyList<DownloadFileRequest> files,
        string? authorizedDestinationPath = null)
    {
        Title = title;
        Target = target;
        GameDirectory = gameDirectory;
        Version = string.Empty;
        InstanceName = string.Empty;
        Loader = null;
        _fileDownloads = files;
        AuthorizedDestinationPath = authorizedDestinationPath;
        Steps = [new TransferStep("下载文件", 1, TransferStepState.Waiting, "等待中")];
    }

    public TransferJob(string title, string target, string gameDirectory, string instanceName,
        Func<IProgress<InstallProgress>, CancellationToken, Task> install)
        : this(title, target, gameDirectory, Array.Empty<DownloadFileRequest>())
    {
        InstanceName = instanceName;
        _fileDownloads = null;
        ResourceInstall = install;
        Steps = [new("下载整合包", 1, TransferStepState.Waiting, "等待中"),
            new("下载整合包组件", 5, TransferStepState.Waiting, "等待中"),
            new("安装游戏与加载器", 5, TransferStepState.Waiting, "等待中"),
            new("整理实例文件", 1, TransferStepState.Waiting, "等待中")];
    }

    public string Title { get; }
    public string Target { get; }
    public string Version { get; }
    public string InstanceName { get; }
    public string GameDirectory { get; }
    public string? AuthorizedDestinationPath { get; }
    public string? Loader { get; }
    public string? LoaderVersion { get; }
    public string? LiteLoaderVersion { get; }
    public string? OptiFineVersion { get; }
    public string? FabricApiVersion { get; }
    public string? OptiFabricVersion { get; }
    public IReadOnlyList<DownloadFileRequest>? FileDownloads => _fileDownloads;
    public Func<IProgress<InstallProgress>, CancellationToken, Task>? ResourceInstall { get; }
    public bool IsResourceJob => FileDownloads is not null || ResourceInstall is not null;
    public bool IsMaintenance { get; init; }
    public bool RefreshInstancesOnCompletion { get; init; }
    public string? SourceInstanceId { get; init; }
    public bool ReturnToLaunchOnCompletion { get; init; }
    public string? OutputFilePath { get; init; }
    public Visibility OutputFileVisibility => State == TransferJobState.Completed && OutputFilePath is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<TransferStep> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(StateMark))]
    [NotifyPropertyChangedFor(nameof(DetailsVisibility))]
    [NotifyPropertyChangedFor(nameof(RetryVisibility))]
    [NotifyPropertyChangedFor(nameof(StateIcon))]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyPropertyChangedFor(nameof(ActionIcon))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    [NotifyPropertyChangedFor(nameof(OutputFileVisibility))]
    private TransferJobState _state = TransferJobState.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private long _bytesPerSecond;

    [ObservableProperty]
    private string _detail = "等待前序任务完成";

    public string StateLabel => State switch
    {
        TransferJobState.Active => "正在处理",
        TransferJobState.Queued => "排队中",
        TransferJobState.Failed => "异常",
        TransferJobState.Completed => "已完成",
        TransferJobState.Canceled => "已取消",
        _ => string.Empty,
    };

    public string StateMark => State switch
    {
        TransferJobState.Active => "↓",
        TransferJobState.Failed => "!",
        TransferJobState.Completed => "✓",
        TransferJobState.Canceled => "×",
        _ => "…",
    };

    public Visibility DetailsVisibility => State == TransferJobState.Queued
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility RetryVisibility => State == TransferJobState.Failed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ProgressLabel => $"{Math.Floor(Progress):0}%";
    public bool HasFailed => State == TransferJobState.Failed;
    public string StateIcon => State switch
    {
        TransferJobState.Active => "\uE896",
        TransferJobState.Failed => "\uE7BA",
        TransferJobState.Completed => "\uE73E",
        TransferJobState.Canceled => "\uE711",
        _ => "\uE823",
    };
    public string ActionLabel => State is TransferJobState.Active or TransferJobState.Queued ? "取消任务" : "移除记录";
    public string ActionIcon => State is TransferJobState.Active or TransferJobState.Queued ? "\uE71A" : "\uE711";
    public string SpeedText => BytesPerSecond <= 0 ? "0 B/s" : FormatBytes(BytesPerSecond) + "/s";

    public void Apply(InstallProgress update)
    {
        var index = Math.Clamp(update.Stage, 0, Steps.Count - 1);
        BytesPerSecond = update.BytesPerSecond;
        Detail = update.Detail;
        if (update.PreserveOtherStages)
        {
            Steps[index].SetProgress(update.Progress, isActive: true);
        }
        else
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                Steps[i].SetProgress(i < index ? 1 : i == index ? update.Progress : 0,
                    isActive: i == index);
            }
        }
        var totalWeight = Steps.Sum(step => step.Weight);
        Progress = totalWeight <= 0 ? 0 : Steps.Sum(step => step.Weight * step.Progress) / totalWeight * 100;
    }

    public void SetState(TransferJobState state, string detail)
    {
        if (State != state && state != TransferJobState.Failed)
            Serilog.Log.Information("传输任务{State}：{Title}，实例：{Instance}", state switch
            {
                TransferJobState.Active => "开始", TransferJobState.Completed => "完成",
                TransferJobState.Canceled => "已取消", _ => "等待中"
            }, Title, InstanceName);
        State = state;
        Detail = detail;
        if (state == TransferJobState.Completed)
        {
            Progress = 100;
            BytesPerSecond = 0;
            foreach (var step in Steps) step.SetProgress(1);
        }
        else if (state == TransferJobState.Canceled)
        {
            foreach (var step in Steps.Where(step => step.State is TransferStepState.Active or TransferStepState.Waiting))
                step.SetState(TransferStepState.Canceled, "已取消");
            BytesPerSecond = 0;
        }
    }

    public void Fail(string detail)
    {
        SetState(TransferJobState.Failed, detail);
        var active = Steps.FirstOrDefault(step => step.State == TransferStepState.Active)
            ?? Steps.FirstOrDefault(step => step.State == TransferStepState.Waiting);
        active?.SetState(TransferStepState.Failed, detail);
        foreach (var step in Steps.Where(step => step.State == TransferStepState.Active))
            step.SetState(TransferStepState.Canceled, "已取消");
        BytesPerSecond = 0;
    }

    public void ResetForRetry()
    {
        Progress = 0;
        BytesPerSecond = 0;
        Detail = "等待前序任务完成";
        State = TransferJobState.Queued;
        foreach (var step in Steps) step.ResetForRetry();
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double amount = value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return $"{amount:0.#} {units[unit]}";
    }
}

public sealed partial class TransferStep : ObservableObject
{
    public TransferStep(string name, double weight, TransferStepState state, string detail)
    {
        Name = name;
        Weight = weight;
        State = state;
        Detail = detail;
    }

    public string Name { get; private set; }
    public double Weight { get; }

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateMark))]
    [NotifyPropertyChangedFor(nameof(CompactDetail))]
    private TransferStepState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactDetail))]
    private string _detail;

    public string CompactDetail => State == TransferStepState.Failed ? "失败" : Detail;

    public string StateMark => State switch
    {
        TransferStepState.Complete => "√",
        TransferStepState.Active => "↓",
        TransferStepState.Failed => "!",
        TransferStepState.Canceled => "×",
        _ => "○",
    };

    public void SetState(TransferStepState state, string detail)
    {
        State = state;
        Detail = detail;
        if (state == TransferStepState.Complete) Progress = 1;
        else if (state == TransferStepState.Waiting) Progress = 0;
    }

    public void SetProgress(double progress, bool isActive = true)
    {
        Progress = Math.Clamp(progress, 0, 1);
        SetState(Progress >= 1 ? TransferStepState.Complete : isActive ? TransferStepState.Active : TransferStepState.Waiting,
            Progress >= 1 ? "√" : isActive ? $"{Math.Floor(Progress * 100):0}%" : "等待中");
    }

    public void ResetForRetry()
    {
        Progress = 0;
        SetState(TransferStepState.Waiting, "等待中");
    }
}
