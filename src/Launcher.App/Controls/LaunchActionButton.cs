using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Launcher.App.Controls;

public sealed class LaunchActionButton : Button
{
    public static readonly DependencyProperty IsLaunchingProperty = DependencyProperty.Register(
        nameof(IsLaunching), typeof(bool), typeof(LaunchActionButton), new PropertyMetadata(false, OnLaunchingChanged));

    public bool IsLaunching
    {
        get => (bool)GetValue(IsLaunchingProperty);
        set => SetValue(IsLaunchingProperty, value);
    }

    public static readonly DependencyProperty CompactIconProperty = DependencyProperty.Register(
        nameof(CompactIcon), typeof(bool), typeof(LaunchActionButton),
        new PropertyMetadata(false, (sender, _) => ((LaunchActionButton)sender).UpdateIconAppearance()));

    public bool CompactIcon
    {
        get => (bool)GetValue(CompactIconProperty);
        set => SetValue(CompactIconProperty, value);
    }

    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private FrameworkElement? _surface;
    private FrameworkElement? _icon;
    private FrameworkElement? _shine;
    private SpriteVisual? _glow;
    private FrameworkElement? _play;
    private ProgressRing? _busy;
    private bool _hovered;
    private bool _pressed;
    private bool _hasPointerPosition;
    private bool _contrastSubscribed;

    public LaunchActionButton()
    {
        IsEnabledChanged += (_, _) => UpdateState();
        Loaded += (_, _) =>
        {
            try { _accessibility.HighContrastChanged += ContrastChanged; _contrastSubscribed = true; }
            catch (System.Runtime.InteropServices.COMException) { }
            ApplyContrast(); ResizeSurface(); UpdateState();
        };
        Unloaded += (_, _) =>
        {
            _hovered = _pressed = false;
            _hasPointerPosition = false;
            if (_contrastSubscribed)
            {
                try { _accessibility.HighContrastChanged -= ContrastChanged; }
                catch (System.Runtime.InteropServices.COMException) { }
                _contrastSubscribed = false;
            }
            StopAnimations();
            if (_busy is not null) _busy.IsActive = false;
        };
        SizeChanged += (_, _) => ResizeSurface();
    }

    private static void OnLaunchingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((LaunchActionButton)sender).UpdateState();

    protected override void OnApplyTemplate()
    {
        StopAnimations();
        if (_shine is not null) ElementCompositionPreview.SetElementChildVisual(_shine, null);
        base.OnApplyTemplate();
        _surface = GetTemplateChild("LaunchSurface") as FrameworkElement;
        _icon = GetTemplateChild("LaunchIcon") as FrameworkElement;
        if (_icon is not null) _icon.Visibility = Visibility.Visible;
        _shine = GetTemplateChild("LaunchShine") as FrameworkElement;
        _play = GetTemplateChild("LaunchPlay") as FrameworkElement;
        _busy = GetTemplateChild("LaunchBusy") as ProgressRing;
        UpdateIconAppearance();
        foreach (var element in new[] { _surface, _icon })
            if (element is not null) ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        _hasPointerPosition = false;
        _glow = null;
        if (_shine is not null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_shine).Compositor;
            var brush = compositor.CreateRadialGradientBrush();
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(70, 255, 255, 255)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(.45f, Windows.UI.Color.FromArgb(28, 255, 255, 255)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
            _glow = compositor.CreateSpriteVisual();
            _glow.Brush = brush;
            _glow.Size = new Vector2(220, 140);
            _glow.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(_shine, _glow);
        }
        ResizeSurface();
        UpdateState();
    }

