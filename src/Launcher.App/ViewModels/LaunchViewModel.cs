using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Launcher.App.Services;
using Launcher.App.Services.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App.ViewModels;

/// <summary>Live status shown on the launch page.</summary>
public partial class LaunchViewModel : ObservableObject
{
    private readonly AccountManager _accounts;
    private readonly SettingsStore _settings;
    private readonly InstanceLaunchSettingsStore _instanceSettings;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly MinecraftLaunchService _launcher;
    private readonly GameSessionManager _sessions;
    private InstanceItem? _instance;
    private string _readinessReason = "未选择实例";
    private CancellationTokenSource? _launchCancellation;
    private Guid? _launchSessionId;
    private bool _hasCompatibleJavaRuntime;
    private bool _isCheckingJavaRuntime;
    private bool _javaCheckFailed;
    private bool _javaVersionMismatch;
    private string _javaRecommendation = "";
    private DateTimeOffset? _lastPlayedAt;
    private int _javaDetectionId;

    public LaunchViewModel(
        AccountManager accounts,
        SettingsStore settings,
        InstanceLaunchSettingsStore instanceSettings,
        SettingsViewModel settingsViewModel,
        MinecraftLaunchService launcher,
        GameSessionManager sessions,
        JavaRuntimeChangeNotifier runtimeChanges)
    {
        _accounts = accounts;
        _settings = settings;
        _instanceSettings = instanceSettings;
        _settingsViewModel = settingsViewModel;
        _launcher = launcher;
        _sessions = sessions;
        _sessions.SessionsChanged += (_, _) =>
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher is null) return;
            if (dispatcher.HasThreadAccess) SyncGameSessions();
            else dispatcher.TryEnqueue(SyncGameSessions);
        };
        var durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        durationTimer.Tick += (_, _) =>
        {
            foreach (var item in GameSessions) item.RefreshDuration();
            UpdateLastPlayedDisplay();
        };
        durationTimer.Start();
        SyncGameSessions();
        runtimeChanges.RuntimesChanged += OnJavaRuntimesChanged;
        _settingsViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.SelectedJavaPath))
            {
                _ = DetectJavaAsync();
            }
        };
        _accounts.Changed += (_, _) => RefreshAccountStatus();
        RefreshAccountStatus();
    }

    private void OnJavaRuntimesChanged(object? sender, EventArgs e)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess)
            _ = DetectJavaAsync();
        else
            dispatcher.TryEnqueue(() => _ = DetectJavaAsync());
    }

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private string _instanceName = "未选择实例";

    [ObservableProperty]
    private string? _selectedInstanceId;

    [ObservableProperty]
    private string? _selectedInstancePath;

    [ObservableProperty]
    private string _instanceIconPath = "ms-appx:///Assets/Blocks/vanilla.png";

    [ObservableProperty]
    private bool _isInstanceReady;

    [ObservableProperty]
    private string _gameVersion = "—";

    [ObservableProperty]
    private string _loginMode = "未选择账号";

    [ObservableProperty]
    private string _loaderDisplay = "—";

    [ObservableProperty]
    private string _javaVersion = "暂无对应 Java";

    [ObservableProperty]
    private string _javaHint = "未找到适用于此实例的 Java。";

    [ObservableProperty]
    private bool _isVersionIsolated;

    [ObservableProperty]
    private int _installedInstanceCount;

    [ObservableProperty]
    private int _accountCount;

    [ObservableProperty]
    private string _lastPlayedDisplay = "从未启动";

    public bool HasPlaceholderInstance => _instance is null;

    public ObservableCollection<RunningGameItem> GameSessions { get; } = [];

    public Visibility EmptyGamesVisibility => GameSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StopAllVisibility => GameSessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public bool HasLaunchPrerequisites => _instance is { HasError: false }
        && Directory.Exists(_instance.Path)
        && _accounts.Current is { IsUsable: true };

    public bool RequiresJavaInstallation => HasLaunchPrerequisites
        && !_isCheckingJavaRuntime
        && !_javaCheckFailed
        && !_hasCompatibleJavaRuntime;

    public bool HasJavaVersionMismatch => _javaVersionMismatch;

    public string LastPlayedHint => _lastPlayedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "此实例尚无成功启动记录。";

    public bool JavaInstallRequiresAutomaticMode => _instance is not null
        && _instanceSettings.Load(_instance.Path, _settings.Load()).JavaMode != "自动选择并按需下载";

    public bool CanLaunch => !IsLaunching && HasLaunchPrerequisites && !_isCheckingJavaRuntime;

    public string LaunchButtonText => IsLaunching ? "正在启动实例" : "启动实例";

    public string ReadinessDisplay => _readinessReason;

    public string IsolationDisplay => IsVersionIsolated ? "版本隔离" : "未隔离";

    public void SetInstalledInstanceCount(int count)
        => InstalledInstanceCount = count;

    public void RefreshInstanceStatus()
    {
        RefreshIsolationStatus();
        ReadLastPlayed();
        _ = DetectJavaAsync();
        OnPropertyChanged(nameof(JavaInstallRequiresAutomaticMode));
        RefreshReadiness();
    }

    public void ApplyInstance(InstanceItem instance)
    {
        var changed = _instance?.Path != instance.Path;
        _instance = instance;
        _settingsViewModel.SetMemoryInstance(instance);
        SelectedInstanceId = instance.Id;
        SelectedInstancePath = instance.Path;
        PersistSelectedInstance(instance.Path);
        if (changed) Serilog.Log.Information("当前启动实例：{Instance}（{Version} · {Loader}）", instance.Name, instance.GameVersion, instance.Loader);
        InstanceName = instance.Name;
        GameVersion = instance.GameVersion;
        InstanceIconPath = instance.IconPath;
        LoaderDisplay = instance.LoaderVersion;
        ReadLastPlayed();
        OnPropertyChanged(nameof(HasPlaceholderInstance));
        RefreshIsolationStatus();
        _ = DetectJavaAsync();
        OnPropertyChanged(nameof(JavaInstallRequiresAutomaticMode));
        RefreshReadiness();
    }

    public void ClearInstance()
    {
        _instance = null;
        _settingsViewModel.SetMemoryInstance(null);
        Interlocked.Increment(ref _javaDetectionId);
        _hasCompatibleJavaRuntime = false;
        _isCheckingJavaRuntime = false;
        _javaCheckFailed = false;
        _javaVersionMismatch = false;
        _javaRecommendation = "";
        SelectedInstanceId = null;
        SelectedInstancePath = null;
        PersistSelectedInstance("");
        InstanceName = "未选择实例";
        GameVersion = "—";
        LoaderDisplay = "—";
        InstanceIconPath = "ms-appx:///Assets/Blocks/vanilla.png";
        JavaVersion = "暂无对应 Java";
        JavaHint = "请先选择实例。";
        _lastPlayedAt = null;
        UpdateLastPlayedDisplay();
        IsVersionIsolated = false;
        _readinessReason = "未选择实例";
        OnPropertyChanged(nameof(HasPlaceholderInstance));
        OnPropertyChanged(nameof(ReadinessDisplay));
        RefreshAccountStatus();
        RefreshReadiness();
    }

    private void RefreshAccountStatus()
    {
        var account = _accounts.Current;
        AccountCount = _accounts.Accounts.Count;
        LoginMode = account is null
            ? "未选择账号"
            : $"{account.Name} · {account.TypeDisplay}";
        _ = DetectJavaAsync();
        RefreshReadiness();
    }

    private async Task DetectJavaAsync()
    {
        if (_instance is { } instance)
        {
            await DetectCompatibleJavaAsync(instance);
            return;
        }

        var requestId = Interlocked.Increment(ref _javaDetectionId);
        var settings = _settings.Load();
        var runtimes = await JavaRuntimeDetector.DetectAsync(
            new[] { settings.SelectedJavaPath }.Concat(settings.AdditionalJavaPaths ?? []),
            managedRuntimeRoots: settings.ManagedJavaRuntimeDirectories, gameDirectory: settings.SelectedGameFolderPath);
        if (requestId != _javaDetectionId || _instance is not null) return;
        var selected = runtimes.FirstOrDefault(item =>
            JavaRuntimeDetector.IsSameRuntime(item.Path, settings.SelectedJavaPath))
            ?? runtimes.FirstOrDefault();
        _hasCompatibleJavaRuntime = selected is not null;
        _javaCheckFailed = false;
        _javaVersionMismatch = false;
        JavaVersion = selected is null
            ? "暂无对应 Java"
            : $"Java {selected.Version}";
        JavaHint = selected?.Path ?? "未找到 Java，请手动添加或启动时按需安装。";
        RefreshReadiness();
    }

    private async Task DetectCompatibleJavaAsync(InstanceItem instance)
    {
        var requestId = Interlocked.Increment(ref _javaDetectionId);
        _isCheckingJavaRuntime = true;
        _javaCheckFailed = false;
        _javaVersionMismatch = false;
        JavaVersion = "正在检查 Java…";
        JavaHint = "正在检查实例所需的 Java 版本。";
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(RequiresJavaInstallation));
        RefreshReadiness();
        try
        {
            var check = await _launcher.CheckInstalledJavaAsync(instance);
            if (requestId != _javaDetectionId || !ReferenceEquals(_instance, instance)) return;
            _hasCompatibleJavaRuntime = check.IsCompatible;
            _javaVersionMismatch = check.Runtime is not null && !check.IsCompatible;
            _javaRecommendation = check.Recommendation;
            JavaVersion = check.Runtime is not { } runtime ? "暂无对应 Java"
                : check.IsCompatible ? $"Java {runtime.Version}" : $"Java {runtime.Version} · 不兼容";
            JavaHint = check.IsCompatible ? check.Runtime!.Path
                : _javaVersionMismatch
                    ? $"Java {check.Runtime!.Version} 版本不匹配。{check.Recommendation}。\n{check.Runtime.Path}"
                    : $"未找到适用于此实例的 Java。{check.Recommendation}。";
        }
        catch (Exception ex)
        {
            if (requestId != _javaDetectionId || !ReferenceEquals(_instance, instance)) return;
            _hasCompatibleJavaRuntime = false;
            _javaCheckFailed = true;
            JavaVersion = "Java 检查失败";
            JavaHint = ex.Message;
            Serilog.Log.Warning(ex, "检查实例 Java 失败：{Instance}", instance.Name);
        }
        finally
        {
            if (requestId == _javaDetectionId && ReferenceEquals(_instance, instance))
            {
                _isCheckingJavaRuntime = false;
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(RequiresJavaInstallation));
                OnPropertyChanged(nameof(HasJavaVersionMismatch));
                RefreshReadiness();
            }
        }
    }

    public async Task<LaunchResult> StartLaunchAsync(CatalogServer? server = null)
    {
        var instance = _instance;
        if (instance is null || !HasLaunchPrerequisites) throw new InvalidOperationException(ReadinessDisplay);
        if (_launchCancellation is not null) throw new InvalidOperationException("启动任务正在运行。");

        var sessionId = _sessions.Begin(instance);
        string? lastStage = null;
        _launchSessionId = sessionId;
        _launchCancellation = new CancellationTokenSource();
        IsLaunching = true;
        StatusText = "正在准备启动…";
        try
        {
            var reporter = new Progress<LaunchProgress>(update =>
            {
                if (_launchSessionId != sessionId) return;
                _sessions.Report(sessionId, update);
                if (lastStage != update.Stage)
                {
                    lastStage = update.Stage;
                    Serilog.Log.Information("启动实例 {Instance}：{Stage}", instance.Name, update.Stage);
                }
            });
            var result = await _launcher.StartAsync(instance, reporter, sessionId, _launchCancellation.Token, server);
            StatusText = "游戏已启动";
            if (SelectedInstanceId == instance.Id)
            {
                ReadLastPlayed();
                _hasCompatibleJavaRuntime = true;
                _javaCheckFailed = false;
                _javaVersionMismatch = false;
                JavaVersion = $"Java {result.JavaVersion}";
                _ = DetectJavaAsync();
            }
            RefreshReadiness();
            return result;
        }
        catch (OperationCanceledException ex) when (_launchCancellation.IsCancellationRequested || ex is UserCanceledOperationException)
        {
            StatusText = "启动已取消";
            App.Services.GetRequiredService<LaunchHistoryStore>().End(sessionId, LaunchHistoryState.Cancelled);
            throw;
        }
        catch (Exception)
        {
            StatusText = "启动失败";
            App.Services.GetRequiredService<LaunchHistoryStore>().End(sessionId, LaunchHistoryState.Failed,
                "启动未完成，可查看日志了解详情");
            throw;
        }
        finally
        {
            _sessions.EndBeforeProcess(sessionId);
            _launchCancellation.Dispose();
            _launchCancellation = null;
            _launchSessionId = null;
            IsLaunching = false;
            RefreshReadiness();
        }
    }

    public void CancelLaunch()
    {
        _launchCancellation?.Cancel();
        if (_launchSessionId is { } id) _sessions.Stop(id);
    }

    public void StopGame(Guid id)
    {
        if (_launchSessionId == id) CancelLaunch();
        else _sessions.Stop(id);
    }

    public void StopAllGames()
    {
        foreach (var session in _sessions.ActiveSessions) StopGame(session.Id);
    }

    private void SyncGameSessions()
    {
        var active = _sessions.ActiveSessions;
        foreach (var old in GameSessions.ToArray())
            if (active.All(item => item.Id != old.Id)) GameSessions.Remove(old);
        foreach (var info in active)
        {
            var row = GameSessions.FirstOrDefault(item => item.Id == info.Id);
            if (row is null) GameSessions.Add(new RunningGameItem(info));
            else row.Update(info);
        }
        OnPropertyChanged(nameof(EmptyGamesVisibility));
        OnPropertyChanged(nameof(StopAllVisibility));
        RefreshReadiness();
    }

    public void UseAutomaticJavaForCurrentInstance()
    {
        if (_instance is null) return;
        var settings = _instanceSettings.Load(_instance.Path, _settings.Load());
        settings.JavaMode = "自动选择并按需下载";
        _instanceSettings.Save(_instance.Path, settings);
        OnPropertyChanged(nameof(JavaInstallRequiresAutomaticMode));
    }

    private void PersistSelectedInstance(string path)
    {
        var saved = _settings.Load();
        if (string.Equals(saved.SelectedInstancePath, path, StringComparison.OrdinalIgnoreCase)) return;
        saved.SelectedInstancePath = path;
        _settings.Save(saved);
    }

    private void ReadLastPlayed()
    {
        _lastPlayedAt = _instance is null ? null : InstanceUsageStore.Read(_instance.Path).LastPlayed;
        UpdateLastPlayedDisplay();
    }

    private void UpdateLastPlayedDisplay()
    {
        if (_lastPlayedAt is not { } last)
            LastPlayedDisplay = "从未启动";
        else
        {
            var elapsed = DateTimeOffset.Now - last;
            LastPlayedDisplay = elapsed.TotalSeconds < -60 ? last.ToLocalTime().ToString("yyyy-MM-dd")
                : elapsed.TotalMinutes < 1 ? "刚刚"
                : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} 分钟前"
                : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} 小时前"
                : elapsed.TotalDays < 7 ? $"{(int)elapsed.TotalDays} 天前"
                : last.ToLocalTime().ToString("yyyy-MM-dd");
        }
        OnPropertyChanged(nameof(LastPlayedHint));
    }

    private void RefreshIsolationStatus()
    {
        if (_instance is null)
        {
            IsVersionIsolated = false;
            return;
        }

        var global = _settings.Load();
        var settings = _instanceSettings.Load(_instance.Path, global);
        var hasInstanceData = Directory.Exists(Path.Combine(_instance.Path, "mods"))
            || Directory.Exists(Path.Combine(_instance.Path, "saves"));

        IsVersionIsolated = hasInstanceData || settings.VersionIsolation switch
        {
            "关闭" => false,
            "隔离可安装 Mod 的版本" => _instance.IsModdable,
            "隔离非正式版" => !_instance.IsRelease,
            "隔离可安装 Mod 的版本与非正式版" => _instance.IsModdable || !_instance.IsRelease,
            _ => true,
        };
        OnPropertyChanged(nameof(IsolationDisplay));
    }

    private void RefreshReadiness()
    {
        var selectedSession = _sessions.ActiveSessions
            .Where(item => string.Equals(item.InstancePath, _instance?.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.State == GameSessionState.Starting)
            .FirstOrDefault();
        if (selectedSession is not null)
        {
            _readinessReason = selectedSession.State == GameSessionState.Starting ? "启动中" : "运行中";
        }
        else if (_instance is null)
        {
            _readinessReason = "未选择实例";
        }
        else if (_instance.HasError || !Directory.Exists(_instance.Path))
        {
            _readinessReason = "实例文件异常";
        }
        else if (_accounts.Current is not { IsUsable: true })
        {
            _readinessReason = "未选择可用账号";
        }
        else if (_isCheckingJavaRuntime)
        {
            _readinessReason = "正在检查 Java";
        }
        else if (!_hasCompatibleJavaRuntime)
        {
            _readinessReason = _javaCheckFailed ? "Java 检查失败"
                : _javaVersionMismatch ? $"Java 版本不匹配，{_javaRecommendation.Split('（')[0]}"
                : "未检测到 Java";
        }
        else
        {
            _readinessReason = "就绪";
        }

        IsInstanceReady = _readinessReason is "就绪" or "启动中" or "运行中";
        OnPropertyChanged(nameof(ReadinessDisplay));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(RequiresJavaInstallation));
    }

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
        RefreshReadiness();
    }
}
