using System.Diagnostics;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Launcher.App.Views;

/// <summary>
/// Instance settings shown over the instances page. The host dims and blurs the
/// page behind it; this control draws the card and owns the section rail.
/// </summary>
public sealed partial class InstanceSettingsDialog : UserControl
{
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private static readonly LogBuffer Log = App.Services.GetRequiredService<LogBuffer>();

    private InstanceItem? _instance;
    public InstanceSettingsViewModel ViewModel { get; }

    public InstanceSettingsDialog()
    {
        ViewModel = App.Services.GetRequiredService<InstanceSettingsViewModel>();
        InitializeComponent();
        _memoryTimer.Tick += (_, _) => { if (ViewModel.IsSettings) ViewModel.RefreshMemoryPreview(); };
        Loaded += (_, _) => { ViewModel.RefreshMemoryPreview(); _memoryTimer.Start(); };
        Unloaded += (_, _) => _memoryTimer.Stop();
        ContentPanel.ModifyRequested += () => BodyList.SelectedIndex = 1;
        ContentPanel.BusyChanged += busy => BodyList.IsEnabled = ResourceList.IsEnabled = !busy;
        ModificationView.ModificationSubmitted += () =>
        {
            Host?.Invoke();
            App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Transfer);
        };
        ExportView.ExportSubmitted += () =>
        {
            Host?.Invoke();
            App.Services.GetRequiredService<INavigationService>().Navigate(AppPage.Transfer);
        };
        Unloaded += (_, _) => ModificationView.ReleaseModificationView();
    }

    private static IDialogService Dialogs => App.Services.GetRequiredService<IDialogService>();

    /// <summary>
    /// Points the dialog at a specific instance and reloads all per-open state.
    /// </summary>
    public void Load(InstanceItem instance)
    {
        ModificationView.ReleaseModificationView();
        _instance = instance;
        ViewModel.Load(instance);
        ExportView.Load(instance);
        ResourceList.SelectedItem = null;
        BodyList.SelectedIndex = 0;
    }

    /// <summary>
    /// Supplies the callback the Close button uses to dismiss the overlay.
    /// </summary>
    public void SetHost(Action close) => Host = close;

    // ======== rail ========

    private async void BodyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BodyList.SelectedItem is not ListViewItem { Tag: string tag })
        {
            return;
        }
        if (tag != "modify") ModificationView?.ReleaseModificationView();

        if (ResourceList is not null)
        {
            ResourceList.SelectedItem = null;
        }

        ViewModel.Section = tag switch
        {
            "settings" => InstanceSection.Settings,
            "modify" => InstanceSection.Modify,
            "export" => InstanceSection.Export,
            _ => InstanceSection.Settings,
        };
        if (_instance is not null && ViewModel.IsModify)
            await LoadModificationAsync();
    }

    private void ResourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResourceList.SelectedItem is not ListViewItem { Tag: string tag })
        {
            return;
        }
        ModificationView?.ReleaseModificationView();

        if (BodyList is not null)
        {
            BodyList.SelectedItem = null;
        }

        ViewModel.Section = tag switch
        {
            "saves" => InstanceSection.Saves,
            "screenshots" => InstanceSection.Screenshots,
            "mods" => InstanceSection.Mods,
            "resourcepacks" => InstanceSection.ResourcePacks,
            "shaderpacks" => InstanceSection.ShaderPacks,
            _ => InstanceSection.Settings,
        };
        if (_instance is not null)
            ContentPanel.Load(_instance, ViewModel.Section switch
            {
                InstanceSection.Saves => Launcher.Core.Instances.ContentKind.Saves,
                InstanceSection.Screenshots => Launcher.Core.Instances.ContentKind.Screenshots,
                InstanceSection.ResourcePacks => Launcher.Core.Instances.ContentKind.ResourcePacks,
                InstanceSection.ShaderPacks => Launcher.Core.Instances.ContentKind.ShaderPacks,
                _ => Launcher.Core.Instances.ContentKind.Mods,
            });
    }

    private async Task LoadModificationAsync()
    {
        try { if (_instance is not null) await ModificationView.LoadForModificationAsync(_instance); }
        catch (Exception ex) { Log.Append(ex.Message, LogEntryKind.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Host?.Invoke();

    private void DialogOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var inset = e.NewSize.Width < 1088 || e.NewSize.Height < 808 ? 16 : 24;
        DialogOverlay.Padding = new Thickness(inset);
        DialogCard.Width = Math.Max(0, Math.Min(1040, e.NewSize.Width - inset * 2));
        DialogCard.Height = Math.Max(0, Math.Min(760, e.NewSize.Height - inset * 2));
    }

    /// <summary>
    /// Set by the page that hosts the overlay so the dialog can dismiss itself.
    /// </summary>
    internal Action? Host { get; set; }

    /// <summary>
    /// Logs the dialog's measured geometry once layout settles, so the centring
    /// can be checked against the actual host size rather than guessed at.
    /// </summary>
    public void LogGeometry()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var host = Parent as FrameworkElement;
            Log.Append(
                $"弹窗尺寸 card={ActualWidth:0}x{ActualHeight:0} "
                + $"host={(host?.ActualWidth ?? 0):0}x{(host?.ActualHeight ?? 0):0}");
        });
    }

    // ======== 设置 ========

    private void MemoryMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radio)
        {
            ViewModel.IsAutoMemory = ReferenceEquals(radio, AutoMemoryRadio);
        }
    }

    private void RenameInstance_Click(object sender, RoutedEventArgs e)
        => BodyList.SelectedIndex = 1;

}
