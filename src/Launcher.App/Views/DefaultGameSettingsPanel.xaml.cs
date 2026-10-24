using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.System;

namespace Launcher.App.Views;

public sealed partial class DefaultGameSettingsPanel : UserControl
{
    private readonly SettingsStore _store = App.Services.GetRequiredService<SettingsStore>();
    private DefaultGameSettings _values = new();
    private bool _loading = true;
    private bool _capturing;

    public DefaultGameSettingsPanel()
    {
        InitializeComponent();
        LoadValues((_store.Load().GameDefaults ?? new()).Normalize());
        Loaded += (_, _) => LoadValues((_store.Load().GameDefaults ?? new()).Normalize());
    }

    private void LoadValues(DefaultGameSettings values)
    {
        _loading = true;
        _values = values;
        EnabledToggle.IsOn = values.Enabled;
        FovSlider.Value = values.FieldOfView;
        BrightnessSlider.Value = values.Brightness;
        DistanceSlider.Value = values.RenderDistance;
        SensitivitySlider.Value = values.MouseSensitivity;
        SetInputsEnabled(values.Enabled);
        UpdateKeys();
        _loading = false;
    }

    private void UpdateKeys()
    {
        SprintButton.Content = GameInputKey.Display(_values.SprintKey);
        SneakButton.Content = GameInputKey.Display(_values.SneakKey);
        PerspectiveButton.Content = GameInputKey.Display(_values.PerspectiveKey);
        ConflictHint.Visibility = new[] { _values.SprintKey, _values.SneakKey, _values.PerspectiveKey }.Distinct().Count() < 3
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveValues()
    {
        if (_loading) return;
        _values = (_values with { Enabled = EnabledToggle.IsOn, FieldOfView = FovSlider.Value,
            Brightness = BrightnessSlider.Value, RenderDistance = DistanceSlider.Value, MouseSensitivity = SensitivitySlider.Value }).Normalize();
        var settings = _store.Load();
        settings.GameDefaults = _values;
        _store.Save(settings);
        LoadValues(_values);
    }

    private void Enabled_Toggled(object sender, RoutedEventArgs e) => SaveValues();
    private void Value_Changed(object sender, RangeBaseValueChangedEventArgs e) => SaveValues();

    private void SetInputsEnabled(bool enabled)
    {
        foreach (var input in new Control[] { FovSlider, BrightnessSlider, DistanceSlider, SensitivitySlider,
                     FovNumber, BrightnessNumber, DistanceNumber, SensitivityNumber,
                     SprintButton, SneakButton, PerspectiveButton }) input.IsEnabled = enabled;
        OptionsPanel.IsHitTestVisible = enabled;
    }

    private async void CaptureKey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || sender is not Button { Tag: string action }) return;
        _capturing = true;
        try
        {
            string? selected = null;
            var target = new Button { Content = "请按下键盘按键，或在此按下鼠标按键", MinHeight = 84,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            var hint = new TextBlock { Text = "支持单个按键及鼠标侧键。Esc 取消，不保存本次修改。", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
            panel.Children.Add(target); panel.Children.Add(hint);
            var dialog = new ContentDialog { Title = action switch { "sprint" => "设置疾跑按键", "sneak" => "设置潜行按键", _ => "设置切换视角按键" },
                Content = panel, CloseButtonText = "取消", DefaultButton = ContentDialogButton.None };
            void Accept(string key) { if (selected is not null) return; selected = key; dialog.Hide(); }
            dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
            {
                if (args.Key == VirtualKey.Escape) { args.Handled = true; dialog.Hide(); return; }
                args.Handled = true;
                var key = GameInputKey.FromVirtualKey((int)args.Key, args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey);
                if (key is not null) Accept(key); else hint.Text = "暂不支持此按键，请换一个按键。";
            }), true);
            target.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
                var properties = args.GetCurrentPoint(target).Properties;
                var key = properties.IsXButton1Pressed ? "key.mouse.4" : properties.IsXButton2Pressed ? "key.mouse.5"
                    : properties.IsMiddleButtonPressed ? "key.mouse.middle" : properties.IsRightButtonPressed ? "key.mouse.right"
                    : properties.IsLeftButtonPressed ? "key.mouse.left" : null;
                if (key is not null) { args.Handled = true; Accept(key); }
            }), true);
            dialog.Opened += (_, _) => target.Focus(FocusState.Programmatic);
            await ContentUi.ShowAsync(dialog, XamlRoot);
            if (selected is null) return;
            _values = action switch { "sprint" => _values with { SprintKey = selected },
                "sneak" => _values with { SneakKey = selected }, _ => _values with { PerspectiveKey = selected } };
            SaveValues();
        }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法设置按键", ex.Message); }
        finally { _capturing = false; }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        LoadValues(new DefaultGameSettings(Enabled: _values.Enabled));
        SaveValues();
        App.Services.GetRequiredService<ToastService>().Show("已恢复默认值", "新实例将使用恢复后的游戏设置预设。");
    }

    private async void ReadInstance_Click(object sender, RoutedEventArgs e)
    {
        ReadButton.IsEnabled = false;
        try
        {
            var instances = App.Services.GetRequiredService<InstancesViewModel>();
            var available = instances.AvailableInstances.Where(i => !i.HasError).ToArray();
            if (available.Length == 0) throw new InvalidOperationException("暂无可读取设置的实例。");
            var picker = new ComboBox { ItemsSource = available, DisplayMemberPath = "Name", MinWidth = 340,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = instances.SelectedInstance is { } current && available.Contains(current) ? current : available[0] };
            var dialog = new ContentDialog { Title = "从实例读取游戏设置", Content = picker,
                PrimaryButtonText = "读取", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            if (await ContentUi.ShowAsync(dialog, XamlRoot) != ContentDialogResult.Primary || picker.SelectedItem is not InstanceItem instance) return;
            var context = App.Services.GetRequiredService<InstanceContentService>().Context(instance);
            var result = await Task.Run(() => DefaultGameSettingsService.Read(context.GameDirectory, _values));
            if (result.Count == 0) throw new InvalidOperationException("此实例还没有可读取的游戏设置，请先运行游戏并保存设置。");
            LoadValues(result.Settings);
            SaveValues();
            App.Services.GetRequiredService<ToastService>().Show("已读取游戏设置", $"已从「{instance.Name}」读取 {result.Count} 项。"
                + (result.Count < 7 ? "其余项目保留当前预设。" : ""));
        }
        catch (Exception ex) { App.Services.GetRequiredService<ToastService>().ShowError("无法读取游戏设置", ex.Message); }
        finally { ReadButton.IsEnabled = true; }
    }
}
