using Homework.Core;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace Homework.Core.Recognition;

/// <summary>
/// 智学网式学号填涂模板的选项。版面以 300 DPI 的 A4 纵向为基准（2480×3508 像素），
/// 其余 DPI 按比例缩放，保证默认参数下几何值与契约逐像素一致。
/// </summary>
public sealed record FillInTemplateOptions(
    string Title,
    string? ClassName,
    int Columns,
    int Rows = FillInOptions.Rows,
    int Dpi = 300);

/// <summary>
/// 生成可打印的学号填涂模板：PNG 由 SkiaSharp 直接绘制，PDF 由 QuestPDF 把同一张 PNG
/// 铺进 A4 页面（与桌贴 / 座位表 / 课表一致的「高分辨率 PNG 嵌入 A4」管线，
/// 中文由 SkiaSharp 画进位图，不交给 PdfSharp，也不让 QuestPDF 内嵌整套中文字体）。
/// </summary>
public static class FillInTemplateRenderer
{
    /// <summary>CJK 字体回退顺序，与计划一致。</summary>
    public static readonly IReadOnlyList<string> CjkFamilies =
    [
        "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC", "SimHei",
    ];

    /// <summary>位值列标，从个位到千亿；列数不足时取右侧 N 个（个位永远在最右）。</summary>
    private static readonly string[] PlaceValues =
    [
        "个", "十", "百", "千", "万", "十万", "百万", "千万", "亿", "十亿", "百亿", "千亿",
    ];

    public static byte[] RenderPng(FillInTemplateOptions options) =>
        RenderPng(options, ResolveCjkTypeface);

