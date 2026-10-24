using Launcher.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Launcher.App.Views;

/// <summary>
/// Activity log pane, hosted on the launch page.
/// </summary>
public sealed partial class LogPanel : UserControl
{
    public LogBuffer Log { get; }

    /// <summary>
    /// Set while a follow is already queued, to collapse bursts into one pass.
    /// </summary>
    /// <remarks>
    /// A launcher writing a batch of lines raises one change per line. Scrolling for
    /// each of them made the pane visibly jump, so only the last one in a burst is
    /// acted on.
    /// </remarks>
    private bool _followQueued;

    public LogPanel()
    {
        Log = App.Services.GetRequiredService<LogBuffer>();
        InitializeComponent();

        Log.Entries.CollectionChanged += (_, _) => QueueFollow();
        QueueFollow();
    }

    private void QueueFollow()
    {
        if (_followQueued)
        {
            return;
        }

        _followQueued = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            _followQueued = false;
            Follow();
        });
    }

    /// <summary>
    /// Keeps the empty-state placeholder in sync and follows the tail.
    /// </summary>
    /// <remarks>
    /// The visibility is only written when it actually changes. Setting it on every
    /// line forced a fresh layout pass each time and showed up as a flash, because the
    /// change re-measured the row that was not moving.
    /// </remarks>
    private void Follow()
    {
        var empty = Log.Entries.Count == 0;

        var emptyVisibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyState.Visibility != emptyVisibility)
        {
            EmptyState.Visibility = emptyVisibility;
            LogList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        if (empty)
        {
            return;
        }

        // ScrollIntoView also forces a layout pass, so it is only worth doing when the
        // list was already scrolled to the bottom: otherwise the user is reading older
        // lines and the view must stay where they put it.
        if (!IsScrolledToBottom())
        {
            return;
        }

        LogList.ScrollIntoView(Log.Entries[^1], ScrollIntoViewAlignment.Default);
    }

    /// <summary>
    /// True when the last line is already in view.
    /// </summary>
    /// <remarks>
    /// A ListView has no direct "at bottom" property, so this reads the inner
    /// ScrollViewer. A missing ScrollViewer means the list has not been realised yet,
    /// which counts as "at bottom" so the very first lines still scroll into view.
    /// </remarks>
    private bool IsScrolledToBottom()
    {
        var viewer = FindScrollViewer(LogList);

        if (viewer is null)
        {
            return true;
        }

        // Half a line of slack: exact equality almost never holds after a new item is
        // appended, because the extent grows before the offset catches up.
        return viewer.VerticalOffset >= viewer.ScrollableHeight - 24;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Log.Entries.Clear();
    }
}
