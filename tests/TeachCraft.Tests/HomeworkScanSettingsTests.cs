using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkScanSettingsTests
{
    [Fact]
    public void Default_IsFeederSingleA4At300Dpi()
    {
        var settings = ScanSettings.Default;

        Assert.Equal(ScanSource.FeederSingle, settings.Source);
        Assert.Equal(ScanPageSize.A4, settings.PageSize);
        Assert.Equal(300, settings.ResolutionDpi);
        Assert.Equal(ScanColorMode.BlackWhite, settings.ColorMode);
        Assert.Equal(ScanPageLimit.All, settings.PageLimit);
        Assert.True(settings.IsFeeder);
        Assert.False(settings.IsDuplex);
        Assert.True(settings.ScanAllPages);
    }

    [Theory]
    [InlineData(120, 200)]
    [InlineData(200, 200)]
    [InlineData(250, 300)]
    [InlineData(300, 300)]
    [InlineData(400, 600)]
    [InlineData(600, 600)]
    [InlineData(1200, 600)]
    public void NormalizeResolution_ClampsToSupportedBuckets(int input, int expected) =>
        Assert.Equal(expected, ScanSettings.NormalizeResolution(input));

    [Fact]
    public void Normalized_FlatbedForcesFirstPage()
    {
        var settings = new ScanSettings
        {
            Source = ScanSource.Flatbed,
            PageLimit = ScanPageLimit.All,
        };

        var normalized = settings.Normalized();

        Assert.Equal(ScanPageLimit.FirstPage, normalized.PageLimit);
        Assert.False(normalized.ScanAllPages);
    }

    [Theory]
    [InlineData(ScanPageSize.A4, 300, 2480, 3508)]
    [InlineData(ScanPageSize.A3, 300, 3508, 4961)]
    [InlineData(ScanPageSize.B5, 300, 2079, 2953)]
    [InlineData(ScanPageSize.EightK, 300, 3189, 4606)]
    [InlineData(ScanPageSize.SixteenK, 300, 2303, 3189)]
    [InlineData(ScanPageSize.A4, 200, 1654, 2339)]
    public void PixelExtent_UsesPaperSizeAndDpi(
        ScanPageSize size,
        int dpi,
        int expectedWidth,
        int expectedHeight)
    {
        var settings = new ScanSettings { PageSize = size, ResolutionDpi = dpi };

        var extent = settings.PixelExtent;

        Assert.Equal(expectedWidth, extent.Width);
        Assert.Equal(expectedHeight, extent.Height);
    }

    [Theory]
    [InlineData(50, 75)]
    [InlineData(150, 150)]
    [InlineData(300, 300)]
    [InlineData(1200, 1200)]
    [InlineData(1500, 1200)]
    public void NormalizedForDriver_AllowsDriverReportedDpi(int input, int expected)
    {
        var settings = new ScanSettings { ResolutionDpi = input };

        Assert.Equal(expected, settings.NormalizedForDriver().ResolutionDpi);
    }

    [Fact]
    public void NormalizedForDriver_FlatbedForcesFirstPage()
    {
        var settings = new ScanSettings
        {
            Source = ScanSource.Flatbed,
            PageLimit = ScanPageLimit.All,
            ResolutionDpi = 150,
        };

        var normalized = settings.NormalizedForDriver();

        Assert.Equal(150, normalized.ResolutionDpi);
        Assert.Equal(ScanPageLimit.FirstPage, normalized.PageLimit);
    }

    [Fact]
    public void Summary_IncludesAllSettings()
    {
        var settings = new ScanSettings
        {
            Source = ScanSource.FeederDuplex,
            PageSize = ScanPageSize.A4,
            ResolutionDpi = 300,
            ColorMode = ScanColorMode.Grayscale,
            PageLimit = ScanPageLimit.All,
        };

        var summary = ScanSettingsText.Summary(settings);

        Assert.Contains("输稿器（双面）", summary);
        Assert.Contains("A4", summary);
        Assert.Contains("300 dpi", summary);
        Assert.Contains("灰度", summary);
        Assert.Contains("全部页面", summary);
    }
}
