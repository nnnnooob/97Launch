using Launcher.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Launcher.App.Views;

namespace Launcher.App;

public sealed partial class AuthorizationWindow : Window
{
    private const int WidthDips = 960;
    private const int HeightDips = 600;
    private readonly Win32.SubclassProc _subclass;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _applyingGeometry;
    public event EventHandler? StartupRetryRequested;

    internal async Task PrepareFirstStartupAsync(CancellationToken token)
    {
        Title = AuthorizationTitle.Text = $"{AppInfo.Name} · 首次准备";
        var view = new StartupInitializationView();
        view.RetryRequested += (_, _) => StartupRetryRequested?.Invoke(this, EventArgs.Empty);
        WindowContent.Content = view;
        await view.RunAsync(token);
    }

    internal void ShowInitializationError(string message)
    {
        if (WindowContent.Content is StartupInitializationView view) view.ShowError(message);
    }

    internal void ShowPairing()
    {
        Title = AuthorizationTitle.Text = $"{AppInfo.Name} · 设备验证";
        WindowContent.Content = new WelcomePairingView();
    }

    public AuthorizationWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} · 设备验证";
        AuthorizationTitle.Text = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AuthorizationTitleBar);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        _subclass = OnWindowMessage;
        Win32.SetWindowSubclass(Win32.GetWindowHandle(this), _subclass, IntPtr.Zero, IntPtr.Zero);
        AppWindow.Changed += (_, _) =>
        {
            if (!_applyingGeometry && (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped ||
                AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }))
                ApplyFixedGeometry();
        };
    }

    public void ApplyFixedGeometry()
    {
        if (_applyingGeometry) return;
        _applyingGeometry = true;
        try
        {
            if (AppWindow.Presenter is not OverlappedPresenter)
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            var hwnd = Win32.GetWindowHandle(this);
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var margin = Win32.DipsToPixels(hwnd, 24);
            _pixelWidth = Math.Min(Win32.DipsToPixels(hwnd, WidthDips), Math.Max(1, work.Width - margin));
            _pixelHeight = Math.Min(Win32.DipsToPixels(hwnd, HeightDips), Math.Max(1, work.Height - margin));
            Win32.SetWindowPos(hwnd, IntPtr.Zero, work.X + (work.Width - _pixelWidth) / 2,
                work.Y + (work.Height - _pixelHeight) / 2, _pixelWidth, _pixelHeight,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        finally { _applyingGeometry = false; }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) is 0xF000 or 0xF030) return IntPtr.Zero;
        if (message == 0x00A3 && wParam.ToInt64() == 2) return IntPtr.Zero;
        if (message == 0x0024)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = _pixelWidth > 0 ? _pixelWidth : Win32.DipsToPixels(hwnd, WidthDips);
            info.ptMinTrackSize.Y = _pixelHeight > 0 ? _pixelHeight : Win32.DipsToPixels(hwnd, HeightDips);
            info.ptMaxTrackSize = info.ptMinTrackSize;
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }
        return Win32.DefSubclassProc(hwnd, message, wParam, lParam);
    }
}
