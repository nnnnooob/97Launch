using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Services;
using Microsoft.UI.Xaml.Media;

namespace Launcher.App.ViewModels;

public sealed partial class ServerCardViewModel(CatalogServer server) : ObservableObject
{
    public CatalogServer Server { get; private set; } = server;
    public string Id => Server.Id;
    public string Name => Server.Name;
    public string Description => string.IsNullOrWhiteSpace(Server.Description) ? (string.IsNullOrWhiteSpace(Server.Motd) ? "Minecraft 多人游戏服务器" : Server.Motd.Replace('\n', ' ')) : Server.Description;
    public string Address => MinecraftServerList.Address(Server);
    public string StateText => Server.State switch { "online" => "在线", "starting" => "启动中", "stopped" => "未运行", "stopping" => "停止中", "maintenance" => "维护中", "unreachable" => "游戏尚未就绪", _ => "状态未知" };
    public Brush StateBrush => (Brush)App.Current.Resources[Server.State == "online" ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush"];
    public string PlayersText => Server.Players is { } players && Server.MaxPlayers is { } maximum ? $"{players} / {maximum} 人" : "人数未知";
    public string VersionText => string.Join(" · ", new[] { Server.Minecraft, Server.Loader }.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } value ? value : "版本未指定";
    public string CopyAddressLabel => $"复制服务器 IP 地址 {Address}";
    public string LatencyText => Server.CanJoin && LatencyMs is { } ms ? $"{ms} ms" : "—";
    public string LatencyHint => Server.CanJoin && LatencyMs is { } ms ? $"连接延迟 {ms} ms" : "暂时无法测得连接延迟";
    private int SignalBars => !Server.CanJoin || LatencyMs is null ? 0 : LatencyMs switch { <= 100 => 4, <= 200 => 3, <= 400 => 2, _ => 1 };
    public double Signal1Opacity => SignalBars >= 1 ? 1 : .18;
    public double Signal2Opacity => SignalBars >= 2 ? 1 : .18;
    public double Signal3Opacity => SignalBars >= 3 ? 1 : .18;
    public double Signal4Opacity => SignalBars >= 4 ? 1 : .18;
    public Brush SignalBrush => (Brush)App.Current.Resources[SignalBars switch
    {
        >= 3 => "SystemFillColorSuccessBrush",
        2 => "SystemFillColorCautionBrush",
        1 => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush"
    }];
    public string InstallText => Server.Pack is not null ? "获取相关实例" : "暂无相关实例";
    public bool CanInstall => Server.Pack is not null && !IsWorking;
    public bool CanJoin => Server.CanJoin && CanLaunch && !IsWorking;
    public string JoinText => IsJoining ? "正在启动实例" : "启动&加入";

    [ObservableProperty] private string _iconPath = ServerCatalogService.DefaultIcon;
    [ObservableProperty] private int? _latencyMs;
    [ObservableProperty] private bool _canLaunch;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isJoining;

    public void Update(CatalogServer value)
    {
        Server = value;
        OnPropertyChanged(string.Empty);
    }
    partial void OnCanLaunchChanged(bool value) => OnPropertyChanged(nameof(CanJoin));
    partial void OnIsWorkingChanged(bool value) { OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CanJoin)); }
    partial void OnIsJoiningChanged(bool value) => OnPropertyChanged(nameof(JoinText));
    partial void OnLatencyMsChanged(int? value)
    {
        foreach (var property in new[] { nameof(LatencyText), nameof(LatencyHint), nameof(SignalBrush),
                     nameof(Signal1Opacity), nameof(Signal2Opacity), nameof(Signal3Opacity), nameof(Signal4Opacity) })
            OnPropertyChanged(property);
    }
}
