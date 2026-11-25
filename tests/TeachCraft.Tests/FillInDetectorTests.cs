using Homework.Core;
using Homework.Core.Recognition;
using SkiaSharp;

namespace TeachCraft.Tests;

/// <summary>
/// 填涂检测器：全部使用 SkiaSharp 合成图（白底、细格线、已知涂格），经 PNG 编解码后检测。
/// 不依赖真实扫描件，阈值与余量由 FillInOptions 锁定。
/// </summary>
public sealed class FillInDetectorTests
{
    private const int Width = 400;
    private const int Height = 800;

    private static NormalizedRect Full => new(0, 0, 1, 1);

    /// <summary>
    /// 合成一张填涂图。涂格按「格内缩 15% 后的采样区」的 sqrt(fill) 边长居中绘制，
    /// 使检测器读到的墨迹占比约等于 fill；格线画在格边界上，被 15% 内缩排除。
    /// </summary>
    private static SKBitmap BuildImage(
        IEnumerable<(int Col, int Row)> marks,
        int columns,
        double fill = 0.40,
        int gray = 0,
        bool gridLines = true,
        NormalizedRect? region = null,
        int rows = FillInOptions.Rows,
        int width = Width,
        int height = Height)
    {
        region ??= Full;
        var pixel = region.Clamp().ToPixelRect(width, height);
        var insetX = (int)Math.Round(pixel.Width * FillInOptions.RegionInsetRatio);
        var insetY = (int)Math.Round(pixel.Height * FillInOptions.RegionInsetRatio);
        var inner = new SKRectI(
            pixel.Left + insetX, pixel.Top + insetY,
            pixel.Right - insetX, pixel.Bottom - insetY);
        var cellW = inner.Width / (double)columns;
        var cellH = inner.Height / (double)rows;

        var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);

            if (gridLines)
            {
                using var line = new SKPaint { Color = SKColors.Black, StrokeWidth = 1 };
                for (var c = 0; c <= columns; c++)
                {
                    var x = (float)(inner.Left + c * cellW);
                    canvas.DrawLine(x, inner.Top, x, inner.Bottom, line);
                }
                for (var r = 0; r <= rows; r++)
                {
                    var y = (float)(inner.Top + r * cellH);
                    canvas.DrawLine(inner.Left, y, inner.Right, y, line);
                }
            }

            using var ink = new SKPaint
            {
                Color = new SKColor((byte)gray, (byte)gray, (byte)gray),
                Style = SKPaintStyle.Fill,
            };
            var sampleW = cellW * (1 - 2 * FillInOptions.CellInsetRatio);
            var sampleH = cellH * (1 - 2 * FillInOptions.CellInsetRatio);
            var sideW = Math.Sqrt(fill) * sampleW;
            var sideH = Math.Sqrt(fill) * sampleH;
            foreach (var (col, row) in marks)
            {
                var cx = inner.Left + (col + 0.5) * cellW;
                var cy = inner.Top + (row + 0.5) * cellH;
                canvas.DrawRect(
                    (float)(cx - sideW / 2), (float)(cy - sideH / 2),
                    (float)sideW, (float)sideH, ink);
            }
        }
        return bitmap;
    }

    /// <summary>编码成 PNG 再解码，走一遍真实解码路径。</summary>
    private static SKBitmap Decode(SKBitmap source)
    {
        using var stream = new MemoryStream();
        Assert.True(source.Encode(stream, SKEncodedImageFormat.Png, 100));
        stream.Position = 0;
        return SKBitmap.Decode(stream) ?? throw new InvalidOperationException("合成图解码失败。");
    }

    private static FillInDetectionResult Run(
        SKBitmap bitmap, int columns, NormalizedRect? region = null) =>
        FillInDetector.Detect(bitmap, region ?? Full, columns);

    // ----------------------------- happy -----------------------------

    [Fact]
    public void Detect_FourColumns_Reads0123()
    {
        using var source = BuildImage(new[] { (0, 0), (1, 1), (2, 2), (3, 3) }, columns: 4);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 4);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Null(result.FailureReason);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Digits);
        Assert.Equal(4, result.Confidences.Length);
        Assert.All(result.Confidences, c => Assert.True(c > FillInOptions.MinInkRatio && c <= 1d));
    }

    [Fact]
    public void Detect_ThreeColumns_Reads001()
    {
        using var source = BuildImage(new[] { (0, 0), (1, 0), (2, 1) }, columns: 3);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 3);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(new[] { 0, 0, 1 }, result.Digits);
    }

    [Theory]
    [InlineData(0.30)]
    [InlineData(0.50)]
    [InlineData(0.80)]
    public void Detect_FillLevels_AllReadWithConfidenceTrackingInk(double fill)
    {
        using var source = BuildImage(new[] { (0, 7) }, columns: 1, fill: fill);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 1);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(new[] { 7 }, result.Digits);
        Assert.InRange(result.Confidences[0], fill - 0.08, fill + 0.08);
    }

    [Fact]
    public void Detect_PencilGray140_Reads()
    {
        using var source = BuildImage(new[] { (0, 4) }, columns: 1, gray: 140);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 1);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(new[] { 4 }, result.Digits);
    }

    [Fact]
    public void Detect_GridLinesExcludedByInset()
    {
        // 格线为纯黑（亮度 0，若被采样会算墨迹），但落在格边界，被 15% 内缩排除。
        using var source = BuildImage(new[] { (0, 3), (1, 6) }, columns: 2, gridLines: true);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 2);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(new[] { 3, 6 }, result.Digits);
    }

    [Fact]
    public void Detect_RegionClamp_Reads()
    {
        // 图按 (0,0,1,1) 画格；传入越界区域，夹取后应回到同一格区。
        using var source = BuildImage(new[] { (0, 0), (1, 1), (2, 2), (3, 3) }, columns: 4);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 4, new NormalizedRect(-0.2, -0.2, 1.4, 1.4));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Digits);
    }

    [Fact]
    public void Detect_SubRegion_Reads()
    {
        var region = new NormalizedRect(0.25, 0.25, 0.5, 0.5);
        using var source = BuildImage(new[] { (0, 2), (1, 8) }, columns: 2, region: region);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 2, region);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(new[] { 2, 8 }, result.Digits);
    }

    // ----------------------------- failure -----------------------------

    [Fact]
    public void Detect_EmptyColumn_FailsNamingFirstFailingColumn()
    {
        using var source = BuildImage(new[] { (0, 0), (1, 1), (2, 2) }, columns: 4);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 4);

        Assert.False(result.Succeeded);
        Assert.Equal("第 4 列没有涂黑", result.FailureReason);
        Assert.Equal(new[] { 0, 1, 2, 0 }, result.Digits); // 失败列之前的数字保留
    }

    [Fact]
    public void Detect_DoubleMark_FailsNamingFirstFailingColumn()
    {
        using var source = BuildImage(new[] { (0, 1), (1, 2), (1, 5) }, columns: 2);
        using var bitmap = Decode(source);

        var result = Run(bitmap, 2);

        Assert.False(result.Succeeded);
        Assert.Equal("第 2 列涂了多格", result.FailureReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Detect_InvalidColumns_ThrowsHomeworkException(int columns)
    {
        using var source = BuildImage(new[] { (0, 0) }, columns: 1);
        using var bitmap = Decode(source);

        Assert.Throws<HomeworkException>(() => Run(bitmap, columns));
    }
}
