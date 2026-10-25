namespace Launcher.App.Services;

public sealed class JavaRuntimeChangeNotifier
{
    public event EventHandler? RuntimesChanged;

    public void NotifyChanged() => RuntimesChanged?.Invoke(this, EventArgs.Empty);
}
