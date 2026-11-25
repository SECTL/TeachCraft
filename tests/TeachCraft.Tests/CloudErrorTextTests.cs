using System.IO;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

/// <summary>
/// 业务异常经 ErrorText 时必须原样透传，不能被误报成「请检查文件」。
/// ErrorText 是 internal，经 CloudErrorText.DescribeForUi 在 Core 内嵌的那份上断言。
/// </summary>
public sealed class CloudErrorTextTests
{
    [Fact]
    public void Describe_CloudAuthException_ReturnsMessageVerbatim()
    {
        const string message = "登录状态已失效，请重新登录。";

        var result = CloudErrorText.DescribeForUi(new CloudAuthException(message), "上传共享名单");

        Assert.Equal(message, result);
        Assert.DoesNotContain("失败：", result, StringComparison.Ordinal);
        Assert.DoesNotContain("文件", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_CloudConflictException_ReturnsMessageVerbatim()
    {
        var remote = new CloudDocumentInfo("doc-1", "共享名单", "roster", DateTimeOffset.UtcNow, 128, 7);
        var ex = new CloudConflictException(remote);

        var result = CloudErrorText.DescribeForUi(ex, "上传共享名单");

        Assert.Equal(ex.Message, result);
        Assert.DoesNotContain("失败：", result, StringComparison.Ordinal);
        Assert.DoesNotContain("文件", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ToolboxException_ReturnsMessageVerbatim()
    {
        const string message = "页码 “99” 超出范围（共 3 页）。";

        var result = CloudErrorText.DescribeForUi(new ToolboxException(message), "拆分 PDF");

        Assert.Equal(message, result);
    }

    [Fact]
    public void Describe_PlainIOException_KeepsFileWording()
    {
        var result = CloudErrorText.DescribeForUi(new IOException("disk error"), "上传共享名单");

        Assert.Equal("上传共享名单失败：读写文件时出错，请确认磁盘空间充足、文件未被占用。", result);
    }

    [Fact]
    public void Describe_LockedIOException_KeepsLockedWording()
    {
        var result = CloudErrorText.DescribeForUi(new IOException("The file is being used by another process."), "导出");

        Assert.Equal("导出失败：文件正被其他程序占用，关闭后重试。", result);
    }
}
