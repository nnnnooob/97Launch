using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Launcher.App.Services;

internal static class WebViewRuntimeInstaller
{
    private const string Client = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const int Limit = 10 * 1024 * 1024;
    private static void Report(string detail) => Program.Send(new { type = "progress", detail });
    private static bool Installed()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(Client);
            if (Version.TryParse(key?.GetValue("pv") as string, out var version) && version > new Version(0, 0, 0, 0)) return true;
        }
        return false;
    }
    internal static async Task<object> EnsureAsync(CancellationToken token)
    {
        if (Installed()) return new { installed = true };
        var root = Path.Combine(AppInfo.DataDirectory, "installers");
        Directory.CreateDirectory(root);
        FileStream? installLock = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            while (installLock is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (Installed()) return new { installed = true };
                try { installLock = new FileStream(Path.Combine(root, "webview2.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { Report("正在等待另一项 WebView2 安装完成…"); await Task.Delay(1000, timeout.Token); }
            }
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "MicrosoftEdgeWebview2Setup.exe");
            try
            {
                Report("正在从微软下载 WebView2 安装程序…");
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
                var url = new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                for (var redirects = 0; ; redirects++)
                {
                    if (url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo) ||
                        !(url.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("WebView2 安装程序下载地址不是微软 HTTPS 地址。");
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        if (redirects >= 8 || response.Headers.Location is null) throw new IOException("WebView2 下载重定向无效。");
                        url = new Uri(url, response.Headers.Location); continue;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > Limit) throw new IOException("WebView2 引导程序大小异常。");
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    var buffer = new byte[65536]; var total = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(45), timeout.Token);
                        if (read == 0) break;
                        total += read;
                        if (total > Limit) throw new IOException("WebView2 引导程序超出大小限制。");
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }
                    if (total < 65536) throw new IOException("WebView2 引导程序不完整。");
                    break;
                }
                Report("正在验证微软安装程序签名…");
                VerifyPublisher(path);
                timeout.Token.ThrowIfCancellationRequested();
                Report("正在安装 WebView2 Runtime，请稍候…");
                var start = new ProcessStartInfo(path) { Arguments = "/silent /install", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
                using var installer = Process.Start(start) ?? throw new IOException("无法启动 WebView2 安装程序。");
                await installer.WaitForExitAsync(timeout.Token);
                for (var attempt = 0; attempt < 20 && !Installed(); attempt++) await Task.Delay(1000, timeout.Token);
                if (!Installed()) throw new IOException($"WebView2 未能安装完成（退出码 0x{installer.ExitCode:X8}）。请联网重试，或稍后手动安装。" );
                Report("WebView2 Runtime 已安装");
                Serilog.Log.Information("首次准备：WebView2 Runtime 已安装");
                return new { installed = true };
            }
            finally
            {
                // Cancellation stops waiting, without terminating a system component installer.
                try { File.Delete(path); Directory.Delete(directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning(ex, "保留 WebView2 安装临时文件以待系统安装结束"); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("WebView2 安装等待超时，可以稍后重试。"); }
        finally { installLock?.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData { public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Union; public IntPtr File; public uint State; public IntPtr StateData, Url; public uint Flags, Context; public IntPtr Signature; }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    private static void VerifyPublisher(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Revocation = 1, Union = 1, File = memory, State = 1, Flags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        Marshal.StructureToPtr(file, memory, false);
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new IOException("WebView2 安装程序的 Windows 签名验证失败，已停止执行。");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (certificate.GetNameInfo(X509NameType.SimpleName, false) != "Microsoft Corporation") throw new IOException("WebView2 安装程序不是由微软签发，已停止执行。");
        }
        finally { data.State = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); Marshal.DestroyStructure<TrustFile>(memory); Marshal.FreeHGlobal(memory); }
    }
}
