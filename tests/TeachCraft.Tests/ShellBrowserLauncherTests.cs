using TeachCraft.App.Cloud;

namespace TeachCraft.Tests;

public sealed class ShellBrowserLauncherTests
{
    [Fact]
    public async Task OpenAsync_InvalidScheme_ThrowsChineseMessage()
    {
        var launcher = new ShellBrowserLauncher();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.OpenAsync(new Uri("bogus://login"), CancellationToken.None));

        Assert.Contains("无法打开浏览器，请手动复制登录地址完成登录。", ex.Message);
    }
}
