using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Launcher.App.Controls;

public sealed partial class SidebarItem : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SidebarItem), new PropertyMetadata("", ContentChanged));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SidebarItem), new PropertyMetadata("", ContentChanged));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(SidebarItem), new PropertyMetadata("", ContentChanged));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(SidebarItem), new PropertyMetadata("", IconChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public SidebarItem()
    {
        InitializeComponent();
        UpdateContent();
        UpdateIcon();
        Loaded += (_, _) =>
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not ListViewItem row) continue;
                row.SetBinding(AutomationProperties.NameProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Title)) });
                row.SetBinding(AutomationProperties.HelpTextProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Subtitle)) });
                break;
            }
        };
    }

    private static void ContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((SidebarItem)sender).UpdateContent();

    private static void IconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((SidebarItem)sender).UpdateIcon();

    private void UpdateIcon()
    {
        if (IconPath is null) return;
        IconPath.Data = string.IsNullOrWhiteSpace(Icon)
            ? null
            : (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Icon);
    }

    private void UpdateContent()
    {
        if (SubtitleText is null) return;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        BadgeSurface.Visibility = string.IsNullOrWhiteSpace(Badge) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, Title);
        ToolTipService.SetToolTip(this, string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}\n{Subtitle}");
    }
}
