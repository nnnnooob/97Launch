using System.ComponentModel;
using System.Runtime.InteropServices;
using Launcher.App.ViewModels;

namespace Launcher.App.Services;

public sealed record MemoryAllocationPlan(int HeapMb, double TotalGb, double AvailableGb,
    double AvailableCommitGb, int ModCount, double MinimumGb)
{
    public double HeapGb => HeapMb / 1024d;
    public bool IsLow => HeapGb + .5 > AvailableGb || HeapGb < MinimumGb
        || HeapGb + 1 > AvailableCommitGb;
    public string Summary => $"系统可用 {AvailableGb:0.0} / {TotalGb:0.0} GB"
        + (ModCount > 0 ? $" · {ModCount} 个 Mod" : string.Empty)
        + (IsLow ? " · 内存紧张" : string.Empty);
}

public static class MemoryAllocationService
{
    public static MemoryAllocationPlan Calculate(InstanceItem? instance, InstanceLaunchSettings options, LauncherSettings global)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统可用内存。");
        const double gb = 1024d * 1024 * 1024;
        var available = status.AvailablePhysical / gb;
        var mods = 0;
        if (instance?.IsModdable == true)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(instance.Path))!)!;
            var directory = Path.Combine(MinecraftLaunchService.ResolveGameDirectory(instance, root, options, global), "mods");
            if (Directory.Exists(directory))
                mods = Directory.EnumerateFiles(directory).Count(path => Path.GetExtension(path).ToLowerInvariant() is ".jar" or ".zip" or ".litemod");
        }
        var minimum = instance?.IsModdable == true ? .5 + mods / 150d : .5;
        var target1 = instance?.IsModdable == true ? 1.5 + mods / 90d : 1.5;
        var target2 = instance?.IsModdable == true ? 2.7 + mods / 50d : instance?.Loader.Contains("OptiFine", StringComparison.OrdinalIgnoreCase) == true ? 3 : 2.5;
        var target3 = instance?.IsModdable == true ? 4.5 + mods / 25d : instance?.Loader.Contains("OptiFine", StringComparison.OrdinalIgnoreCase) == true ? 5 : 4;
        var give = options.CustomMemoryGb;
        if (options.IsAutoMemory)
        {
            // PCL GetRam: allocate each demand tier at 100%, 70%, 40%, then 15%.
            var remaining = Math.Round(available, 1);
            give = 0;
            foreach (var (delta, ratio) in new[] { (target1, 1d), (target2 - target1, .7), (target3 - target2, .4), (target3, .15) })
            {
                give += Math.Min(remaining * ratio, delta);
                remaining -= delta / ratio;
                if (remaining < .1) break;
            }
            give = Math.Round(Math.Max(give, minimum), 1);
        }
        var cap = double.IsFinite(global.MaxMemoryGb) ? Math.Max(.5, global.MaxMemoryGb) : 16;
        give = double.IsFinite(give) ? Math.Clamp(give, .5, cap) : .5;
        return new((int)Math.Floor(give * 1024), status.TotalPhysical / gb, available,
            status.AvailablePageFile / gb, mods, minimum);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