    /// <summary>
    /// 可注入字体探测的重载，供测试覆盖「找不到中文字体」的失败路径。
    /// 返回的 <see cref="SKTypeface"/> 由本方法负责释放。
    /// </summary>
    public static byte[] RenderPng(
        FillInTemplateOptions options, Func<IReadOnlyList<string>, SKTypeface?> resolveTypeface)
    {
        var layout = Measure(options);
        var typeface = resolveTypeface(CjkFamilies)
            ?? throw new HomeworkException("找不到中文字体，无法生成填涂模板。请安装 Microsoft YaHei / PingFang SC / Noto Sans CJK SC 等中文字体后重试。");

        using (typeface)
        using (var bitmap = new SKBitmap(layout.PageWidth, layout.PageHeight, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var canvas = new SKCanvas(bitmap))
        {
            Draw(canvas, layout, options, typeface);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
    }

    public static byte[] RenderPdf(FillInTemplateOptions options) =>
        RenderPdf(options, ResolveCjkTypeface);

    /// <summary>可注入字体探测的 PDF 重载；与 PNG 共用同一张位图，保证两种格式版面一致。</summary>
    public static byte[] RenderPdf(
        FillInTemplateOptions options, Func<IReadOnlyList<string>, SKTypeface?> resolveTypeface)
    {
        var png = RenderPng(options, resolveTypeface);
        QuestPDF.Settings.License = LicenseType.Community;
        using var stream = new MemoryStream();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.PageColor(Colors.White);
                page.Content().Image(png).FitArea();
            });
        }).GeneratePdf(stream);
        return stream.ToArray();
    }

    private static SKTypeface? ResolveCjkTypeface(IReadOnlyList<string> families)
    {
        foreach (var name in families)
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face is not null) return face;
        }
        return null;
    }

    private static Layout Measure(FillInTemplateOptions options)
    {
        if (options.Columns < FillInOptions.MinColumns || options.Columns > FillInOptions.MaxColumns)
            throw new HomeworkException($"填涂模板列数必须在 {FillInOptions.MinColumns} 到 {FillInOptions.MaxColumns} 之间，当前为 {options.Columns}。");
        if (options.Rows < 1)
            throw new HomeworkException($"填涂模板行数必须大于 0，当前为 {options.Rows}。");
        if (options.Dpi <= 0)
            throw new HomeworkException($"填涂模板 DPI 必须大于 0，当前为 {options.Dpi}。");

        var scale = options.Dpi / 300d;
        var pageWidth = (int)Math.Round(210d / 25.4 * options.Dpi);
        var pageHeight = (int)Math.Round(297d / 25.4 * options.Dpi);
        var margin = (int)Math.Round(180 * scale);
        var gridTop = (int)Math.Round(760 * scale);
        var cellHeight = (int)Math.Round(120 * scale);
        var rowLabelWidth = (int)Math.Round(120 * scale);
        var maxCellWidth = (int)Math.Round(260 * scale);
        var available = pageWidth - 2 * margin - rowLabelWidth;
        var cellWidth = Math.Min(maxCellWidth, available / options.Columns);
        var cellsWidth = cellWidth * options.Columns;
        var gridWidth = rowLabelWidth + cellsWidth;
        var gridLeft = (pageWidth - gridWidth) / 2;
        var cellsLeft = gridLeft + rowLabelWidth;

        return new Layout(pageWidth, pageHeight, margin, gridTop, cellHeight, rowLabelWidth,
            cellWidth, cellsWidth, gridLeft, cellsLeft, options.Columns, options.Rows, scale);
    }

    private static void Draw(SKCanvas canvas, Layout l, FillInTemplateOptions options, SKTypeface typeface)
    {
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = new SKColor(20, 20, 19), IsAntialias = true };
        using var gridLine = new SKPaint
        {
            Color = new SKColor(198, 198, 198), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2f * (float)l.Scale,
        };
        using var border = new SKPaint
        {
            Color = new SKColor(20, 20, 19), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 6f * (float)l.Scale,
        };
        using var font = new SKFont(typeface) { Edging = SKFontEdging.Antialias };

        // 页眉：标题 64px、班级/姓名/学号行 36px、说明 28px（均按 DPI 缩放）。
        font.Size = 64f * (float)l.Scale;
        DrawCentered(canvas, options.Title, l.PageWidth / 2f, l.Margin + 70f * (float)l.Scale, font, ink);

        font.Size = 36f * (float)l.Scale;
        var classText = string.IsNullOrWhiteSpace(options.ClassName)
            ? "班级：________"
            : $"班级：{options.ClassName}";
        DrawCentered(canvas, $"{classText}    姓名：________    学号：________",
            l.PageWidth / 2f, l.Margin + 160f * (float)l.Scale, font, ink);

        font.Size = 28f * (float)l.Scale;
        DrawCentered(canvas, "请在下方数字格内用 2B 铅笔涂出学号，每列只涂一格。",
            l.PageWidth / 2f, l.Margin + 230f * (float)l.Scale, font, ink);

        // 上方位值列标：个位在最右，列数不足时取右侧 N 个。
        font.Size = 36f * (float)l.Scale;
        for (var c = 0; c < l.Columns; c++)
        {
            var label = PlaceValues[l.Columns - 1 - c];
            var cx = l.CellsLeft + (c + 0.5f) * l.CellWidth;
            DrawCenteredInBox(canvas, label, cx, l.GridTop - 34f * (float)l.Scale, font, ink);
        }

        // 左侧 0–9 行标。
        font.Size = 32f * (float)l.Scale;
        for (var r = 0; r < l.Rows; r++)
        {
            var cy = l.GridTop + (r + 0.5f) * l.CellHeight;
            DrawCenteredInBox(canvas, r.ToString(), l.GridLeft + l.RowLabelWidth / 2f, cy, font, ink);
        }

        // 数字格：浅色分隔线 + 外侧实线边框（边框最后画，覆盖分隔线的外沿）。
        for (var c = 1; c < l.Columns; c++)
        {
            var x = l.CellsLeft + c * l.CellWidth;
            canvas.DrawLine(x, l.GridTop, x, l.GridBottom, gridLine);
        }
        for (var r = 1; r < l.Rows; r++)
        {
            var y = l.GridTop + r * l.CellHeight;
            canvas.DrawLine(l.CellsLeft, y, l.CellsRight, y, gridLine);
        }
        canvas.DrawRect(l.CellsLeft, l.GridTop, l.CellsWidth, l.Rows * l.CellHeight, border);

        // 底部说明：N 为列数（学号位数）。
        font.Size = 28f * (float)l.Scale;
        DrawCentered(canvas,
            $"每列只涂一格；学号不足 {l.Columns} 位时左侧补 0；请用 2B 铅笔涂满方格",
            l.PageWidth / 2f, l.GridBottom + 70f * (float)l.Scale, font, ink);
    }

    private static void DrawCentered(SKCanvas canvas, string text, float centerX, float baselineY,
        SKFont font, SKPaint paint) =>
        canvas.DrawText(text, centerX, baselineY, SKTextAlign.Center, font, paint);

    private static void DrawCenteredInBox(SKCanvas canvas, string text, float centerX, float centerY,
        SKFont font, SKPaint paint)
    {
        var metrics = font.Metrics;
        var baseline = centerY - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(text, centerX, baseline, SKTextAlign.Center, font, paint);
    }

    private sealed record Layout(
        int PageWidth, int PageHeight, int Margin, int GridTop, int CellHeight, int RowLabelWidth,
        int CellWidth, int CellsWidth, int GridLeft, int CellsLeft, int Columns, int Rows, double Scale)
    {
        public int CellsRight => CellsLeft + CellsWidth;
        public int GridBottom => GridTop + Rows * CellHeight;
    }
}
