using Homework.Core;
using SkiaSharp;

namespace Homework.Core.Recognition;

/// <summary>识别方法。本阶段只做填涂识别，不引入 OCR / 二维码。</summary>
public enum RecognitionMethod
{
    FillIn,
}

/// <summary>
/// 一页的识别结果。Digits 是原始数字串（可能带前导零），FailureReason 非空表示填涂读取失败。
/// <para>
/// MatchFailureReason 与 FailureReason 严格分工：前者表示「读到了数字，但无法唯一匹配」
/// （歧义 / 无匹配 / 该生已交齐），后者表示「填涂本身没读出来」。两者都让该页留在待处理。
/// StudentId 仍是匹配的唯一真源；本记录只描述「读到了什么」，不写匹配。
/// </para>
/// </summary>
public sealed record PageRecognition(
    RecognitionMethod Method,
    string Digits,
    double Confidence,
    string? FailureReason,
    DateTimeOffset RecognizedAt,
    string? MatchFailureReason = null)
{
    public bool Succeeded => FailureReason is null && Digits.Length > 0;
}

/// <summary>
/// 归一化矩形选区（0..1）。X/Y 为左上角，Width/Height 为宽高。
/// <para>坐标以图像左上角为原点，与图像同尺寸；界面、检测共用同一份。</para>
/// </summary>
public sealed record NormalizedRect(double X, double Y, double Width, double Height)
{
    /// <summary>最小可用宽 / 高，低于此值视为无效选区。</summary>
    public const double MinUsableSize = 0.02;

    /// <summary>非有限坐标直接拒绝；其余逐项夹到 0..1。</summary>
    public NormalizedRect Clamp()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) ||
            !double.IsFinite(Width) || !double.IsFinite(Height))
            throw new HomeworkException("识别区域坐标无效，请重新框选。");

        return new NormalizedRect(
            Math.Clamp(X, 0d, 1d),
            Math.Clamp(Y, 0d, 1d),
            Math.Clamp(Width, 0d, 1d),
            Math.Clamp(Height, 0d, 1d));
    }

    /// <summary>宽高都达到最小尺寸才算可用。</summary>
    public bool IsUsable => Width >= MinUsableSize && Height >= MinUsableSize;

    /// <summary>换算到像素矩形；先夹取再按宽高取整。</summary>
    public SKRectI ToPixelRect(int width, int height)
    {
        var clamped = Clamp();
        var x = (int)Math.Round(clamped.X * width);
        var y = (int)Math.Round(clamped.Y * height);
        var w = (int)Math.Round(clamped.Width * width);
        var h = (int)Math.Round(clamped.Height * height);
        return new SKRectI(x, y, x + w, y + h);
    }
}

/// <summary>填涂识别的阈值与版面常量，集中定义以便合成图测试锁定。</summary>
public static class FillInOptions
{
    public const int Rows = 10;
    public const double CellInsetRatio = 0.15;
    public const double RegionInsetRatio = 0.02;
    public const int InkLuminanceCutoff = 180;
    public const double MinInkRatio = 0.15;
    public const double MinLeadRatio = 0.10;
    public const int MinColumns = 1;
    public const int MaxColumns = 12;
    public const int RecognitionLongEdge = 2800;
    public const int PdfRecognitionDpi = 200;
}