    private void UpdateIconAppearance()
    {
        if (_icon is Border icon)
        {
            icon.Width = icon.Height = CompactIcon ? 20 : 34;
            icon.CornerRadius = new CornerRadius(CompactIcon ? 0 : 10);
            icon.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(CompactIcon ? (byte)0 : (byte)31, 255, 255, 255));
            if (icon.Parent is StackPanel row) row.Spacing = CompactIcon ? 8 : 12;
        }
        if (_play is Viewbox play)
        {
            play.Width = play.Height = CompactIcon ? 14 : 16;
            if (play.Child is SymbolIcon symbol) symbol.Symbol = CompactIcon ? Symbol.Forward : Symbol.Play;
        }
        if (_busy is not null) _busy.Width = _busy.Height = CompactIcon ? 14 : 20;
    }

    private void ResizeSurface()
    {
        if (_surface is null) return;
        _surface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
        ElementCompositionPreview.GetElementVisual(_surface).CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        _hasPointerPosition = false;
        UpdateGlowPosition(e);
        UpdateState();
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        var hadPosition = _hasPointerPosition;
        UpdateGlowPosition(e);
        if (!hadPosition && _hasPointerPosition) UpdateState();
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = _pressed = false;
        UpdateState();
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        UpdateState();
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pressed = false;
        UpdateState();
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
        UpdateState();
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsEnabled && !IsLaunching && (e.Key is VirtualKey.Space or VirtualKey.Enter)) { _pressed = true; UpdateState(); }
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is VirtualKey.Space or VirtualKey.Enter) { _pressed = false; UpdateState(); }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _pressed = false;
        UpdateState();
    }

    private void ContrastChanged(AccessibilitySettings sender, object args)
        => DispatcherQueue.TryEnqueue(() => { ApplyContrast(); UpdateState(); });

    private void ApplyContrast()
    {
        if (_accessibility.HighContrast)
        {
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            BorderBrush = Foreground;
        }
        else
        {
            ClearValue(BackgroundProperty); ClearValue(ForegroundProperty); ClearValue(BorderBrushProperty);
        }
    }

    private void UpdateState()
    {
        if (_surface is null) return;
        var interactive = IsEnabled && !IsLaunching;
        if (!interactive) _pressed = false;
        var pressed = interactive && _pressed;
        var hovered = interactive && _hovered;
        if (_play is not null) _play.Visibility = IsLaunching ? Visibility.Collapsed : Visibility.Visible;
        if (_busy is not null)
        {
            _busy.Visibility = IsLaunching ? Visibility.Visible : Visibility.Collapsed;
            _busy.IsActive = IsLaunching && IsLoaded;
        }
        if (_shine is not null) _shine.Visibility = _accessibility.HighContrast ? Visibility.Collapsed : Visibility.Visible;
        _surface.Opacity = IsLaunching || IsEnabled ? 1 : .45;
        Animate(_surface, "Scale", new Vector3(pressed ? .988f : 1, pressed ? .988f : 1, 1), pressed ? 70 : 170);
        Animate(_surface, "Translation", new Vector3(0, pressed ? 1 : hovered ? -1.5f : 0, 0), pressed ? 70 : 170);
        if (_icon is not null) Animate(_icon, "Translation", new Vector3(hovered && !pressed ? 2 : 0, 0, 0), 160);
        if (_glow is not null)
        {
            var visible = hovered && _hasPointerPosition && !_accessibility.HighContrast && _settings.AnimationsEnabled;
            _glow.StopAnimation("Opacity");
            if (!_settings.AnimationsEnabled || !IsLoaded) _glow.Opacity = 0;
            else
            {
                var fade = _glow.Compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(1, visible ? 1 : 0);
                fade.Duration = TimeSpan.FromMilliseconds(visible ? 120 : 180);
                _glow.StartAnimation("Opacity", fade);
            }
        }
    }

    private void Animate(UIElement element, string property, Vector3 target, int milliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!_settings.AnimationsEnabled || !IsLoaded)
        {
            if (!_settings.AnimationsEnabled) target = property == "Scale" ? Vector3.One : Vector3.Zero;
            visual.StopAnimation(property);
            if (property == "Scale") visual.Scale = target;
            else visual.Properties.InsertVector3(property, target);
            return;
        }
        var motion = visual.Compositor.CreateVector3KeyFrameAnimation();
        motion.InsertKeyFrame(1, target, visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1)));
        motion.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation(property, motion);
    }

    private void UpdateGlowPosition(PointerRoutedEventArgs e)
    {
        if (_glow is null || _shine is null || !_hovered || !IsEnabled || IsLaunching ||
            !_settings.AnimationsEnabled || _accessibility.HighContrast || e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;
        var point = e.GetCurrentPoint(_shine).Position;
        _hasPointerPosition = true;
        _glow.Offset = new Vector3((float)point.X - _glow.Size.X / 2, (float)point.Y - _glow.Size.Y / 2, 0);
    }

    private void StopAnimations()
    {
        if (_glow is not null)
        {
            _glow.StopAnimation("Opacity");
            _glow.Opacity = 0;
        }
        foreach (var element in new[] { _surface, _icon })
        {
            if (element is null) continue;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Scale");
            visual.StopAnimation("Translation");
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }
}
