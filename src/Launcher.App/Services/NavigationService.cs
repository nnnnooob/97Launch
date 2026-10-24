using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App.Services;

/// <summary>
/// Page identifiers used by <see cref="INavigationService"/>.
/// </summary>
public enum AppPage
{
    Launch,
    Instances,
    Download,
    Transfer,
    Settings,
}

/// <summary>
/// Navigation contract. Implemented against a Frame; the indirection keeps
/// view models free of UI types.
/// </summary>
public interface INavigationService
{
    AppPage CurrentPage { get; }

    void Navigate(AppPage page);

    bool CanGoBack { get; }

    void GoBack();
}

/// <summary>
/// Frame-backed navigation.
/// </summary>
/// <remarks>
/// <para>
/// Pages are cached by the Frame itself through <c>NavigationCacheMode</c>
/// (<c>Required</c> on each page), not by this class. Reusing a page instance by
/// assigning <c>Frame.Content</c> directly would also work, but it replaces the
/// frame's own transition: the page would swap instantly with no slide/fade,
/// which reads as a glitch rather than a navigation.
/// </para>
/// <para>
/// So navigation still goes through <see cref="Frame.Navigate(Type)"/> and the
/// animation plays; <c>Required</c> keeps the instance alive across visits, so
/// view state and scroll position survive without rebuilding the visual tree.
/// </para>
/// </remarks>
public sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    public AppPage CurrentPage { get; private set; } = AppPage.Launch;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void Attach(Frame frame)
    {
        _frame = frame;
    }

    public void Navigate(AppPage page)
    {
        if (!App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) return;
        if (_frame is null)
        {
            return;
        }

        // Re-selecting the active item would otherwise restart the transition
        // and re-raise Navigated for no reason.
        if (ReferenceEquals(_frame.Content?.GetType(), PageTypeFor(page))
            && CurrentPage == page)
        {
            return;
        }

        CurrentPage = page;
        _frame.Navigate(PageTypeFor(page));
    }

    public void GoBack()
    {
        if (!App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) return;
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }

    private static Type PageTypeFor(AppPage page) => page switch
    {
        AppPage.Launch => typeof(Views.LaunchPage),
        AppPage.Instances => typeof(Views.InstancesPage),
        AppPage.Download => typeof(Views.DownloadPage),
        AppPage.Transfer => typeof(Views.TransferPage),
        AppPage.Settings => typeof(Views.SettingsPage),
        _ => typeof(Views.LaunchPage),
    };
}
