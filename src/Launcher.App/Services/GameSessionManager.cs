using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Launcher.App.Services;

public enum GameSessionState { Starting, Running }

public sealed record GameSessionInfo(
    Guid Id,
    string InstancePath,
    string InstanceName,
    string GameVersion,
    string IconPath,
    GameSessionState State,
    string Stage,
    double? Progress,
    DateTimeOffset StartedAt,
    DateTimeOffset? RunningAt,
    int? ProcessId,
    string? GameDirectory = null);

public sealed record GameCrashInfo(
    string InstanceName,
    int ExitCode,
    string? LogPath,
    string? CrashReportPath,
    string? ProcessLogDirectory = null,
    string? Reason = null);

public sealed class GameSessionManager
{
    private sealed class Session
    {
        public required Guid Id { get; init; }
        public required string InstancePath { get; init; }
        public required string InstanceName { get; init; }
        public required string GameVersion { get; init; }
        public required string IconPath { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? RunningAt;
        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process? Process;
        public string? GameDirectory;
        public GameSessionState State = GameSessionState.Starting;
        public string Stage = "检查实例";
        public double? Progress;
        public bool StopRequested;
        public bool LegacyReadiness;
        public string? FailureReason;
        public string? ProcessLogDirectory;
        public string[] Secrets = [];
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly ILogger<GameSessionManager> _log;
    private readonly LaunchHistoryStore _history;

    public GameSessionManager(ILogger<GameSessionManager> log, LaunchHistoryStore history)
    {
        _log = log;
        _history = history;
    }

    public event EventHandler? SessionsChanged;
    public event EventHandler<GameCrashInfo>? GameCrashed;

    public IReadOnlyList<GameSessionInfo> ActiveSessions
    {
        get
        {
            lock (_sync)
                return _sessions.Values.OrderBy(session => session.StartedAt)
                    .Select(Snapshot).ToArray();
        }
    }

    public Guid Begin(InstanceItem instance)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            InstancePath = instance.Path,
            InstanceName = instance.Name,
            GameVersion = instance.GameVersion,
            IconPath = instance.IconPath,
            StartedAt = DateTimeOffset.Now,
            LegacyReadiness = Regex.IsMatch(instance.MinecraftVersionId, @"^(?:[abc]\d|rd-|inf-|old_)")
                || Version.TryParse(instance.MinecraftVersionId, out var oldVersion) && oldVersion.Major == 1 && oldVersion.Minor < 6,
        };
        lock (_sync)
        {
            _sessions.Add(session.Id, session);
        }
        _history.Begin(session.Id, instance, session.StartedAt);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return session.Id;
    }

