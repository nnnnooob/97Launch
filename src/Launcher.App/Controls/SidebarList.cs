using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Launcher.App.Controls;

/// <summary>Internal navigation with native list selection and one moving selection surface.</summary>
public sealed class SidebarList : ListView
{
    private readonly UISettings _uiSettings = new();
    private FrameworkElement? _root;
    private Border? _selection;
    private ScrollViewer? _scroller;
    private Storyboard? _selectionMotion;
    private readonly RectangleGeometry _clip = new();
    private Rect _selectionBounds;
    private bool _hasSelection;
    private bool _animateSelection;

    public SidebarList()
    {
        Style = (Style)Application.Current.Resources["SidebarListStyle"];
        Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromHorizontalOffset = 8, FromVerticalOffset = 0, IsStaggeringEnabled = false }
        };
        SelectionChanged += (_, _) =>
        {
            _animateSelection = true;
            UpdateSelection(true);
        };
        LayoutUpdated += (_, _) => UpdateSelection(_animateSelection);
        SizeChanged += (_, _) => UpdateSelection(false);
        Unloaded += (_, _) =>
        {
            _selectionMotion?.Stop();
            _hasSelection = false;
            if (_selection is not null) _selection.Opacity = 0;
        };
    }

    protected override void OnApplyTemplate()
    {
        if (_scroller is not null) _scroller.ViewChanged -= Scroller_ViewChanged;
        base.OnApplyTemplate();
        _root = GetTemplateChild("SidebarRoot") as FrameworkElement;
        if (_root is not null) _root.Clip = _clip;
        _selection = GetTemplateChild("SelectionSurface") as Border;
        _scroller = GetTemplateChild("SidebarScroller") as ScrollViewer;
        if (_scroller is not null) _scroller.ViewChanged += Scroller_ViewChanged;
        _hasSelection = false;
        UpdateSelection(false);
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not ListViewItem row) return;
        row.PointerPressed -= Row_PointerPressed;
        row.PointerReleased -= Row_PointerReleased;
        row.PointerCanceled -= Row_PointerReleased;
        row.PointerCaptureLost -= Row_PointerReleased;
        row.PointerExited -= Row_PointerReleased;
        row.PointerPressed += Row_PointerPressed;
        row.PointerReleased += Row_PointerReleased;
        row.PointerCanceled += Row_PointerReleased;
        row.PointerCaptureLost += Row_PointerReleased;
        row.PointerExited += Row_PointerReleased;
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListViewItem row)
        {
            row.PointerPressed -= Row_PointerPressed;
            row.PointerReleased -= Row_PointerReleased;
            row.PointerCanceled -= Row_PointerReleased;
            row.PointerCaptureLost -= Row_PointerReleased;
            row.PointerExited -= Row_PointerReleased;
            SetRowScale(row, 1);
        }
        base.ClearContainerForItemOverride(element, item);
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateSelection(false);

    private void UpdateSelection(bool animate)
    {
        if (_root is null || _selection is null || !IsLoaded) return;
        var clipBounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_clip.Rect != clipBounds) _clip.Rect = clipBounds;
        var row = SelectedItem is null ? null : ContainerFromItem(SelectedItem) as ListViewItem;
        if (row is null || row.ActualHeight <= 0)
        {
            _selection.Opacity = 0;
            _hasSelection = false;
            return;
        }

        var position = row.TransformToVisual(_root).TransformPoint(new Point());
        // Inset the surface inside the hit area, leaving no dead gaps between rows.
        var bounds = new Rect(position.X + 2, position.Y + 2,
            Math.Max(0, row.ActualWidth - 4), Math.Max(0, row.ActualHeight - 4));
        if (_hasSelection && bounds == _selectionBounds) return;

        var transform = (TranslateTransform)_selection.RenderTransform;
        var previous = new Point(transform.X, transform.Y);
        _selectionMotion?.Stop();
        transform.X = bounds.X;
        transform.Y = bounds.Y;
        if (animate && _hasSelection && _uiSettings.AnimationsEnabled)
        {
            _selectionMotion = new Storyboard();
            foreach (var (property, from, to) in new[] { ("X", previous.X, bounds.X), ("Y", previous.Y, bounds.Y) })
            {
                var motion = new DoubleAnimation
                {
                    From = from, To = to, Duration = TimeSpan.FromMilliseconds(190),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(motion, transform);
                Storyboard.SetTargetProperty(motion, property);
                _selectionMotion.Children.Add(motion);
            }
            _selectionMotion.Begin();
        }

        _selection.Width = bounds.Width;
        _selection.Height = bounds.Height;
        _selection.Opacity = 1;
        _selectionBounds = bounds;
        _hasSelection = true;
        _animateSelection = false;
    }

    private void Row_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
            SetRowScale((ListViewItem)sender, .985f);
    }

    private void Row_PointerReleased(object sender, PointerRoutedEventArgs e) => SetRowScale((ListViewItem)sender, 1);

    private void SetRowScale(ListViewItem row, float scale)
    {
        if (VisualTreeHelper.GetChildrenCount(row) == 0) return;
        var content = VisualTreeHelper.GetChild(row, 0) as UIElement;
        if (content is null) return;
        var visual = ElementCompositionPreview.GetElementVisual(content);
        visual.CenterPoint = new Vector3((float)row.ActualWidth / 2, (float)row.ActualHeight / 2, 0);
        var target = new Vector3(scale, scale, 1);
        if (!_uiSettings.AnimationsEnabled)
        {
            visual.StopAnimation("Scale");
            visual.Scale = target;
            return;
        }
        var motion = visual.Compositor.CreateVector3KeyFrameAnimation();
        motion.InsertKeyFrame(1, target);
        motion.Duration = TimeSpan.FromMilliseconds(scale < 1 ? 70 : 140);
        visual.StartAnimation("Scale", motion);
    }
}
