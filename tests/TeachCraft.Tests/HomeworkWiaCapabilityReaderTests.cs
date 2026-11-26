using Homework.App.Scanning;
using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkWiaCapabilityReaderTests
{
    [Fact]
    public void Read_MapsAllReportedCapabilities()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.DocumentHandlingCapabilitiesProperty] = WiaPropertyInfo.Flag(1, 2, 4),
            [WiaScanRequest.CurIntentProperty] = WiaPropertyInfo.List(1, 2, 4),
            [WiaScanRequest.XResolutionProperty] = WiaPropertyInfo.List(75, 150, 300, 600),
            [WiaScanRequest.PageSizeProperty] = WiaPropertyInfo.List(3, 5, 9, 100),
            [WiaScanRequest.PagesProperty] = WiaPropertyInfo.Range(0, 50, 1),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.True(capabilities.IsReported);
        Assert.Equal(
            [ScanSource.FeederSingle, ScanSource.Flatbed, ScanSource.FeederDuplex],
            capabilities.Sources);
        Assert.Equal(
            [ScanColorMode.Color, ScanColorMode.Grayscale, ScanColorMode.BlackWhite],
            capabilities.ColorModes);
        Assert.Equal([75, 150, 300, 600], capabilities.Resolutions);
        Assert.Equal(
            [ScanPageSize.A4, ScanPageSize.A3, ScanPageSize.B5, ScanPageSize.Auto],
            capabilities.PageSizes);
        Assert.Equal(50, capabilities.MaxPages);
    }

    [Fact]
    public void Read_RangeResolution_IntersectsKnownLadder()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.XResolutionProperty] = WiaPropertyInfo.Range(75, 600, 75),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.Equal([75, 150, 300, 600], capabilities.Resolutions);
    }

    [Fact]
    public void Read_FlagSources_RespectsFeederAndDuplexBits()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.DocumentHandlingProperty] = WiaPropertyInfo.Flag(1, 4),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.Equal([ScanSource.FeederSingle, ScanSource.FeederDuplex], capabilities.Sources);
        Assert.DoesNotContain(ScanSource.Flatbed, capabilities.Sources);
    }

    [Fact]
    public void Read_CustomPageSize_IsNotExposed()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.PageSizeProperty] = WiaPropertyInfo.List(0, 3, 5),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.Equal([ScanPageSize.A4, ScanPageSize.A3], capabilities.PageSizes);
        Assert.DoesNotContain(ScanPageSize.EightK, capabilities.PageSizes);
        Assert.DoesNotContain(ScanPageSize.SixteenK, capabilities.PageSizes);
    }

    [Fact]
    public void Read_SingleValues_MapFlatbedBlackWhite300()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.DocumentHandlingCapabilitiesProperty] = WiaPropertyInfo.Single(2),
            [WiaScanRequest.CurIntentProperty] = WiaPropertyInfo.Single(4),
            [WiaScanRequest.XResolutionProperty] = WiaPropertyInfo.Single(300),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.True(capabilities.IsReported);
        Assert.Equal([ScanSource.Flatbed], capabilities.Sources);
        Assert.Equal([ScanColorMode.BlackWhite], capabilities.ColorModes);
        Assert.Equal([300], capabilities.Resolutions);
    }

    [Fact]
    public void Read_SingleFeederDuplexValue_MapsBothFeederModes()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.DocumentHandlingProperty] = WiaPropertyInfo.Single(5),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.Equal([ScanSource.FeederSingle, ScanSource.FeederDuplex], capabilities.Sources);
        Assert.DoesNotContain(ScanSource.Flatbed, capabilities.Sources);
    }

    [Fact]
    public void Read_EmptySnapshot_ReturnsUnknownDefaults()
    {
        var capabilities = WiaCapabilityReader.Read(
            new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>()));

        Assert.False(capabilities.IsReported);
        Assert.Equal([ScanSource.FeederSingle], capabilities.Sources);
        Assert.Equal([ScanPageSize.A4], capabilities.PageSizes);
        Assert.Equal([300], capabilities.Resolutions);
        Assert.Equal([ScanColorMode.BlackWhite], capabilities.ColorModes);
        Assert.Equal(100, capabilities.MaxPages);
    }

    [Fact]
    public void Read_SingleOutOfRangeResolution2000_IsFilteredOut()
    {
        var snapshot = new WiaPropertySnapshot(new Dictionary<int, WiaPropertyInfo>
        {
            [WiaScanRequest.XResolutionProperty] = WiaPropertyInfo.Single(2000),
        });

        var capabilities = WiaCapabilityReader.Read(snapshot);

        Assert.False(capabilities.IsReported);
        Assert.Equal([300], capabilities.Resolutions);
        Assert.DoesNotContain(2000, capabilities.Resolutions);
    }
}
