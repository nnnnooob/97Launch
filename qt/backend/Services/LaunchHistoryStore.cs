using System.Diagnostics;
using System.Text.Json;
using Launcher.App.ViewModels;

namespace Launcher.App.Services;

public enum LaunchHistoryState { Starting, Running, Completed, Failed, Cancelled, Interrupted }

public sealed record LaunchHistoryEntry(Guid Id, string InstancePath, string InstanceName,
    string GameVersion, string IconPath, DateTimeOffset StartedAt,
    LaunchHistoryState State = LaunchHistoryState.Starting,
    DateTimeOffset? RunningAt = null, DateTimeOffset? EndedAt = null,
    int? ProcessId = null, DateTimeOffset? ProcessStartedAt = null, string? Detail = null);

/// <summary>Bounded, atomic history shared by every launch entry point.</summary>
public sealed class LaunchHistoryStore
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(AppInfo.DataDirectory, "launch-history.json");
    private List<LaunchHistoryEntry> _entries = [];
    public event EventHandler? Changed;

    public LaunchHistoryStore()
    {
        try
        {
            if (File.Exists(_path))
                _entries = (JsonSerializer.Deserialize<List<LaunchHistoryEntry>>(File.ReadAllText(_path)) ?? [])
                    .OrderByDescending(e => e.StartedAt).Take(100).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning(ex, "读取启动历史失败");
            try { if (File.Exists(_path)) File.Copy(_path, _path + $".{DateTime.Now:yyyyMMddHHmmss}.bak", true); }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            { Serilog.Log.Warning(backupError, "备份启动历史失败"); }
        }
        RefreshDetachedSessions([]);
    }

    public IReadOnlyList<LaunchHistoryEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    public void Begin(Guid id, InstanceItem instance, DateTimeOffset startedAt)
    {
        lock (_sync)
        {
            _entries.Insert(0, new(id, instance.Path, instance.Name, instance.GameVersion, instance.IconPath, startedAt));
            if (_entries.Count > 100) _entries.RemoveRange(100, _entries.Count - 100);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Attach(Guid id, Process process)
    {
        DateTimeOffset? started = null;
        try { started = process.StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Update(id, e => e with { ProcessId = process.Id, ProcessStartedAt = started });
    }

    public void Running(Guid id, DateTimeOffset runningAt)
        => Update(id, e => e.EndedAt is null ? e with { State = LaunchHistoryState.Running, RunningAt = runningAt } : e);

    public void End(Guid id, LaunchHistoryState state, string? detail = null)
        => Update(id, e => e.EndedAt is not null ? e : e with
        { State = state, EndedAt = DateTimeOffset.Now, Detail = detail });

    // Sessions started before this launcher process cannot report their exit code.
    public void RefreshDetachedSessions(IEnumerable<Guid> attachedIds)
    {
        var attached = attachedIds.ToHashSet();
        bool changed = false;
        lock (_sync)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.State is not (LaunchHistoryState.Starting or LaunchHistoryState.Running)
                    || attached.Contains(entry.Id)) continue;
                if (IsOriginalProcessAlive(entry)) continue;
                _entries[i] = entry with { State = LaunchHistoryState.Interrupted, Detail = "未记录结束时间" };
                changed = true;
            }
            if (changed) Save();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsOriginalProcessAlive(LaunchHistoryEntry entry)
    {
        if (entry.ProcessId is not { } pid || entry.ProcessStartedAt is not { } started) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private void Update(Guid id, Func<LaunchHistoryEntry, LaunchHistoryEntry> update)
    {
        lock (_sync)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            var updated = update(_entries[index]);
            if (updated == _entries[index]) return;
            _entries[index] = updated;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_entries));
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Serilog.Log.Warning(ex, "保存启动历史失败"); }
    }
}
