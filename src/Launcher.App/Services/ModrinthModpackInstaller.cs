using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Services;

public sealed class ModrinthModpackInstaller(MinecraftInstallService installer, SettingsStore settings, InstanceLaunchSettingsStore instanceSettings,
    CurseForgeResourceService curseForge)
{
    public async Task InstallAsync(ModrinthFile file, string versionId, string gameDirectory, string name,
        IProgress<InstallProgress> progress, CancellationToken cancellationToken, string source = "Modrinth")
    {
        ValidateName(name);
        var instanceDirectory = Path.Combine(gameDirectory, "versions", name);
        var marker = Path.Combine(instanceDirectory, ".97launch-installing");
        var identity = source + ":" + versionId;
        var cacheName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        var archivePath = Path.Combine(gameDirectory, "97Launch", "cache", "modpacks", cacheName + (source == "CurseForge" ? ".zip" : ".mrpack"));
        if (Directory.Exists(instanceDirectory) && (!File.Exists(marker) || await File.ReadAllTextAsync(marker, cancellationToken) != identity))
            throw new InvalidOperationException($"实例“{name}”已存在，请更换名称。");
        await installer.DownloadFilesAsync(gameDirectory, "下载整合包", [new(archivePath, "整合包", file.Sha1, file.Size, CommunityResourceService.DownloadSources(file.Url).ToArray(), file.Md5)],
            new StageProgress(progress, 0), cancellationToken);
        CurseForgePackPlan? cursePlan = source == "CurseForge" ? await Task.Run(() => CurseForgePackReader.Read(archivePath), cancellationToken) : null;
        var plan = cursePlan is null ? await Task.Run(() => ModrinthPackReader.Read(archivePath, instanceDirectory), cancellationToken)
            : new ModrinthPackPlan(cursePlan.MinecraftVersion, cursePlan.Loader, cursePlan.LoaderVersion,
                await curseForge.ResolvePackFilesAsync(cursePlan.Files, instanceDirectory, cancellationToken,
                    missing => ConfirmMissingFilesAsync(missing, cancellationToken)));
        Directory.CreateDirectory(instanceDirectory);
        await File.WriteAllTextAsync(marker, identity, cancellationToken);
        progress.Report(new(1, 0, "下载整合包组件"));
        await installer.DownloadFilesAsync(gameDirectory, "下载整合包组件", plan.Files, new StageProgress(progress, 1), cancellationToken);
        var existing = InstanceInstallManifest.TryLoad(instanceDirectory);
        if (existing?.MinecraftVersion != plan.MinecraftVersion || existing.Loader != plan.Loader || existing.LoaderVersion != plan.LoaderVersion)
        {
            await installer.InstallAsync(new(plan.MinecraftVersion, name, gameDirectory, plan.Loader, plan.LoaderVersion),
                new StageProgress(progress, 2, mapMinecraftStages: true), cancellationToken);
        }
        progress.Report(new(3, 0, "整理整合包文件"));
        if (cursePlan is null)
            await Task.Run(() => ModrinthPackReader.ExtractClientOverridesAsync(archivePath, instanceDirectory, cancellationToken), cancellationToken);
        else await Task.Run(() => CurseForgePackReader.ExtractOverridesAsync(archivePath, instanceDirectory, cursePlan.Overrides, cancellationToken), cancellationToken);
        var options = instanceSettings.Load(instanceDirectory, settings.Load());
        options.UseGlobalLaunchOptions = false;
        options.VersionIsolation = "隔离所有版本";
        instanceSettings.Save(instanceDirectory, options);
        cancellationToken.ThrowIfCancellationRequested();
        DefaultGameSettingsService.RecordNewInstance(instanceDirectory);
        File.Delete(marker);
        progress.Report(new(3, 1, "整合包安装完成"));
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("请填写有效的实例名称。");
    }

    internal static async Task<bool> ConfirmMissingFilesAsync(IReadOnlyList<CurseForgeMissingFile> missing, CancellationToken token)
    {
        var window = App.MainWindow ?? throw new InvalidOperationException("无法显示整合包内容缺失提示。");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var root = (window.Content as FrameworkElement)?.XamlRoot ?? throw new InvalidOperationException("应用窗口尚未就绪。");
                var details = string.Join("\n", missing.Select(file => $"• {file.Name}（项目 {file.ProjectId}，文件 {file.FileId}）"));
                var dialog = new ContentDialog
                {
                    Title = "整合包内容缺失", XamlRoot = root,
                    Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { MaxWidth = 480, TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true, Text = "接口未返回以下文件的信息，它们可能已被删除或暂时无法获取：\n\n" + details
                            + "\n\n继续安装将缺少这些内容，可能导致游戏无法启动或功能异常。是否仍要继续？" } },
                    PrimaryButtonText = "仍然继续", CloseButtonText = "取消安装", DefaultButton = ContentDialogButton.Close,
                };
                var result = await App.Services.GetRequiredService<IDialogService>().ShowAsync(dialog, token);
                if (result != ContentDialogResult.Primary) throw new UserCanceledOperationException();
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) throw new InvalidOperationException("无法显示整合包内容缺失提示。");
        return await completion.Task;
    }

    private sealed class StageProgress(IProgress<InstallProgress> target, int stage, bool mapMinecraftStages = false) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => target.Report(new(stage,
            mapMinecraftStages ? Math.Clamp((value.Stage + value.Progress) / 6d, 0, .999) : value.Progress,
            mapMinecraftStages ? "安装游戏与加载器" : value.Detail, value.BytesPerSecond));
    }
}
