using System.Text.Json;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

public sealed partial class InstanceExportPanel : UserControl
{
    private InstanceContext? _context;
    private string? _java;
    private bool _preparing;
    public event Action? ExportSubmitted;

    public InstanceExportPanel() => InitializeComponent();
    public void Load(InstanceItem instance)
    {
        if (_context?.Instance.Path == instance.Path) return;
        _context = App.Services.GetRequiredService<InstanceContentService>().Context(instance);
        _java = null;
        InstanceLabel.Text = instance.Name + (_context.Shared ? " · 使用共享游戏目录" : " · 使用独立游戏目录");
        Apply(new InstanceExportOptions { Name = instance.Name });
        StatusText.Text = "选择需要导出的内容。";
        StartButton.IsEnabled = OptionsPanel.IsEnabled = true;
    }

    private InstanceExportOptions Read() => new()
    {
        Name = PackName.Text.Trim(), Version = PackVersion.Text.Trim(), Summary = PackSummary.Text.Trim(),
        Mods = Mods.IsChecked == true, DisabledMods = DisabledMods.IsChecked == true, Options = GameOptions.IsChecked == true,
        Configs = Configs.IsChecked == true, Scripts = Scripts.IsChecked == true, ResourcePacks = ResourcePacks.IsChecked == true,
        ShaderPacks = ShaderPacks.IsChecked == true, Saves = Saves.IsChecked == true, Screenshots = Screenshots.IsChecked == true,
        Servers = Servers.IsChecked == true, Personal = Personal.IsChecked == true, DirectFiles = DirectFiles.IsChecked == true,
        OnlyModrinth = OnlyModrinth.IsChecked == true, IncludeLauncher = IncludeLauncher.IsChecked == true,
        JavaDirectory = IncludeJava.IsChecked == true ? _java : null, Rules = Rules.Text,
    };

    private void Apply(InstanceExportOptions profile)
    {
        PackName.Text = profile.Name; PackVersion.Text = profile.Version; PackSummary.Text = profile.Summary;
        Mods.IsChecked = profile.Mods; DisabledMods.IsChecked = profile.DisabledMods; GameOptions.IsChecked = profile.Options;
        Configs.IsChecked = profile.Configs; Scripts.IsChecked = profile.Scripts; ResourcePacks.IsChecked = profile.ResourcePacks;
        ShaderPacks.IsChecked = profile.ShaderPacks; Saves.IsChecked = profile.Saves; Screenshots.IsChecked = profile.Screenshots;
        Servers.IsChecked = profile.Servers; Personal.IsChecked = profile.Personal; DirectFiles.IsChecked = profile.DirectFiles;
        OnlyModrinth.IsChecked = profile.OnlyModrinth; IncludeLauncher.IsChecked = profile.IncludeLauncher;
        _java = profile.JavaDirectory; IncludeJava.IsChecked = _java is not null; JavaPathLabel.Text = _java ?? ""; Rules.Text = profile.Rules;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || _preparing) return;
        _preparing = true; StartButton.IsEnabled = false;
        try
        {
            var profile = Read();
            if (profile.Name.Length == 0 || profile.Version.Length == 0) throw new InvalidOperationException("请填写整合包名称和版本。");
            ModrinthModpackInstaller.ValidateName(profile.Name);
            if (IncludeJava.IsChecked == true && _java is null) throw new IOException("请先选择要携带的 Java 文件夹。");
            if (profile.DirectFiles && !await ContentUi.ConfirmAsync("直接打包资源", "将把资源文件放入整合包。请确认这些资源允许按你的用途分发。", "继续导出", XamlRoot)) return;
            var destination = profile.IncludeLauncher
                ? await ContentUi.SaveAsync(profile.Name + " " + profile.Version, ("ZIP 压缩文件", ".zip"))
                : await ContentUi.SaveAsync(profile.Name + " " + profile.Version, ("Modrinth 整合包", ".mrpack"), ("ZIP 压缩文件", ".zip"));
            if (destination is null) return;
            var context = _context;
            App.Services.GetRequiredService<TransferViewModel>().EnqueueOperation("导出整合包 · " + profile.Name, context.Instance.Name, context.Root,
                ["整理文件与识别来源", "生成整合包"], (progress, token) => App.Services.GetRequiredService<InstanceExportService>()
                    .ExportAsync(context, profile, destination, ConfirmMissingAsync, progress, token), outputFilePath: destination);
            StatusText.Text = "已加入传输队列。";
            ExportSubmitted?.Invoke();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _preparing = false; StartButton.IsEnabled = true; }
    }

    private Task<bool> ConfirmMissingAsync(string message)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var root = (App.MainWindow?.Content as FrameworkElement)?.XamlRoot;
                completion.TrySetResult(root is not null && await ContentUi.ConfirmAsync("资源来源识别失败", message, "继续导出", root));
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }

    private void DirectFiles_Changed(object sender, RoutedEventArgs e)
    { if (OnlyModrinth is null) return; OnlyModrinth.IsEnabled = DirectFiles.IsChecked != true; if (DirectFiles.IsChecked == true) OnlyModrinth.IsChecked = false; }
    private void Java_Changed(object sender, RoutedEventArgs e)
    { if (ChooseJava is not null) ChooseJava.IsEnabled = IncludeJava.IsChecked == true; }
    private async void ChooseJava_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ContentUi.PickFolderAsync("选择 Java 主目录");
            if (path is null) return;
            if (!File.Exists(Path.Combine(path, "bin", "java.exe"))) throw new IOException("请选择包含 bin/java.exe 的 Java 主目录。");
            _java = path; JavaPathLabel.Text = path;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void ReadConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await ContentUi.PickFilesAsync("读取配置", ".json"); if (paths.Length == 0) return;
            Apply(JsonSerializer.Deserialize<InstanceExportOptions>(await File.ReadAllTextAsync(paths[0])) ?? throw new IOException("配置文件无效。"));
            StatusText.Text = "已读取导出配置";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ContentUi.SaveAsync(PackName.Text + "-export", ("JSON 配置文件", ".json")); if (path is null) return;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Read(), new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "已保存导出配置";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
