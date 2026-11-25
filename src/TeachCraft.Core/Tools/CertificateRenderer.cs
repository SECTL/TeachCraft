using SkiaSharp;

namespace TeachCraft.Core.Tools;

public static class CertificateRenderer
{
    public const int PreviewWidth = 1200;

    public static CertificatePreviewPage RenderPreview(
        string templatePath, CertificateRecord record, CertificateTextPlacement placement, int previewWidth = PreviewWidth)
    {
        placement = placement.Clamp();
        using var template = ImageToolbox.Load(templatePath);
        var metrics = CertificatePageGeometry.Measure(new SKSize(template.Width, template.Height));
        var scale = Math.Max(1, previewWidth) / metrics.PageWidth;
        var width = Math.Max(1, (int)Math.Round(metrics.PageWidth * scale));
        var height = Math.Max(1, (int)Math.Round(metrics.PageHeight * scale));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        using (var image = SKImage.FromBitmap(template))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            DrawCertificate(canvas, image, metrics, record, placement);
        }
        using var snapshot = SKImage.FromBitmap(bitmap);
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 95);
        return new CertificatePreviewPage(encoded.ToArray(), width, height,
            $"A4 {(metrics.IsLandscape ? "横向" : "纵向")} · 底图等比铺放 · 预览 {width}×{height}");
    }

    public static string RenderPdf(CertificateRenderRequest request, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Records.Count == 0) throw new ToolboxException("名单中没有可生成的奖状记录。");
        if (!File.Exists(request.TemplatePath)) throw new ToolboxException("找不到奖状底图，请重新选择。");
        if (!File.Exists(request.RosterPath)) throw new ToolboxException("找不到获奖名单工作簿，请重新选择。");
        if (SourcesOverlap(request.OutputPath, request.TemplatePath, request.RosterPath))
            throw new ToolboxException("输出文件不能覆盖奖状底图或名单工作簿，请换一个文件名。");

        cancellationToken.ThrowIfCancellationRequested();
        var placement = request.Placement.Clamp();
        using var template = ImageToolbox.Load(request.TemplatePath);
        var metrics = CertificatePageGeometry.Measure(new SKSize(template.Width, template.Height));
        using var templateImage = SKImage.FromBitmap(template);
        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory ?? Path.GetTempPath(), $".{Guid.NewGuid():N}.certificate.tmp.pdf");
        var ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                ownsTemporary = true;
                using var document = SKDocument.CreatePdf(stream);
                for (var index = 0; index < request.Records.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var canvas = document.BeginPage(metrics.PageWidth, metrics.PageHeight);
                    canvas.Clear(SKColors.White);
                    DrawCertificate(canvas, templateImage, metrics, request.Records[index], placement);
                    document.EndPage();
                    progress?.Report((index + 1) * 100 / request.Records.Count);
                }
                document.Close();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, request.OutputPath, overwrite: false);
            ownsTemporary = false;
            return $"已生成 {request.Records.Count} 张奖状 · {Path.GetFileName(request.OutputPath)}";
        }
        finally
        {
            if (ownsTemporary) File.Delete(temporary);
        }
    }

    internal static void DrawCertificate(SKCanvas canvas, SKImage template, CertificatePageMetrics metrics,
        CertificateRecord record, CertificateTextPlacement placement)
    {
        var source = new SKRect(0, 0, template.Width, template.Height);
        canvas.DrawImage(template, source, metrics.TemplateRect,
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        DrawField(canvas, metrics.TemplateRect, record.Name, placement.NameX, placement.NameY,
            placement.NameFontScale, SKFontStyle.Bold);
        DrawField(canvas, metrics.TemplateRect, record.Award, placement.AwardX, placement.AwardY,
            placement.AwardFontScale, SKFontStyle.Normal);
    }

    private static void DrawField(SKCanvas canvas, SKRect templateRect, string text, double x, double y,
        double scale, SKFontStyle style)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var typeface = ImageToolbox.ResolveCjkTypeface(style);
        using var font = new SKFont(typeface);
        font.Size = CertificatePageGeometry.FitFontSize(templateRect, scale, text, font);
        font.Edging = SKFontEdging.Antialias;
        var anchor = CertificatePageGeometry.Anchor(templateRect, x, y);
        var metrics = font.Metrics;
        var baseline = anchor.Y - (metrics.Ascent + metrics.Descent) / 2f;
        using var paint = new SKPaint { Color = new SKColor(20, 20, 19), IsAntialias = true };
        canvas.DrawText(text, anchor.X, baseline, SKTextAlign.Center, font, paint);
    }

    private static bool SourcesOverlap(string output, string templatePath, string rosterPath) =>
        string.Equals(Path.GetFullPath(output), Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFullPath(output), Path.GetFullPath(rosterPath), StringComparison.OrdinalIgnoreCase);
}
