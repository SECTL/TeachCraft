using Homework.Core;
using SkiaSharp;

namespace Homework.Core.Recognition;

/// <summary>
/// 一页填涂检测的结果。Digits 为逐列读到的行号（0–9），Confidences 为对应列的墨迹占比。
/// <para>FailureReason 非空表示整页失败（给出第一个失败列）；此时 Digits 只填到失败列之前。</para>
/// </summary>
public sealed record FillInDetectionResult(int[] Digits, double[] Confidences, string? FailureReason)
{
    public bool Succeeded => FailureReason is null;
}

/// <summary>
/// 确定性的填涂检测器：按列切格、格内缩、灰度墨迹占比、阈值 + 领先余量。
/// <para>不做纠偏、不读格线 / 文字、不把空列当 0；全部在本机完成。</para>
/// </summary>
public static class FillInDetector
{
    /// <summary>
    /// 检测 region 内的 columns×rows 数字格。region 先夹取再按 RegionInsetRatio 内缩，
    /// 每格再按 CellInsetRatio 内缩以排除格线与行标；灰度亮度低于 InkLuminanceCutoff 计为墨迹。
    /// </summary>
    public static FillInDetectionResult Detect(
        SKBitmap bitmap, NormalizedRect region, int columns, int rows = FillInOptions.Rows)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (columns < FillInOptions.MinColumns || columns > FillInOptions.MaxColumns)
            throw new HomeworkException($"填涂列数须在 {FillInOptions.MinColumns}–{FillInOptions.MaxColumns} 之间。");
        if (rows < 1)
            throw new HomeworkException("填涂行数无效。");

        var clamped = region.Clamp();
        if (!clamped.IsUsable)
            throw new HomeworkException("识别区域太小，请重新框选。");

        var pixel = clamped.ToPixelRect(bitmap.Width, bitmap.Height);
        var insetX = (int)Math.Round(pixel.Width * FillInOptions.RegionInsetRatio);
        var insetY = (int)Math.Round(pixel.Height * FillInOptions.RegionInsetRatio);
        var inner = new SKRectI(
            pixel.Left + insetX, pixel.Top + insetY,
            pixel.Right - insetX, pixel.Bottom - insetY);
        if (inner.Width < 1 || inner.Height < 1)
            throw new HomeworkException("识别区域太小，请重新框选。");

        var cellWidth = inner.Width / (double)columns;
        var cellHeight = inner.Height / (double)rows;
        var digits = new int[columns];
        var confidences = new double[columns];
        string? failure = null;

        for (var column = 0; column < columns && failure is null; column++)
        {
            var ratios = new double[rows];
            for (var row = 0; row < rows; row++)
            {
                ratios[row] = InkRatio(bitmap, inner, column, row, cellWidth, cellHeight);
            }

            var winnerRow = 0;
            var winner = ratios[0];
            for (var row = 1; row < rows; row++)
            {
                if (ratios[row] > winner)
                {
                    winner = ratios[row];
                    winnerRow = row;
                }
            }

            var runnerUp = 0d;
            for (var row = 0; row < rows; row++)
            {
                if (row != winnerRow && ratios[row] > runnerUp)
                    runnerUp = ratios[row];
            }

            if (winner < FillInOptions.MinInkRatio)
            {
                failure = $"第 {column + 1} 列没有涂黑";
                break;
            }

            if (runnerUp >= winner - FillInOptions.MinLeadRatio)
            {
                failure = $"第 {column + 1} 列涂了多格";
                break;
            }

            digits[column] = winnerRow;
            confidences[column] = winner;
        }

        return new FillInDetectionResult(digits, confidences, failure);
    }

    /// <summary>单格内缩后的灰度墨迹占比；越界像素不计入分母。</summary>
    private static double InkRatio(
        SKBitmap bitmap, SKRectI inner, int column, int row, double cellWidth, double cellHeight)
    {
        var left = inner.Left + column * cellWidth;
        var top = inner.Top + row * cellHeight;
        var insetX = cellWidth * FillInOptions.CellInsetRatio;
        var insetY = cellHeight * FillInOptions.CellInsetRatio;
        var x0 = (int)Math.Floor(left + insetX);
        var x1 = (int)Math.Ceiling(left + cellWidth - insetX);
        var y0 = (int)Math.Floor(top + insetY);
        var y1 = (int)Math.Ceiling(top + cellHeight - insetY);

        var ink = 0;
        var total = 0;
        for (var y = y0; y < y1; y++)
        {
            if (y < 0 || y >= bitmap.Height) continue;
            for (var x = x0; x < x1; x++)
            {
                if (x < 0 || x >= bitmap.Width) continue;
                total++;
                var color = bitmap.GetPixel(x, y);
                var luminance = 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;
                if (luminance < FillInOptions.InkLuminanceCutoff)
                    ink++;
            }
        }

        return total == 0 ? 0d : ink / (double)total;
    }
}
