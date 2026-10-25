using System.Text.Json;

namespace Launcher.App.Services;

public sealed record InstanceUsage(int LaunchCount, DateTimeOffset? LastPlayed);
public static class InstanceUsageStore
{
    public static InstanceUsage Read(string instancePath)
    {
        try { return JsonSerializer.Deserialize<InstanceUsage>(File.ReadAllText(Path.Combine(instancePath, "97Launch", "Usage.json"))) ?? new(0, null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(0, null); }
    }
    public static void RecordLaunch(string instancePath)
    {
        try
        {
            var file = Path.Combine(instancePath, "97Launch", "Usage.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var usage = Read(instancePath);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new InstanceUsage(usage.LaunchCount + 1, DateTimeOffset.Now)));
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning(ex, "保存实例启动记录失败"); }
    }
}
