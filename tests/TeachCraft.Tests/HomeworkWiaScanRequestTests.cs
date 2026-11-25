using Homework.App.Scanning;
using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkWiaScanRequestTests
{
    [Fact]
    public void Build_A4BlackWhiteFeederSingle()
    {
        var request = WiaScanRequest.Build(new ScanSettings
        {
            Source = ScanSource.FeederSingle,
            PageSize = ScanPageSize.A4,
            ResolutionDpi = 300,
            ColorMode = ScanColorMode.BlackWhite,
            PageLimit = ScanPageLimit.FirstPage,
        });

        Assert.Equal(WiaScanRequest.IntentText, request.Intent);
        Assert.Equal(300, request.XResolution);
        Assert.Equal(300, request.YResolution);
        Assert.Equal(WiaScanRequest.Feeder, request.DocumentHandling);
        Assert.Equal(1, request.Pages);
        Assert.Equal(WiaScanRequest.PageSizeA4, request.PageSize);
        Assert.Equal(2480, request.WidthPixels);
        Assert.Equal(3508, request.HeightPixels);
    }

    [Fact]
    public void Build_A4ColorFeederDuplexAll()
    {
        var request = WiaScanRequest.Build(new ScanSettings
        {
            Source = ScanSource.FeederDuplex,
            PageSize = ScanPageSize.A4,
            ResolutionDpi = 300,
            ColorMode = ScanColorMode.Color,
            PageLimit = ScanPageLimit.All,
        });

        Assert.Equal(WiaScanRequest.IntentColor, request.Intent);
        Assert.Equal(WiaScanRequest.Feeder | WiaScanRequest.Duplex, request.DocumentHandling);
        Assert.Equal(0, request.Pages);
        Assert.Equal(WiaScanRequest.PageSizeA4, request.PageSize);
    }

    [Fact]
    public void Build_EightKGrayscale_UsesCustomPageSize()
    {
        var request = WiaScanRequest.Build(new ScanSettings
        {
            Source = ScanSource.FeederSingle,
            PageSize = ScanPageSize.EightK,
            ResolutionDpi = 300,
            ColorMode = ScanColorMode.Grayscale,
            PageLimit = ScanPageLimit.FirstPage,
        });

        Assert.Equal(WiaScanRequest.IntentGrayscale, request.Intent);
        Assert.Equal(WiaScanRequest.PageSizeCustom, request.PageSize);
        Assert.Equal(3189, request.WidthPixels);
        Assert.Equal(4606, request.HeightPixels);
    }

    [Fact]
    public void Build_KeepsDriverReportedResolution()
    {
        var request = WiaScanRequest.Build(new ScanSettings { ResolutionDpi = 150 });

        Assert.Equal(150, request.XResolution);
        Assert.Equal(150, request.YResolution);
    }

    [Fact]
    public void Build_AutoPageSize_UsesAutoCode()
    {
        var request = WiaScanRequest.Build(new ScanSettings
        {
            Source = ScanSource.Flatbed,
            PageSize = ScanPageSize.Auto,
            PageLimit = ScanPageLimit.All,
        });

        Assert.Equal(WiaScanRequest.PageSizeAuto, request.PageSize);
        Assert.Equal(WiaScanRequest.Flatbed, request.DocumentHandling);
        Assert.Equal(1, request.Pages);
    }
}
