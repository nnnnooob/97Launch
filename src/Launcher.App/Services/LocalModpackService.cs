using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;

namespace Launcher.App.Services;

public sealed class LocalModpackService(MinecraftInstallService installer, CurseForgeResourceService curseForge,
    InstanceLaunchSettingsStore instanceSettings, SettingsStore settings)
{
    public async Task InstallAsync(string archivePath, string gameRoot, string name, IProgress<InstallProgress> progress, CancellationToken token)
    {
        ModrinthModpackInstaller.ValidateName(name);
        var directory = InstanceContentService.SafeChild(Path.Combine(gameRoot, "versions"), name);
        var identity = "local:" + await InstanceModificationService.HashAsync(archivePath, token);
        var marker = Path.Combine(directory, ".97launch-installing");
        if (Directory.Exists(directory) && (!File.Exists(marker) || await File.ReadAllTextAsync(marker, token) != identity))
            throw new IOException("同名实例已经存在，请使用其他名称。");
        var cache = Path.Combine(gameRoot, "97Launch", "cache", "local-modpacks");
        Directory.CreateDirectory(cache);
        var pack = archivePath;
        using (var archive = ZipFile.OpenRead(pack))
        {
            if (archive.GetEntry("modrinth.index.json") is null && archive.GetEntry("manifest.json") is null)
            {
                var nested = archive.Entries.Where(e => e.FullName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (nested.Length != 1 || nested[0].Length > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("未找到支持的整合包清单。");
                pack = Path.Combine(cache, identity[6..] + ".mrpack");
                await using var input = nested[0].Open(); await using var output = File.Create(pack);
                await input.CopyToAsync(output, token);
            }
        }
        progress.Report(new(0, .3, "读取整合包清单"));
        CurseForgePackPlan? cf = null;
        InstanceInstallManifest? extra = null;
        using (var archive = ZipFile.OpenRead(pack))
        {
            if (archive.GetEntry("manifest.json") is not null && archive.GetEntry("modrinth.index.json") is null) cf = CurseForgePackReader.Read(pack);
            var entry = archive.GetEntry("97launch.components.json");
            if (entry is not null)
            {
                if (entry.Length > 1024 * 1024) throw new InvalidDataException("组件清单过大。");
                await using var stream = entry.Open();
                extra = await JsonSerializer.DeserializeAsync<InstanceInstallManifest>(stream, cancellationToken: token);
                if (extra is { SchemaVersion: > 2 }) throw new InvalidDataException("此整合包需要更新版本的 97Launch。");
            }
        }
        var plan = cf is null ? ModrinthPackReader.Read(pack, directory)
            : new ModrinthPackPlan(cf.MinecraftVersion, cf.Loader, cf.LoaderVersion, await curseForge.ResolvePackFilesAsync(cf.Files, directory, token,
                missing => ModrinthModpackInstaller.ConfirmMissingFilesAsync(missing, token)));
        if (extra is not null && (extra.MinecraftVersion != plan.MinecraftVersion || extra.Loader != plan.Loader || extra.LoaderVersion != plan.LoaderVersion))
            throw new InvalidDataException("整合包的组件信息和标准清单不一致。");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(marker, identity, token);
        var existing = InstanceInstallManifest.TryLoad(directory);
        if (existing?.MinecraftVersion != plan.MinecraftVersion || existing.Loader != plan.Loader || existing.LoaderVersion != plan.LoaderVersion)
            await installer.InstallAsync(new(plan.MinecraftVersion, name, gameRoot, plan.Loader, plan.LoaderVersion,
                extra?.LiteLoaderVersion, extra?.OptiFineVersion, extra?.FabricApiVersion, extra?.OptiFabricVersion, Isolated: true),
                new RemapProgress(progress), token);
        progress.Report(new(1, .8, "安装整合包资源"));
        await installer.DownloadFilesAsync(gameRoot, "安装整合包资源", plan.Files,
            new Progress<InstallProgress>(p => progress.Report(new(1, .8 + p.Progress * .2, p.Detail, p.BytesPerSecond))), token);
        progress.Report(new(2, 0, "整理配置与本地文件"));
        if (cf is null) await ModrinthPackReader.ExtractClientOverridesAsync(pack, directory, token);
        else await CurseForgePackReader.ExtractOverridesAsync(pack, directory, cf.Overrides, token);
        var options = instanceSettings.Load(directory, settings.Load());
        options.UseGlobalLaunchOptions = false; options.VersionIsolation = "隔离所有版本";
        if (Directory.Exists(Path.Combine(directory, "java"))) options.JavaMode = "使用实例文件夹中的 Java";
        instanceSettings.Save(directory, options);
        token.ThrowIfCancellationRequested();
        DefaultGameSettingsService.RecordNewInstance(directory);
        File.Delete(marker);
        progress.Report(new(2, 1, "整合包导入完成"));
    }

    private sealed class RemapProgress(IProgress<InstallProgress> target) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => target.Report(new(1, Math.Clamp((value.Stage + value.Progress) / 6d * .8, 0, .8), value.Detail, value.BytesPerSecond));
    }
}
