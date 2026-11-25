using System.Diagnostics;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 调用 ShellExecute 让系统默认浏览器打开授权地址。不做任何网络请求，
/// 也不等待浏览器退出——启动即完成，绝不挂住调用方。
/// </summary>
public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    private const string FailedMessage = "无法打开浏览器，请手动复制登录地址完成登录。";

    public Task OpenAsync(Uri url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 只放行 http/https：既避免把任意 scheme 交给 shell，也让失败路径可确定地复现。
        if (url is null ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(FailedMessage);
        }

        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(FailedMessage, ex);
        }
    }
}
