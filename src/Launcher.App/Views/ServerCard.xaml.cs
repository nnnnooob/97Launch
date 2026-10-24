using Launcher.App.ViewModels;
using Launcher.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Launcher.App.Views;

public sealed partial class ServerCard : UserControl
{
    public event Action<ServerCardViewModel>? InstallRequested;
    public event Action<ServerCardViewModel>? JoinRequested;

    public ServerCard() => InitializeComponent();
    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var size = Math.Max(0, e.NewSize.Width - 2);
        if (Math.Abs(ImageRegion.Height - size) > .5) ImageRegion.Height = size;
    }
    private void Install_Click(object sender, RoutedEventArgs e)
    { if (DataContext is ServerCardViewModel vm) InstallRequested?.Invoke(vm); }
    private void Join_Click(object sender, RoutedEventArgs e)
    { if (DataContext is ServerCardViewModel vm) JoinRequested?.Invoke(vm); }
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerCardViewModel vm) return;
        var toast = App.Services.GetRequiredService<ToastService>();
        try
        {
            var data = new DataPackage();
            data.SetText(vm.Address);
            Clipboard.SetContent(data);
            toast.Show("已复制 IP 地址", vm.Address);
        }
        catch (Exception) { toast.ShowError("复制失败", "剪贴板暂时不可用，请稍后重试。"); }
    }
}
