using TeachCraft.App.ViewModels;

namespace TeachCraft.Tests;

public sealed class CertificateBatchHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopenCertificate_PreservesResultAndOutputPath(bool isError)
    {
        var host = new MainViewModel();
        var tool = host.FindTool("certificate-batch")!;
        host.OpenTool(tool);
        tool.HasResult = true;
        tool.ResultText = isError ? "请换一个保存位置。" : "已生成 2 页奖状。";
        tool.ResultIsError = isError;
        tool.LastOutputPath = Path.Combine(Path.GetTempPath(), "certificate-result.pdf");
        var resultText = tool.ResultText;
        var outputPath = tool.LastOutputPath;

        host.ClosePanels();
        host.OpenTool(host.FindTool("image-convert")!);
        host.OpenTool(tool);

        Assert.True(host.IsCertificateBatchPanelOpen);
        Assert.True(tool.HasResult);
        Assert.Equal(resultText, tool.ResultText);
        Assert.Equal(isError, tool.ResultIsError);
        Assert.Equal(outputPath, tool.LastOutputPath);
    }

    [Fact]
    public void ReopenOtherTool_RetainsExistingResultReset()
    {
        var host = new MainViewModel();
        var tool = host.FindTool("image-convert")!;
        tool.HasResult = true;
        tool.ResultText = "已完成。";

        host.OpenTool(tool);

        Assert.False(tool.HasResult);
        Assert.Empty(tool.ResultText);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void DismissalPredicate_BlocksOnlyOpenBusyCertificate(
        bool panelOpen, bool certificate, bool busy, bool expected)
    {
        Assert.Equal(expected, MainViewModel.ShouldKeepCertificatePanelOpen(panelOpen, certificate, busy));
    }

    [Fact]
    public void BusyCertificate_DismissalKeepsPanelAndExplainsCancellation()
    {
        var host = new MainViewModel();
        var tool = host.FindTool("certificate-batch")!;
        host.OpenTool(tool);
        tool.IsBusy = true;

        Assert.False(host.CanDismissPanel());
        Assert.True(host.IsCertificateBatchPanelOpen);
        Assert.Contains("取消", host.StatusText);
        Assert.Contains("等待完成", host.StatusText);

        tool.IsBusy = false;
        Assert.True(host.CanDismissPanel());
        host.ClosePanels();
        Assert.False(host.IsPanelOpen);
    }
}
