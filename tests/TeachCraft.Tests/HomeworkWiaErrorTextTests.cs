using Homework.App.Scanning;

namespace TeachCraft.Tests;

public sealed class WiaErrorTextTests
{
    [Theory]
    [InlineData(0x80210002u, "卡纸")]
    [InlineData(0x80210003u, "没有纸")]
    [InlineData(0x80210020u, "多页同时进纸")]
    [InlineData(0x80210016u, "盖板未合")]
    [InlineData(0x80210006u, "正忙")]
    [InlineData(0x80210005u, "离线")]
    [InlineData(0xFFFFFFFFu, null)]
    public void DescribeHResult_MapsKnownCodes(uint code, string? expectedFragment)
    {
        var message = WiaErrorText.DescribeHResult(code);

        if (expectedFragment is null)
            Assert.Null(message);
        else
            Assert.Contains(expectedFragment, message);
    }

    [Fact]
    public void DescribeStatus_MapsJamAndCover()
    {
        var messages = WiaErrorText.DescribeStatus(
            WiaErrorText.StatusPaperJam | WiaErrorText.StatusFlatCoverUp | WiaErrorText.StatusFeedReady);

        Assert.Contains(messages, message => message.Contains("卡纸"));
        Assert.Contains(messages, message => message.Contains("盖板未合"));
        Assert.DoesNotContain(messages, message => message.Contains("多页"));
    }

    [Fact]
    public void IsBlocking_OnlyForErrorBits()
    {
        Assert.True(WiaErrorText.IsBlocking(WiaErrorText.StatusPaperJam));
        Assert.True(WiaErrorText.IsBlocking(WiaErrorText.StatusMultipleFeed));
        Assert.True(WiaErrorText.IsBlocking(WiaErrorText.StatusDeviceAttention));
        Assert.False(WiaErrorText.IsBlocking(WiaErrorText.StatusFeedReady | WiaErrorText.StatusFlatReady));
    }

    [Fact]
    public void IsUserCancelled_RecognisesWiaCancelCodes()
    {
        Assert.True(WiaErrorText.IsUserCancelled(0x80040200));
        Assert.True(WiaErrorText.IsUserCancelled(0x80040202));
        Assert.False(WiaErrorText.IsUserCancelled(0x80210002));
    }
}
