using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Launcher.App.Services;

internal static class ContentUi
{
    public static async Task<string[]> PickFilesAsync(string title, params string[] extensions)
    {
        var picker = new FileOpenPicker { CommitButtonText = title, SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32.GetWindowHandle(App.MainWindow!));
        return (await picker.PickMultipleFilesAsync()).Select(f => f.Path).ToArray();
    }

    public static async Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker { CommitButtonText = title, SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32.GetWindowHandle(App.MainWindow!));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(string name, params (string Label, string Extension)[] formats)
    {
        var picker = new FileSavePicker { SuggestedFileName = name, SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (var format in formats) picker.FileTypeChoices.Add(format.Label, [format.Extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32.GetWindowHandle(App.MainWindow!));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static Task<ContentDialogResult> ShowAsync(ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        return App.Services.GetRequiredService<IDialogService>().ShowAsync(dialog);
    }

    public static Task MessageAsync(string title, string message, XamlRoot root) => ShowAsync(new ContentDialog
    { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "关闭" }, root);

    public static Task<bool> ConfirmAsync(string title, string message, string action, XamlRoot root) =>
        App.Services.GetRequiredService<IDialogService>().ConfirmAsync(title, message, action, "取消", root);

    public static async Task<string?> PromptAsync(string title, string initial, XamlRoot root)
    {
        var input = new TextBox { Text = initial, MinWidth = 320 };
        var dialog = new ContentDialog { Title = title, Content = input, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        return await ShowAsync(dialog, root) == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }
}

public sealed record ResourceBrowseRequest(string InstanceId, string Section, string Search);
public sealed class PendingResourceBrowse
{
    private ResourceBrowseRequest? _request;
    public void Request(ResourceBrowseRequest request) => _request = request;
    public ResourceBrowseRequest? Take() { var request = _request; _request = null; return request; }
}
