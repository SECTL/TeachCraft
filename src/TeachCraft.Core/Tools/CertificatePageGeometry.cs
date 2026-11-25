using SkiaSharp;

namespace TeachCraft.Core.Tools;

public sealed record CertificatePageMetrics(float PageWidth, float PageHeight, SKRect TemplateRect, bool IsLandscape);

public static class CertificatePageGeometry
{
    public const float A4Short = 595.28f;
    public const float A4Long = 841.89f;

    public static CertificatePageMetrics Measure(SKSize templateSize)
    {
        if (!float.IsFinite(templateSize.Width) || !float.IsFinite(templateSize.Height)
            || templateSize.Width <= 0 || templateSize.Height <= 0)
            throw new ToolboxException("奖状底图尺寸无效。");

        var isLandscape = templateSize.Width >= templateSize.Height;
        var pageWidth = isLandscape ? A4Long : A4Short;
        var pageHeight = isLandscape ? A4Short : A4Long;
        var scale = Math.Min(pageWidth / templateSize.Width, pageHeight / templateSize.Height);
        var width = templateSize.Width * scale;
        var height = templateSize.Height * scale;
        var rect = SKRect.Create((pageWidth - width) / 2, (pageHeight - height) / 2, width, height);
        return new CertificatePageMetrics(pageWidth, pageHeight, rect, isLandscape);
    }

    public static SKPoint Anchor(SKRect templateRect, double normalizedX, double normalizedY) =>
        new(templateRect.Left + templateRect.Width * (float)Math.Clamp(normalizedX, 0, 1),
            templateRect.Top + templateRect.Height * (float)Math.Clamp(normalizedY, 0, 1));

    public static float FitFontSize(SKRect templateRect, double fontScale, string text,
        SKFont font, float maxWidthRatio = 0.88f)
    {
        var size = templateRect.Height * (float)Math.Clamp(fontScale, 0.01, 0.40);
        font.Size = size;
        var measured = font.MeasureText(text);
        if (measured <= templateRect.Width * maxWidthRatio || measured <= 0) return size;
        return size * templateRect.Width * maxWidthRatio / measured;
    }
}