    public void Report(Guid id, LaunchProgress update)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.State != GameSessionState.Starting
                || session.StopRequested) return;
            session.Stage = update.Stage;
            session.Progress = update.Progress;
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task Attach(Guid id, Process process, string gameDirectory)
    {
        Session session;
        lock (_sync)
        {
            session = _sessions[id];
            session.Process = process;
            session.GameDirectory = gameDirectory;
            session.Stage = "等待游戏资源加载";
            session.Progress = null;
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        _history.Attach(id, process);
        _ = MonitorAsync(session);
        return session.Ready.Task;
    }

    public void PrepareLogs(Guid id, ProcessStartInfo start, string gameDirectory, IEnumerable<string> secrets, MemoryAllocationPlan memory)
    {
        Session session;
        lock (_sync) session = _sessions[id];
        session.Secrets = secrets.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4).Distinct().ToArray();
        var name = $"{session.StartedAt:yyyyMMdd-HHmmss}-{id:N}";
        foreach (var root in new[] { Path.Combine(gameDirectory, "97Launch", "logs", "launch"), Path.Combine(AppInfo.DataDirectory, "logs", "game") })
        {
            try
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var arguments = new List<string>();
                var hideNext = false;
                foreach (var argument in start.ArgumentList)
                {
                    arguments.Add(hideNext ? "<redacted>" : Redact(argument, session.Secrets));
                    hideNext = argument is "--accessToken" or "--clientToken" or "--refreshToken" or "--password";
                }
                File.WriteAllText(Path.Combine(directory, "launch.json"), JsonSerializer.Serialize(new
                {
                    Executable = start.FileName, start.WorkingDirectory, Arguments = arguments,
                    session.InstanceName, session.StartedAt, Memory = memory,
                }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                session.ProcessLogDirectory = directory;
                _log.LogInformation("游戏进程日志目录：{Directory}", directory);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.LogWarning(ex, "无法创建游戏进程日志目录：{Root}", root); }
        }
    }

    public void Stop(Guid id)
    {
        Process? process;
        Session session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out session!)) return;
            session.StopRequested = true;
            session.Stage = "正在关闭";
            process = session.Process;
        }
        session.Ready.TrySetCanceled();
        _log.LogInformation("请求关闭游戏：{Instance}", session.InstanceName);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning(ex, "结束游戏进程失败：{Instance}", session.InstanceName);
        }
    }

    public void EndBeforeProcess(Guid id)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Process is not null) return;
            _sessions.Remove(id);
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task MonitorAsync(Session session)
    {
        var process = session.Process!;
        using var captureStop = new CancellationTokenSource();
        var output = ReadOutputAsync(session, process.StandardOutput, "stdout.log", captureStop.Token);
        var error = ReadOutputAsync(session, process.StandardError, "stderr.log", captureStop.Token);
        try
        {
            while (!process.HasExited)
            {
                if (session.LegacyReadiness && HasGameWindow(process)) MarkRunning(session);
                await Task.Delay(250).ConfigureAwait(false);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            captureStop.CancelAfter(TimeSpan.FromSeconds(2));
            await Task.WhenAll(output, error).ConfigureAwait(false);

            var exitCode = process.ExitCode;
            bool crashed;
            lock (_sync)
            {
                crashed = !session.StopRequested
                    && (session.State == GameSessionState.Starting || exitCode != 0 || session.FailureReason is not null);
                _sessions.Remove(session.Id);
            }
            _history.End(session.Id, crashed ? LaunchHistoryState.Failed : LaunchHistoryState.Completed,
                crashed ? session.FailureReason ?? $"游戏退出码 {exitCode}" : null);
            if (crashed)
            {
                session.Ready.TrySetException(new InvalidOperationException(
                    $"游戏意外退出，退出码 {exitCode}。"));
                var report = FindCrashReport(session.GameDirectory, session.StartedAt);
                var logPath = Path.Combine(session.GameDirectory ?? string.Empty, "logs", "latest.log");
                _log.LogError("Minecraft 意外退出：{Instance}，PID {Pid}，退出码 {ExitCode}",
                    session.InstanceName, process.Id, exitCode);
                GameCrashed?.Invoke(this, new GameCrashInfo(session.InstanceName, exitCode,
                    File.Exists(logPath) ? logPath : null, report, session.ProcessLogDirectory,
                    session.FailureReason ?? (session.State == GameSessionState.Starting
                        ? $"游戏在资源加载完成前退出（退出码 {exitCode}），启动未完成。" : null)));
            }
            else
            {
                session.Ready.TrySetCanceled();
                _log.LogInformation("Minecraft 已退出：{Instance}，PID {Pid}，退出码 {ExitCode}",
                    session.InstanceName, process.Id, exitCode);
            }
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            lock (_sync) _sessions.Remove(session.Id);
            _history.End(session.Id, LaunchHistoryState.Interrupted, "游戏进程监控中断");
            session.Ready.TrySetException(ex);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            _log.LogError(ex, "监控游戏进程失败：{Instance}", session.InstanceName);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task ReadOutputAsync(Session session, StreamReader reader, string filename, CancellationToken token)
    {
        StreamWriter? writer = null;
        try
        {
            if (session.ProcessLogDirectory is { } directory)
            {
                try
                {
                    writer = new StreamWriter(new FileStream(Path.Combine(directory, filename), FileMode.CreateNew,
                        FileAccess.Write, FileShare.ReadWrite, 65536, FileOptions.Asynchronous), new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _log.LogWarning(ex, "无法保存游戏进程输出：{File}", filename); }
            }
            var lines = 0;
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (writer is not null)
                {
                    try
                    {
                        await writer.WriteLineAsync($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {Redact(line, session.Secrets)}").ConfigureAwait(false);
                        if (++lines % 64 == 0) await writer.FlushAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _log.LogWarning(ex, "保存游戏进程输出中断：{File}", filename);
                        try { await writer.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception disposeError) when (disposeError is IOException or UnauthorizedAccessException) { }
                        writer = null;
                    }
                }
                var memoryFailure = line.Contains("java.lang.OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Could not reserve enough space", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("insufficient memory for the Java Runtime", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Native memory allocation", StringComparison.OrdinalIgnoreCase) && line.Contains("failed", StringComparison.OrdinalIgnoreCase);
                var fatal = line.Contains("This crash report has been saved to:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Exception in thread \"main\"", StringComparison.Ordinal)
                    || line.Contains("MixinTransformerError", StringComparison.Ordinal)
                    || line.Contains("An exception was thrown, the game will display an error screen and halt.", StringComparison.Ordinal);
                if (memoryFailure || fatal)
                {
                    lock (_sync)
                        session.FailureReason = memoryFailure ? "游戏输出报告内存不足。请检查最大堆设置、系统可用内存和提交空间，并查看进程日志。"
                            : session.FailureReason ?? "游戏输出报告致命异常，请查看进程日志中的错误堆栈。";
                }
                if ((line.Contains("Created", StringComparison.OrdinalIgnoreCase)
                        && line.Contains("textures", StringComparison.OrdinalIgnoreCase)
                        && line.Contains("-atlas", StringComparison.OrdinalIgnoreCase))
                    || line.Contains("Found animation info", StringComparison.OrdinalIgnoreCase))
                    MarkRunning(session);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (writer is not null)
            {
                try { await writer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _log.LogWarning(ex, "刷新游戏日志失败：{File}", filename); }
            }
        }
    }

    private static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets) text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
        return Regex.Replace(text, @"(?i)((?:--accessToken|--clientToken|--refreshToken|--password)(?:\s+|=))\S+", "$1<redacted>");
    }

    private void MarkRunning(Session session)
    {
        lock (_sync)
        {
            if (!_sessions.ContainsKey(session.Id) || session.State != GameSessionState.Starting
                || session.StopRequested || session.FailureReason is not null) return;
            session.State = GameSessionState.Running;
            session.RunningAt = DateTimeOffset.Now;
            session.Stage = "运行中";
            session.Progress = 1;
        }
        _history.Running(session.Id, session.RunningAt!.Value);
        session.Ready.TrySetResult(true);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool HasGameWindow(Process process)
    {
        try
        {
            process.Refresh();
            if (process.MainWindowHandle == IntPtr.Zero) return false;
            var title = process.MainWindowTitle;
            return !string.IsNullOrWhiteSpace(title)
                && !title.StartsWith("FML", StringComparison.OrdinalIgnoreCase)
                && !title.StartsWith("Quilt Loader", StringComparison.OrdinalIgnoreCase)
                && !title.StartsWith("GLFW", StringComparison.OrdinalIgnoreCase)
                && !title.EndsWith("Hidden Utility Window", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException) { return false; }
    }

    private static string? FindCrashReport(string? gameDirectory, DateTimeOffset startedAt)
    {
        if (gameDirectory is null) return null;
        var directory = Path.Combine(gameDirectory, "crash-reports");
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, "*.txt")
                .Where(path => File.GetLastWriteTimeUtc(path) >= startedAt.UtcDateTime.AddMinutes(-1))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static GameSessionInfo Snapshot(Session session) => new(
        session.Id, session.InstancePath, session.InstanceName, session.GameVersion,
        session.IconPath, session.State, session.Stage, session.Progress,
        session.StartedAt, session.RunningAt, session.Process?.Id, session.GameDirectory);
}
