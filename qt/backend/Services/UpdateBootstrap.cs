using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.App.Services;

internal sealed record UpdatePlan(string Target, int ParentId, long ParentStartedAt, string OldHash, UpdateRelease Release, string HelperHash);
internal sealed record UpdateResult(bool Success, string Version, string Message);

internal static class UpdateBootstrap
{
    internal static string? StartupTicket;
    internal static int HostParentId;
    private static string? _preparedSession;
    internal static bool RestartPending => _preparedSession is not null;
    internal static string ResultPath => Path.Combine(UpdateTrust.Root, "last-result.json");
    internal static string FailedBuildPath => Path.Combine(UpdateTrust.Root, "failed-build.json");

    internal static bool HandleArguments(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update")
        {
            RunAsync(args[1]).GetAwaiter().GetResult();
            return true;
        }
        if (args.Length == 2 && args[0] == "--update-ticket") StartupTicket = ValidateSession(args[1]);
        return false;
    }

    private static string ValidateSession(string path)
    {
        var full = Path.GetFullPath(path);
        var roots = new[] { Path.Combine(UpdateTrust.Root, "sessions"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "97Launch", "updates", "sessions") };
        if (!roots.Any(root => full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(full) != "plan.json" ||
            !Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(full)), "N", out _))
            throw new InvalidDataException("更新会话路径无效。");
        return full;
    }

    internal static void AcknowledgeStartup()
    {
        if (StartupTicket is not { } ticket) return;
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(ticket), UpdateTrust.Json)!;
            if (AppInfo.BuildNumber == plan.Release.Build &&
                Path.GetFullPath(AppInfo.TargetExecutable).Equals(Path.GetFullPath(plan.Target), StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(ticket)!, "ready"), AppInfo.Version);
        }
        catch (Exception ex) { Serilog.Log.Warning("写入更新启动确认失败：{ErrorType}", ex.GetType().Name); }
    }

    internal static async Task PrepareAsync(UpdateRelease release)
    {
        await UpdateTrust.VerifyFileAsync(release, UpdateTrust.FilePath(release));
        var target = AppInfo.TargetExecutable;
        var root = Path.GetDirectoryName(target)!;
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < release.Size * 3 ||
            new DriveInfo(Path.GetPathRoot(UpdateTrust.Root)!).AvailableFreeSpace < release.Size * 5)
            throw new IOException("更新需要更多磁盘空间，请至少预留约 1 GB。");
        var probe = Path.Combine(root, ".97launch-write-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe)) { } }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        var session = Path.Combine(UpdateTrust.Root, "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var helper = Path.Combine(session, "97Launch-update.exe");
        await using var current = File.OpenRead(target);
        var oldHash = Convert.ToHexString(await SHA256.HashDataAsync(current));
        File.Copy(Environment.ProcessPath!, helper);
        using var process = Process.GetProcessById(HostParentId);
        var plan = Path.Combine(session, "plan.json");
        UpdateTrust.Save(plan, new UpdatePlan(target, process.Id, process.StartTime.ToUniversalTime().Ticks, oldHash, release, HashFile(helper)));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = session, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(plan);
        File.Delete(ResultPath);
        using var updater = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
        var helperReady = Path.Combine(session, "helper-ready");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(helperReady) && !updater.HasExited && DateTime.UtcNow < deadline) await Task.Delay(150);
        if (!File.Exists(helperReady)) throw new IOException("更新程序未能就绪，当前应用保持运行。");
        _preparedSession = session;
    }

    internal static void CommitPreparedUpdate()
    {
        if (_preparedSession is null) throw new InvalidOperationException("更新会话尚未准备好。");
        File.WriteAllText(Path.Combine(_preparedSession, "commit"), "apply");
        _preparedSession = null;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Process Start(string path, string? ticket = null)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) };
        if (ticket is not null) { start.ArgumentList.Add("--update-ticket"); start.ArgumentList.Add(ticket); }
        return Process.Start(start) ?? throw new IOException("无法启动应用。");
    }

    private static async Task RunAsync(string input)
    {
        UpdatePlan? plan = null;
        string? candidate = null;
        string? backup = null;
        var replaced = false;
        var parentExited = false;
        var targetValidated = false;
        var bootConfirmed = false;
        Process? child = null;
        try
        {
            var ticket = ValidateSession(input);
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(ticket), UpdateTrust.Json) ?? throw new InvalidDataException();
            UpdateTrust.Verify(plan.Release);
            if (!Path.IsPathFullyQualified(plan.Target) || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedAt ||
                !Path.GetFullPath(parent.MainModule!.FileName).Equals(Path.GetFullPath(plan.Target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新父进程不匹配。");
            if (HashFile(plan.Target) != plan.OldHash || HashFile(Environment.ProcessPath!) != plan.HelperHash)
                throw new InvalidDataException("原程序校验失败。");
            targetValidated = true;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(ticket)!, "helper-ready"), "ready");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
            parentExited = true;
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(ticket)!, "commit")))
            { targetValidated = false; throw new IOException("更新未确认，已停止替换。"); }
            var lockPath = Path.Combine(UpdateTrust.Root, "apply.lock");
            using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            {
                if (HashFile(plan.Target) != plan.OldHash) { targetValidated = false; throw new InvalidDataException("原程序已变更，更新停止。"); }
                await UpdateTrust.VerifyFileAsync(plan.Release, UpdateTrust.FilePath(plan.Release));
                backup = Path.Combine(Path.GetDirectoryName(ticket)!, "previous.exe");
                File.Copy(plan.Target, backup, true);
                if (HashFile(backup) != plan.OldHash) throw new IOException("上一版备份校验失败，已停止更新。");
                candidate = Path.Combine(Path.GetDirectoryName(plan.Target)!, ".97launch-new-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(UpdateTrust.FilePath(plan.Release), candidate);
                await UpdateTrust.VerifyFileAsync(plan.Release, candidate);
                File.Replace(candidate, plan.Target, null);
                replaced = true;
                child = Start(plan.Target, ticket);
                var ready = Path.Combine(Path.GetDirectoryName(ticket)!, "ready");
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!File.Exists(ready) && !child.HasExited && DateTime.UtcNow < deadline) await Task.Delay(250);
                if (!File.Exists(ready)) throw new IOException("新版未能完成启动，已恢复上一版本。");
                bootConfirmed = true;
                UpdateTrust.Save(ResultPath, new UpdateResult(true, plan.Release.Version, "更新成功"));
                File.Delete(Path.Combine(UpdateTrust.Root, "ready.json"));
                File.Delete(FailedBuildPath);
            }
        }
        catch (Exception ex)
        {
            if (bootConfirmed) return;
            try
            {
                if (replaced && plan is not null && backup is not null)
                {
                    if (child is { HasExited: false }) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                    candidate ??= Path.Combine(Path.GetDirectoryName(plan.Target)!, ".97launch-rollback-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(backup, candidate, true);
                    File.Replace(candidate, plan.Target, null);
                    UpdateTrust.Save(FailedBuildPath, plan.Release.Build);
                }
                UpdateTrust.Save(ResultPath, new UpdateResult(false, plan?.Release.Version ?? "", ex.Message));
                if (targetValidated && parentExited && plan is not null && File.Exists(plan.Target)) _ = Start(plan.Target);
            }
            catch (Exception recovery)
            {
                File.AppendAllText(Path.Combine(UpdateTrust.Root, "recovery.log"), DateTimeOffset.Now + " " + recovery.GetType().Name + Environment.NewLine);
            }
        }
        finally { if (candidate is not null && File.Exists(candidate)) File.Delete(candidate); child?.Dispose(); }
    }
}
