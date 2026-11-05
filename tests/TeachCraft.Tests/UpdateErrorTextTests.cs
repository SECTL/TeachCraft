using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 网络失败必须说清是哪一种。
/// 老师点了「检查更新」没成功，只看到「请检查文件后重试」等于功能坏了却装死。
/// </summary>
public sealed class UpdateErrorTextTests
{
    private static string Describe(Exception ex) => UpdateErrorText.DescribeForUi(ex, "检查更新");

    [Fact]
    public void NoNetwork_SaysSo()
    {
        var text = Describe(new HttpRequestException("boom", new SocketException((int)SocketError.HostNotFound)));

        Assert.Contains("连不上网", text);
        Assert.DoesNotContain("处理失败：", text);
        Assert.DoesNotContain("请检查文件后重试", text);
    }

    [Fact]
    public void Timeout_SaysSo()
    {
        var text = Describe(new TaskCanceledException("timeout"));

        Assert.Contains("超时", text);
        Assert.DoesNotContain("处理失败：", text);
    }

    [Fact]
    public void TlsFailure_ExplainsInterception()
    {
        var text = Describe(new HttpRequestException("ssl", new AuthenticationException("cert")));

        Assert.Contains("拦截", text);
        Assert.Contains("证书", text);
    }

    [Fact]
    public void ProxyAuth_AsksForLogin()
    {
        var text = UpdateErrorText.ForStatus(HttpStatusCode.ProxyAuthenticationRequired, "检查更新");

        Assert.Contains("认证", text);
    }

    [Fact]
    public void RateLimited_AsksToRetryLater()
    {
        var text = UpdateErrorText.ForStatus((HttpStatusCode)403, "检查更新");

        Assert.Contains("频繁", text);
    }

    [Fact]
    public void NotFound_SaysRetryLater()
    {
        var text = UpdateErrorText.ForStatus(HttpStatusCode.NotFound, "检查更新");

        Assert.Contains("稍后", text);
    }

    [Fact]
    public void HtmlInsteadOfJson_MeansPortalOrProxy()
    {
        // 校园网/门禁系统常返回 200 + 登录页，这时解析 JSON 会失败。
        var text = UpdateErrorText.ForNonJsonBody("检查更新");

        Assert.Contains("拦截", text);
        Assert.Contains("浏览器", text);
    }

    [Fact]
    public void AllSevenReasonsAreDistinct()
    {
        var reasons = new[]
        {
            Describe(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))),
            Describe(new TaskCanceledException()),
            Describe(new HttpRequestException("x", new AuthenticationException())),
            UpdateErrorText.ForStatus(HttpStatusCode.ProxyAuthenticationRequired, "检查更新"),
            UpdateErrorText.ForStatus((HttpStatusCode)403, "检查更新"),
            UpdateErrorText.ForStatus(HttpStatusCode.NotFound, "检查更新"),
            UpdateErrorText.ForNonJsonBody("检查更新"),
        };

        Assert.Equal(reasons.Length, reasons.Distinct().Count());
    }

    [Fact]
    public void UnclassifiedException_FallsBackToActionPrefixedSentence()
    {
        var text = Describe(new InvalidOperationException("?"));

        // 兜底句仍然保留「{action}失败：」前缀，和项目里其它错误话术一致。
        Assert.StartsWith("检查更新失败：", text);
    }

    [Fact]
    public void LocalWriteFailure_IsNotReportedAsInstallDirectoryProblem()
    {
        // IS-11 保证不写安装目录，所以「安装目录不可写」这类分支不存在；
        // 真正会失败的是本机状态/下载目录。
        var text = Describe(new UnauthorizedAccessException("denied"));

        Assert.DoesNotContain("安装目录不可写", text);
        Assert.StartsWith("检查更新失败：", text);
    }

    [Fact]
    public void MissingChecksums_IsItsOwnReason()
    {
        var text = UpdateErrorText.ForMissingChecksums("检查更新");

        Assert.Contains("校验", text);
    }
}
