using Launcher.App.Services;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Launcher.Core.Versions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Launcher.App;

/// <summary>
/// Composition root and service locator.
/// </summary>
/// <remarks>
/// <para>
/// The container is built once on first access and then frozen. Code-behind
/// files resolve their view models through <see cref="Current"/>; that keeps
/// constructors parameterless, which XAML requires when it instantiates pages.
/// </para>
/// <para>
/// Everything is registered as a singleton: view models are shared per page so
/// navigation preserves their state, and the navigation and dialog services are
/// stateless coordinators.
/// </para>
/// </remarks>
public static class AppServiceProvider
{
    private static readonly object Sync = new();

    private static IServiceProvider? _current;

    public static IServiceProvider Current
    {
        get
        {
            if (_current is not null)
            {
                return _current;
            }

            lock (Sync)
            {
                _current ??= Build();
                return _current;
            }
        }
    }

    private static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: false));

        // --- shell services ---
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton(LogBuffer.Shared);
        services.AddSingleton<ToastService>();
        services.AddSingleton<LauncherAuthorizationService>();
        services.AddSingleton<AppUpdateService>();
        services.AddSingleton<ServerCatalogService>();
        services.AddSingleton<PendingResourceBrowse>();

        // Carries "open this instance's settings" across a page navigation.
        services.AddSingleton<PendingInstanceSettings>();

        // Carries "select this instance" after an install finishes.
        services.AddSingleton<PendingInstanceSelection>();

        // --- Minecraft folders and instances ---
        services.AddSingleton<WindowStateStore>();
        services.AddSingleton<GameFolderService>();
        services.AddSingleton<InstanceScanner>();
        services.AddSingleton<MinecraftVersionService>(provider => new MinecraftVersionService(
            provider.GetRequiredService<ILogger<MinecraftVersionService>>(),
            Path.Combine(AppInfo.DataDirectory, "cache")));
        services.AddSingleton<PclFileDownloadManager>(provider =>
            new PclFileDownloadManager(provider.GetRequiredService<ILogger<PclFileDownloadManager>>()));
        services.AddSingleton<MinecraftInstallService>();
        services.AddSingleton<JavaRuntimeChangeNotifier>();
        services.AddSingleton<JavaRuntimeService>();
        services.AddSingleton<LaunchHistoryStore>();
        services.AddSingleton<GameSessionManager>();
        services.AddSingleton<InstanceContentService>();
        services.AddSingleton<InstanceModificationService>();
        services.AddSingleton<InstanceExportService>();
        services.AddSingleton<LocalModpackService>();
        services.AddSingleton<LocalModOnlineService>();
        services.AddSingleton<ModInstallService>();
        services.AddSingleton<MinecraftLaunchService>();
        services.AddSingleton<ModrinthResourceService>();
        services.AddSingleton<CommunityCredentials>();
        services.AddSingleton(provider => new CurseForgeResourceService(new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
            provider.GetRequiredService<CommunityCredentials>().ReadKey));
        services.AddSingleton<CommunityResourceService>();
        services.AddSingleton<ModrinthModpackInstaller>();

        // User preferences read by the download service and the instances page.
        // Registered directly so view models can also resolve it explicitly.
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<InstanceLaunchSettingsStore>();

        // --- accounts ---
        services.AddSingleton(_ => new AccountStore(Path.Combine(AppInfo.DataDirectory, "accounts.json")));
        services.AddSingleton(_ => new SkinService(Path.Combine(AppInfo.DataDirectory, "cache", "skins")));
        services.AddSingleton<MicrosoftLogin>();
        services.AddSingleton<AuthlibLogin>();
        services.AddSingleton<AccountManager>();

        // --- view models ---
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<LaunchViewModel>();
        services.AddSingleton<InstancesViewModel>();
        services.AddSingleton<DownloadViewModel>();
        services.AddSingleton<TransferViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Transient: each dialog opening loads a different instance.
        services.AddTransient<InstanceSettingsViewModel>();
        services.AddSingleton<AccountsViewModel>();

        var provider = services.BuildServiceProvider();

        Log.Debug("Service container built with {Count} registrations", services.Count);
        return provider;
    }
}
