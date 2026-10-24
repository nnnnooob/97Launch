using FluentAssertions;
using Launcher.App.Services;
using Xunit;

namespace Launcher.Core.Tests;

public sealed class ToastServiceTests
{
    [Fact]
    public void Distinguishes_queued_work_and_merges_a_burst_of_downloads()
    {
        var service = new ToastService();
        var messages = new List<ToastMessage>();
        service.MessageRequested += messages.Add;
        service.DownloadAccepted("Sodium", "生存", 2);
        service.DownloadAccepted("Iris", "生存", 3);
        messages[0].Title.Should().Be("已加入下载队列");
        messages[0].Detail.Should().Contain("前方还有 2 项任务");
        messages[1].Title.Should().Be("已加入 2 项下载任务");
        service.ShowError("下载失败", "请重试");
        service.DownloadAccepted("Lithium", "生存", 0);
        messages[^1].Title.Should().Be("开始下载");
        messages[^2].IsError.Should().BeTrue();
    }
}
