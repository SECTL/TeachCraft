using Avalonia;
using Homework.App;
using Homework.Core.Recognition;

namespace TeachCraft.Tests;

/// <summary>
/// 归一化矩形选区的纯几何测试。全部是数值断言，不依赖 UI 线程、不读截图；
/// 反向拖拽、越界、移动夹取、八手柄缩放、最小尺寸与归一化 round-trip 全覆盖。
/// </summary>
public sealed class RectSelectionMathTests
{
    private static readonly Size Surface = new(800, 600);

    // ----------------------------- FromDrag -----------------------------

    [Fact]
    public void FromDrag_NormalizesAndReverses()
    {
        var forward = RectSelectionMath.FromDrag(new Point(100, 100), new Point(300, 250), Surface);
        Assert.Equal(0.125, forward.X, 4);
        Assert.Equal(1.0 / 6, forward.Y, 4);
        Assert.Equal(0.25, forward.Width, 4);
        Assert.Equal(0.25, forward.Height, 4);

        var reverse = RectSelectionMath.FromDrag(new Point(300, 250), new Point(100, 100), Surface);
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void FromDrag_ClampsOutOfBounds()
    {
        var rect = RectSelectionMath.FromDrag(new Point(-100, -100), new Point(900, 700), Surface);
        Assert.Equal(0, rect.X, 4);
        Assert.Equal(0, rect.Y, 4);
        Assert.Equal(1, rect.Width, 4);
        Assert.Equal(1, rect.Height, 4);
    }

    [Fact]
    public void FromDrag_EnforcesMinimumSize()
    {
        var rect = RectSelectionMath.FromDrag(new Point(100, 100), new Point(101, 101), Surface);
        Assert.True(rect.Width >= RectSelectionMath.MinSize - 1e-9);
        Assert.True(rect.Height >= RectSelectionMath.MinSize - 1e-9);
    }

    // ----------------------------- Move -----------------------------

    [Fact]
    public void Move_ClampsToSurface()
    {
        var rect = new NormalizedRect(0.1, 0.1, 0.2, 0.2);

        var positive = RectSelectionMath.Move(rect, new Vector(1000, 1000), Surface);
        Assert.Equal(0.8, positive.X, 4); // 1 - 0.2
        Assert.Equal(0.8, positive.Y, 4); // 1 - 0.2
        Assert.Equal(rect.Width, positive.Width, 4);
        Assert.Equal(rect.Height, positive.Height, 4);

        var negative = RectSelectionMath.Move(rect, new Vector(-1000, -1000), Surface);
        Assert.Equal(0, negative.X, 4);
        Assert.Equal(0, negative.Y, 4);
        Assert.Equal(rect.Width, negative.Width, 4);
        Assert.Equal(rect.Height, negative.Height, 4);
    }

    // ----------------------------- Resize -----------------------------

    [Theory]
    [InlineData(RectHandle.TopLeft, 0.35, 0.35, 0.4, 0.4)]
    [InlineData(RectHandle.Top, 0.25, 0.35, 0.5, 0.4)]
    [InlineData(RectHandle.TopRight, 0.25, 0.35, 0.6, 0.4)]
    [InlineData(RectHandle.Right, 0.25, 0.25, 0.6, 0.5)]
    [InlineData(RectHandle.BottomRight, 0.25, 0.25, 0.6, 0.6)]
    [InlineData(RectHandle.Bottom, 0.25, 0.25, 0.5, 0.6)]
    [InlineData(RectHandle.BottomLeft, 0.35, 0.25, 0.4, 0.6)]
    [InlineData(RectHandle.Left, 0.35, 0.25, 0.4, 0.5)]
    public void Resize_MovesExpectedEdges(RectHandle handle, double x, double y, double w, double h)
    {
        var rect = new NormalizedRect(0.25, 0.25, 0.5, 0.5);
        var result = RectSelectionMath.Resize(rect, handle, new Vector(80, 60), Surface); // +0.1 / +0.1
        Assert.Equal(x, result.X, 4);
        Assert.Equal(y, result.Y, 4);
        Assert.Equal(w, result.Width, 4);
        Assert.Equal(h, result.Height, 4);
    }

    [Fact]
    public void Resize_EnforcesMinimumSize()
    {
        var rect = new NormalizedRect(0.4, 0.4, 0.2, 0.2); // 右边界 0.6
        // 把左边界拖过右边界：应保留右边界，左边界停在 0.6 - 0.02 = 0.58。
        var result = RectSelectionMath.Resize(rect, RectHandle.Left, new Vector(1000, 0), Surface);
        Assert.Equal(0.6 - RectSelectionMath.MinSize, result.X, 4);
        Assert.Equal(RectSelectionMath.MinSize, result.Width, 4);
        Assert.True(result.Width >= RectSelectionMath.MinSize - 1e-9);
    }

    [Fact]
    public void Resize_MoveHandleBehavesLikeMove()
    {
        var rect = new NormalizedRect(0.1, 0.1, 0.2, 0.2);
        var delta = new Vector(80, 60);
        Assert.Equal(RectSelectionMath.Move(rect, delta, Surface),
            RectSelectionMath.Resize(rect, RectHandle.Move, delta, Surface));
    }

    [Fact]
    public void Resize_NoneReturnsClampedRect()
    {
        var rect = new NormalizedRect(0.1, 0.1, 0.2, 0.2);
        Assert.Equal(RectSelectionMath.Clamp(rect),
            RectSelectionMath.Resize(rect, RectHandle.None, new Vector(80, 60), Surface));
    }

    // ----------------------------- Clamp / round-trip -----------------------------

    [Fact]
    public void Clamp_KeepsRectInsideSurface()
    {
        var clamped = RectSelectionMath.Clamp(new NormalizedRect(0.9, 0.9, 0.5, 0.5));
        Assert.Equal(1 - clamped.Width, clamped.X, 4);
        Assert.Equal(1 - clamped.Height, clamped.Y, 4);
        Assert.True(clamped.X + clamped.Width <= 1 + 1e-9);
        Assert.True(clamped.Y + clamped.Height <= 1 + 1e-9);
    }

    [Fact]
    public void NormalizedRoundTrip_ToPixelAndBack()
    {
        var rect = new NormalizedRect(0.125, 0.1667, 0.25, 0.25);
        var pixel = rect.ToPixelRect(800, 600);
        var back = RectSelectionMath.FromDrag(
            new Point(pixel.Left, pixel.Top),
            new Point(pixel.Right, pixel.Bottom),
            Surface);
        Assert.Equal(rect.X, back.X, 3);
        Assert.Equal(rect.Y, back.Y, 3);
        Assert.Equal(rect.Width, back.Width, 3);
        Assert.Equal(rect.Height, back.Height, 3);
    }
}
