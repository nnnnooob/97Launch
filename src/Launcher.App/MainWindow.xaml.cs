using Launcher.App.Services;
using Launcher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace Launcher.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; private set; } = null!;

    private INavigationService _navigation = null!;
    private bool _shellInitialized;

    private readonly WindowStateStore _windowStateStore = new();

    /// <summary>
    /// Debounce timer for geometry saves. Dragging a window edge fires
    /// <c>Changed</c> on every pixel, so saving on each event would hammer the
    /// disk for the duration of the drag.
    /// </summary>
    private DispatcherTimer? _windowStateSaveTimer;
    private bool _geometryTrackingStarted;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} {AppInfo.Version}";
        AppTitleTextBlock.Text = Title;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        InstallMinimumSizeHook();
        App.Services.GetRequiredService<LauncherAuthorizationService>().RequireAuthorization();
        InitializeShell();
        NavView.Visibility = StatusBar.Visibility = Visibility.Visible;
    }

    private void InitializeShell()
    {
        if (_shellInitialized) return;
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _navigation = App.Services.GetRequiredService<INavigationService>();
        DialogService = App.Services.GetRequiredService<IDialogService>();
        _shellInitialized = true;
        Bindings.Update();
        InitializeDropImport();
        Title = $"{AppInfo.Name} {AppInfo.Version}";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ToastBar.Translation = new System.Numerics.Vector3(0, 0, 24);

        var toasts = App.Services.GetRequiredService<ToastService>();
        toasts.MessageRequested += ShowToast;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBar.IsOpen = false; };
        ToastBar.Closed += (_, _) => _toastTimer.Stop();
        Closed += (_, _) => { toasts.MessageRequested -= ShowToast; _toastTimer.Stop(); };

        App.Services.GetRequiredService<GameSessionManager>().GameCrashed += (_, crash) =>
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) return;
                try { await DialogService.ShowGameCrashAsync(crash, Content.XamlRoot); }
                catch (Exception ex)
                {
                    App.Services.GetRequiredService<LogBuffer>()
                        .Append($"显示游戏异常提示失败：{ex.Message}", LogEntryKind.Error);
                }
            });

        // Sizing is deferred to ApplySavedGeometry(), called by App after
        // Activate(): applying it in the constructor is too early, because the
        // window is resized to fit its content when first shown and any size set
        // here is overwritten.
        // Enforce the size floor through Win32 window messages. WinAppSDK 1.6's
        // OverlappedPresenter has no PreferredMinimumWidth/Height (those arrived
        // in 2.x), and AppWindow.Resize cannot stop a user drag, so the window
        // procedure is hooked and WM_GETMINMAXINFO answered with our minimum.

        // Custom title bar: the shell draws its own caption so the app icon and
        // window title can be styled. SetTitleBar() must point at an element
        // whose area handles window drag.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The navigation rail is always open: the account chip sits in the pane
        // header, so collapsing the pane would hide the signed-in identity.
        // The toggle button is hidden in XAML; PaneClosing covers the remaining
        // routes (adaptive breakpoints on narrow windows, swipe gestures) by
        // cancelling the close outright.
        NavView.IsPaneOpen = true;
        NavView.PaneClosing += (_, args) => args.Cancel = true;

        // Fixed window title: identity plus version, not the active page. The
        // page name already shows in the status bar, and a title that changed on
        // every navigation made the window harder to pick out in the taskbar.
        AppTitleTextBlock.Text = Title;

        ((NavigationService)_navigation).Attach(ContentFrame);
        _navigation.Navigate(AppPage.Launch);

        // Select the startup item in code rather than with IsSelected="True".
        //
        // Setting IsSelected in XAML makes NavigationView resolve the item to a
        // NavigationViewItem during its own initialization, which throws
        // InvalidCastException (E_NOINTERFACE) and takes the process down. Doing
        // it after the frame has content, via SelectedItem, avoids that path.
        NavView.SelectedItem = LaunchNavItem;
        ViewModel.PageTitle = TitleForPage(AppPage.Launch);

        // Other pages can navigate the frame directly (the instance list jumps
        // to the launch page when a card is clicked), and that path does not go
        // through NavView_SelectionChanged. Mirroring the frame's own Navigated
        // event keeps the rail highlight correct no matter who started the
        // navigation.
        ContentFrame.Navigated += OnContentFrameNavigated;

        // Account names that do not fit the pane scroll rather than truncate.
        // Re-measured whenever the name changes or the pane resizes.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AccountDisplayName))
            {
                DispatcherQueue.TryEnqueue(UpdateAccountNameMarquee);
            }
        };

        NavView.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateAccountNameMarquee);

        // Initial focus and first marquee measurement both need the visual tree
        // to be laid out, which is only true once the content frame has loaded.
        ContentFrame.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateAccountNameMarquee();
            FocusInitialNavItem();
        });
    }

    public IDialogService DialogService { get; private set; } = null!;

    private void ShowToast(ToastMessage message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowToast(message));
            return;
        }
        _toastTimer.Stop();
        ToastBar.Title = message.Title;
        ToastBar.Message = message.Detail;
        ToastBar.Severity = message.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        ToastBar.IconSource = message.IsError ? null : new SymbolIconSource { Symbol = Symbol.Download };
        ToastBar.IsOpen = true;
        _toastTimer.Interval = TimeSpan.FromSeconds(message.IsError ? 6 : 3);
        _toastTimer.Start();
    }

    private void ToastBar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _toastTimer.Stop();

    private void ToastBar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ToastBar.IsOpen) _toastTimer.Start();
    }

    /// <summary>
    /// Restores the saved window size and position, applies the minimum size,
    /// and starts tracking changes.
    /// </summary>
    /// <remarks>
    /// Position is validated against the current display layout: saving on one
    /// monitor setup and reopening on another can otherwise leave the window
    /// entirely off-screen, with no way to drag it back.
    /// </remarks>
    /// <summary>
    /// Applies the saved geometry. Called by <see cref="App"/> once the window is
    /// activated.
    /// </summary>
    public void ApplySavedGeometry()
    {
        if (!_geometryTrackingStarted)
        {
            AppWindow.Changed += OnAppWindowChanged;
            Closed += (_, _) => SaveWindowGeometryNow();
            _geometryTrackingStarted = true;
        }
        RestoreWindowGeometry();
    }

    /// <summary>
    /// Mounts a control as a modal overlay over the window's page area.
    /// </summary>
    /// <remarks>
    /// Used for dialogs opened from the account flyout, which cannot host a dialog
    /// themselves: a flyout closes as soon as focus leaves it, so the dialog has to
    /// live somewhere that outlives the flyout.
    /// </remarks>
    public void ShowModal(FrameworkElement content)
    {
        DialogLayer.Children.Clear();
        DialogLayer.Children.Add(content);
        DialogLayer.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Removes the current modal overlay, if any.
    /// </summary>
    public void HideModal()
    {
        DialogLayer.Children.Clear();
        DialogLayer.Visibility = Visibility.Collapsed;
    }

    internal void SuspendForAuthorization()
    {
        _windowStateSaveTimer?.Stop();
        SaveWindowGeometryNow();
        HideModal();
        ToastBar.IsOpen = false;
        _toastTimer.Stop();
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(DropHost.XamlRoot))
        {
            if (popup.Child is ContentDialog dialog) dialog.Hide();
            else popup.IsOpen = false;
        }
        NavView.IsEnabled = false;
    }

    internal void ResumeAfterAuthorization()
    {
        NavView.IsEnabled = true;
        _navigation.Navigate(AppPage.Launch);
    }

    private void RestoreWindowGeometry()
    {        var state = _windowStateStore.Load();
        var hwnd = Win32.GetWindowHandle(this);

        // Two unit systems meet here: the stored values are physical pixels (as
        // GetWindowRect reported them), while the minimums are DIPs (they
        // describe how much XAML layout must fit). Convert the floor once.
        var minWidth = Win32.DipsToPixels(hwnd, WindowState.MinWidth);
        var minHeight = Win32.DipsToPixels(hwnd, WindowState.MinHeight);

        var width = Math.Max(state.Width, minWidth);
        var height = Math.Max(state.Height, minHeight);

        if (state.HasPosition && IsPositionOnScreen(state.X, state.Y, width, height))
        {
            // Physical pixels throughout, matching GetWindowRect on save.
            Win32.SetWindowPos(
                hwnd, IntPtr.Zero, state.X, state.Y, width, height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        else
        {
            // No usable position: centre on the active display's work area.
            //
            // Dimensions are a fraction of the work area rather than fixed
            // pixels: the stored values are physical pixels, so a hardcoded size
            // would open far too large on a high-DPI display and leave no room
            // around the window.
            var work = DisplayArea
                .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;

            width = Math.Max(minWidth, (int)(work.Width * 0.82));
            height = Math.Max(minHeight, (int)(work.Height * 0.85));

            // Never exceed the work area, or the title bar lands off-screen.
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);

            Win32.SetWindowPos(
                hwnd, IntPtr.Zero,
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, (work.Height - height) / 2),
                width, height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

    }

    /// <summary>
    /// True when the window's top-left corner falls inside a display's work
    /// area, i.e. when the user could actually grab it.
    /// </summary>
    private bool IsPositionOnScreen(int x, int y, int width, int height)
    {
        var area = DisplayArea.GetFromPoint(
            new PointInt32(x + (width / 2), y + (height / 2)),
            DisplayAreaFallback.None);

        return area is not null;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Ignore size-only changes made by us during startup; only user-driven
        // resize/move should trigger a save.
        if (!args.DidSizeChange && !args.DidPositionChange)
        {
            return;
        }

        _windowStateSaveTimer ??= CreateSaveTimer();
        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Start();
    }

    /// <summary>
    /// Creates the debounce timer. Restarting it on each change means the write
    /// happens once, shortly after the user stops dragging.
    /// </summary>
    private DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveWindowGeometryNow();
        };

        return timer;
    }

    private void SaveWindowGeometryNow()
    {
        // Physical pixels, the same unit SetWindowPos takes on restore. Reading
        // AppWindow.Size here would mix DPI-dependent and DPI-independent units
        // and reopen the window scaled by the monitor's DPI factor.
        if (!Win32.TryGetBounds(Win32.GetWindowHandle(this), out var bounds))
        {
            return;
        }

        _windowStateStore.Save(new WindowState(
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            bounds.Left,
            bounds.Top));
    }

    // ======== minimum window size ========

    private const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>
    /// Holds the delegate for the duration of the session.
    /// </summary>
    /// <remarks>
    /// SetWindowLongPtr stores a raw function pointer. Without a managed
    /// reference the delegate can be collected, and the next message to the
    /// window would jump into freed memory.
    /// </remarks>
    private Win32.SubclassProc? _subclassProc;

    /// <summary>
    /// Hooks the window procedure so <c>WM_GETMINMAXINFO</c> reports our minimum
    /// size, which is what makes the OS refuse smaller resize drags.
    /// </summary>
    private void InstallMinimumSizeHook()
    {
        var hwnd = Win32.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            Serilog.Log.Warning("No HWND; minimum window size will not be enforced");
            return;
        }

        _subclassProc = OnSubclassMessage;
        Win32.SetWindowSubclass(hwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr OnSubclassMessage(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr idSubclass,
        IntPtr dwRefData)
    {
        if (message == WM_GETMINMAXINFO)
        {
            var info = System.Runtime.InteropServices.Marshal
                .PtrToStructure<Win32.MINMAXINFO>(lParam);

            // The constants are DIPs (they describe how much XAML layout has to
            // fit); this message is in physical pixels, so convert rather than
            // assigning them directly.
            info.ptMinTrackSize.X = Win32.DipsToPixels(hwnd, WindowState.MinWidth);
            info.ptMinTrackSize.Y = Win32.DipsToPixels(hwnd, WindowState.MinHeight);

            System.Runtime.InteropServices.Marshal
                .StructureToPtr(info, lParam, fDeleteOld: false);

            return IntPtr.Zero;
        }

        return Win32.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Puts keyboard focus on the selected navigation item at startup.
    /// </summary>
    /// <remarks>
    /// Without this the account chip takes focus: it is the first focusable
    /// element in the pane header, and NavigationView's own selected item does
    /// not claim focus on its own. That left the window opening with the cursor
    /// visibly parked on the account button rather than on the page the user is
    /// actually looking at.
    /// </remarks>
    private void FocusInitialNavItem()
    {
        // Move focus off the account chip without letting it land on a control.
        //
        // The chip is the first focusable element in the pane header, so it held
        // startup focus by default. Focusing the frame instead put focus on the
        // frame's first focusable child (the version combo box), which drew a
        // focus rectangle around it. Making the frame itself focusable gives the
        // window a neutral resting focus; Tab still descends into the sidebar
        // and the page in order.
        ContentFrame.IsTabStop = true;
        ContentFrame.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Starts or stops the account-name marquee depending on whether the text
    /// overflows its slot.
    /// </summary>
    /// <remarks>
    /// TextBlock has no marquee, so the ScrollViewer scrolls instead. The width
    /// comparison has to happen after layout, which is why callers defer this
    /// through the dispatcher: during a property change the new text has not
    /// been measured yet and <see cref="FrameworkElement.ActualWidth"/> still
    /// reports the previous value.
    /// </remarks>
    private void UpdateAccountNameMarquee()
    {
        if (AccountNameText.ActualWidth <= 0 || AccountNameScroller.ActualWidth <= 0)
        {
            return;
        }

        var overflows = AccountNameText.ActualWidth > AccountNameScroller.ActualWidth + 1;

        AccountNameScroller.HorizontalScrollMode = overflows
            ? ScrollMode.Auto
            : ScrollMode.Disabled;

        // No ChangeView animation here: the scroll is driven by the user, and an
        // automatic loop would keep repainting the pane for no benefit.
        if (!overflows)
        {
            AccountNameScroller.ChangeView(0, null, null, disableAnimation: true);
        }
    }

    /// <summary>
    /// Keeps the nav rail and page title in step with whatever page the frame is
    /// actually showing, including navigations the rail did not initiate.
    /// </summary>
    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        var page = PageFor(e.SourcePageType);

        foreach (var obj in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (obj is NavigationViewItem { Tag: string tag } item
                && tag == TagFor(page))
            {
                // Guard against re-entering NavView_SelectionChanged, which would
                // ask the navigation service for the page it is already showing.
                if (!ReferenceEquals(NavView.SelectedItem, item))
                {
                    NavView.SelectedItem = item;
                }

                break;
            }
        }

        ViewModel.PageTitle = TitleForPage(page);
    }

    private static AppPage PageFor(Type pageType) => pageType switch
    {
        _ when pageType == typeof(Views.InstancesPage) => AppPage.Instances,
        _ when pageType == typeof(Views.DownloadPage) => AppPage.Download,
        _ when pageType == typeof(Views.TransferPage) => AppPage.Transfer,
        _ when pageType == typeof(Views.SettingsPage) => AppPage.Settings,
        _ => AppPage.Launch,
    };

    private static string TagFor(AppPage page) => page switch
    {
        AppPage.Instances => "instances",
        AppPage.Download => "download",
        AppPage.Transfer => "transfer",
        AppPage.Settings => "settings",
        _ => "launch",
    };

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_shellInitialized || !App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) return;
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var page = tag switch
        {
            "launch" => AppPage.Launch,
            "instances" => AppPage.Instances,
            "download" => AppPage.Download,
            "transfer" => AppPage.Transfer,
            "settings" => AppPage.Settings,
            _ => AppPage.Launch,
        };

        _navigation.Navigate(page);
        ViewModel.PageTitle = TitleForPage(page);
    }

    private void AccountChip_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) return;
        ShowAccountFlyout();
    }

    /// <summary>
    /// Opens account management as a flyout anchored to the account chip.
    /// </summary>
    /// <remarks>
    /// A flyout rather than a page: identities are managed in passing, and
    /// navigating away would take over the content frame and leave the nav
    /// selection pointing at an item that is not on screen.
    ///
    /// The panel is rebuilt per open on purpose. It is cheap, and it guarantees
    /// the list reflects the current account state without wiring up change
    /// tracking for a surface that is only visible for seconds at a time.
    /// </remarks>
    private void ShowAccountFlyout()
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Content = new Views.AccountsPanel(),
        };

        flyout.ShowAt(AccountChipButton);
    }

    private static string TitleForPage(AppPage page) => page switch
    {
        AppPage.Launch => "启动",
        AppPage.Instances => "实例管理",
        AppPage.Download => "下载",
        AppPage.Transfer => "传输",
        AppPage.Settings => "设置",
        _ => "97Launch",
    };
}
