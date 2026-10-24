using Launcher.App.Services;

namespace Launcher.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (UpdateBootstrap.HandleArguments(args)) return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
