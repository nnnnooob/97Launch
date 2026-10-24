using System.Diagnostics;
using System.Runtime.InteropServices;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Launcher.App.Views;

/// <summary>
/// Settings page: a two-group rail on the left (游戏 / 关于) and the matching
/// pane on the right.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private static readonly LogBuffer Log = App.Services.GetRequiredService<LogBuffer>();

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        _memoryTimer.Tick += (_, _) => { if (ViewModel.IsLaunch) ViewModel.RefreshMemoryPreview(); };
        Unloaded += (_, _) => _memoryTimer.Stop();
    }

    private static IDialogService Dialogs => App.Services.GetRequiredService<IDialogService>();

    // ======== rail ========

    /// <summary>
    /// The rail is split into two lists so each group gets its own header. Only
    /// one of them may hold a selection, or both groups would show a highlighted
    /// row at once.
    /// </summary>
    /// <remarks>
    /// All of these handlers guard against the other list still being null:
    /// IsSelected="True" on the first item fires SelectionChanged while
    /// InitializeComponent is still building the tree, so the second list does
    /// not exist yet at that moment.
    /// </remarks>
    private void GameSectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameSectionList.SelectedItem is not ListViewItem { Tag: string tag })
        {
            return;
        }

        if (AboutSectionList is not null)
        {
            AboutSectionList.SelectedItem = null;
        }

        ViewModel.Section = tag switch
        {
            "java" => SettingsSection.Java,
            "gameDefaults" => SettingsSection.GameDefaults,
            "manage" => SettingsSection.Manage,
            _ => SettingsSection.Launch,
        };
    }

    private void AboutSectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AboutSectionList.SelectedItem is not ListViewItem { Tag: string tag })
        {
            return;
        }

        if (GameSectionList is not null)
        {
            GameSectionList.SelectedItem = null;
        }

        ViewModel.Section = tag == "update" ? SettingsSection.Update : SettingsSection.About;
    }

    // ======== launch ========

    private void MemoryMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio)
        {
            return;
        }

        // Custom is the one without a memory figure in its label.
        ViewModel.IsAutoMemory = ReferenceEquals(radio, AutoMemoryRadio);
    }

    // ======== java ========

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshMemoryPreview();
        _memoryTimer.Start();
        await ViewModel.RefreshJavaAsync();
    }

    private async void RefreshJava_Click(object sender, RoutedEventArgs e)
    {
        Log.Append("重新搜索 Java");
        await ViewModel.RefreshJavaAsync();
    }

    private async void AddJava_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsJavaBusy || App.MainWindow is not { } window) return;

        var javaPath = PickJavaExecutable(window);
        if (javaPath is null) return;

        ViewModel.IsJavaBusy = true;
        try
        {
            if (!await ViewModel.AddJavaAsync(javaPath))
            {
                await new ContentDialog
                {
                    Title = "无法添加 Java",
                    Content = ViewModel.JavaStatus,
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                }.ShowAsync();
                return;
            }

            Log.Append($"已添加 Java：{ViewModel.SelectedJavaExecutable}");
            App.Services.GetRequiredService<ToastService>().Show("Java 已添加", ViewModel.SelectedJavaExecutable);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "添加 Java 失败");
            App.Services.GetRequiredService<ToastService>().ShowError("添加 Java 失败", ex.Message);
        }
        finally
        {
            ViewModel.IsJavaBusy = false;
        }
    }

    private static string? PickJavaExecutable(Window window)
    {
        const int FileMustExist = 0x00001000;
        const int PathMustExist = 0x00000800;
        const int ExplorerDialog = 0x00080000;
        const int BufferLength = 32768;

        var filter = Marshal.StringToHGlobalUni("Java (java.exe, javaw.exe)\0java.exe;javaw.exe\0\0");
        var title = Marshal.StringToHGlobalUni("选择 java.exe 或 javaw.exe（位于 bin 文件夹中）");
        var fileBuffer = Marshal.AllocHGlobal(BufferLength * sizeof(char));
        try
        {
            Marshal.Copy(new byte[BufferLength * sizeof(char)], 0, fileBuffer, BufferLength * sizeof(char));
            var dialog = new OpenFileName
            {
                Size = Marshal.SizeOf<OpenFileName>(),
                Owner = WinRT.Interop.WindowNative.GetWindowHandle(window),
                Filter = filter,
                File = fileBuffer,
                MaxFile = BufferLength,
                Title = title,
                Flags = FileMustExist | PathMustExist | ExplorerDialog,
            };

            return GetOpenFileName(ref dialog) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(title);
            Marshal.FreeHGlobal(fileBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDirectory;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedValue;
        public int ExtendedFlags;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);
    private async void RemoveJava_Click(object sender, RoutedEventArgs e)
    {
        var runtime = ViewModel.SelectedJava;
        if (runtime is null || !runtime.IsManual) return;

        var confirmed = await Dialogs.ConfirmAsync(
            "移除手动添加记录",
            $"确定移除 {runtime.Name} 的手动添加记录？\n{runtime.Path}\n\n不会删除 Java 文件；如果搜索仍能发现它，它会继续显示在列表中。",
            "移除",
            "取消",
            XamlRoot);
        if (!confirmed) return;

        await ViewModel.RemoveSelectedJavaAsync();
        Log.Append($"已从 Java 列表移除：{runtime.Path}");
        App.Services.GetRequiredService<ToastService>().Show("手动记录已移除", "Java 文件已保留，自动发现的运行环境仍会显示。");
    }

    private void OpenJavaFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJava is not { } runtime) return;
        try
        {
            if (!Directory.Exists(runtime.DirectoryPath))
                throw new DirectoryNotFoundException("此 Java 目录已不存在，请重新搜索。");
            Process.Start(new ProcessStartInfo { FileName = runtime.DirectoryPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ToastService>().ShowError("无法打开 Java 目录", ex.Message);
        }
    }

    private void CopyJavaPath_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJava is not { } runtime) return;
        try
        {
            var data = new DataPackage();
            data.SetText(runtime.Path);
            Clipboard.SetContent(data);
            App.Services.GetRequiredService<ToastService>().Show("路径已复制", runtime.Path);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ToastService>().ShowError("复制失败", ex.Message);
        }
    }
    // ======== about ========

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        => await OpenFolderAsync(AppInfo.DataDirectory);

    private async void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        => await OpenFolderAsync(AppInfo.LogDirectory);

    /// <summary>
    /// Opens a folder in Explorer, reporting a failure instead of throwing.
    /// </summary>
    private static async Task OpenFolderAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Log.Append($"目录不存在：{path}", LogEntryKind.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Append($"打开目录失败：{path}（{ex.Message}）", LogEntryKind.Error);
        }

        await Task.CompletedTask;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await Dialogs.ShowNotImplementedAsync("检查更新", XamlRoot);

}
