using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace SeatGrid.Core.Exam;

/// <summary>
/// 桌贴绘制：A4 纵向一页排 3×4 = 12 张，每张印姓名、座号、考号、班级与考场，
/// 四周一圈虚线裁切线。文字用 SkiaSharp 画进高分辨率 PNG，再由 QuestPDF 铺进 A4
/// ——与座位表 PDF 同一条管线，中文字体不内嵌。
///
/// 不做条码：Code128 的收益是扫码核验，而老师实际用肉眼核对；把考号放大到 40px
/// 更实用。若以后要条码，在这里加一个 DrawBarcode 分支即可，不影响结构。
/// </summary>
public static class DeskStickerRenderer
{
    public const float PageWidth = 1000f;
    public const float PageHeight = 1414f;      // A4 纵向比例
    private const float PagePad = 42f;

    private static readonly Lazy<SKTypeface> RegularTypeface =
        new(() => SKFontManager.Default.MatchCharacter('李') ?? SKTypeface.Default);

    private static readonly Lazy<SKTypeface> BoldTypeface =
        new(() => SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, '李')
                   ?? RegularTypeface.Value);

    private static readonly SKColor Ink = Hex("#141413");
    private static readonly SKColor InkSoft = Hex("#3D3B36");
    private static readonly SKColor Muted = Hex("#6F6A5E");
    private static readonly SKColor AccentDeep = Hex("#A34A36");
    private static readonly SKColor Line = Hex("#C9C1AE");
    private static readonly SKColor CardFill = Hex("#FFFFFF");
    private static readonly SKColor Paper = Hex("#FFFFFF");

    /// <summary>总页数。</summary>
    public static int PageCount(int stickerCount, DeskStickerOptions options)
    {
        if (stickerCount <= 0) return 1;
        return (stickerCount + options.PerPage - 1) / options.PerPage;
    }

    /// <summary>画某一页。pageIndex 从 0 开始。</summary>
    public static byte[] Render(
        DeskStickerDocument document, DeskStickerOptions options, int pageIndex, float scale = 1.8f)
    {
        var pixelWidth = (int)Math.Ceiling(PageWidth * scale);
        var pixelHeight = (int)Math.Ceiling(PageHeight * scale);

        using var surface = SKSurface.Create(
            new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            throw new SeatGridException("桌贴画布创建失败，请重试。");

        var canvas = surface.Canvas;
        canvas.Clear(Paper);
        canvas.Scale(scale);

        var columns = Math.Max(1, options.Columns);
        var rows = Math.Max(1, options.Rows);
        var cellWidth = (PageWidth - PagePad * 2) / columns;
        var cellHeight = (PageHeight - PagePad * 2) / rows;

        var start = pageIndex * options.PerPage;
        for (var i = 0; i < options.PerPage; i++)
        {
            var index = start + i;
            if (index >= document.Stickers.Count) break;

            var column = i % columns;
            var row = i / columns;
            var rect = new SKRect(
                PagePad + column * cellWidth,
                PagePad + row * cellHeight,
                PagePad + (column + 1) * cellWidth,
                PagePad + (row + 1) * cellHeight);

            DrawSticker(canvas, document.Stickers[index], options, rect);
        }

        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawSticker(
        SKCanvas canvas, DeskSticker sticker, DeskStickerOptions options, SKRect cell)
    {
        // 卡片本身：白底 + 1px 细边，四周留 10px 间距。
        var card = new SKRect(cell.Left + 10, cell.Top + 10, cell.Right - 10, cell.Bottom - 10);

        using var fill = new SKPaint { Color = CardFill, IsAntialias = true };
        using var border = new SKPaint
        {
            Color = Line, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
        };
        canvas.DrawRect(card, fill);
        canvas.DrawRect(card, border);

        // 裁切线：卡片外一圈虚线，沿四边。
        using var cutPaint = new SKPaint
        {
            Color = Line, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0f),
        };
        canvas.DrawRect(new SKRect(cell.Left + 3, cell.Top + 3, cell.Right - 3, cell.Bottom - 3), cutPaint);

        var innerLeft = card.Left + 14f;
        var innerRight = card.Right - 14f;

        // 左上：姓名
        using var nameFont = new SKFont(BoldTypeface.Value, 20f);
        using var namePaint = new SKPaint { Color = Ink, IsAntialias = true };
        canvas.DrawText(sticker.Name, innerLeft, card.Top + 34f, SKTextAlign.Left, nameFont, namePaint);

        // 右上：座号
        using var seatFont = new SKFont(BoldTypeface.Value, 24f);
        using var seatPaint = new SKPaint { Color = AccentDeep, IsAntialias = true };
        canvas.DrawText($"{sticker.SeatNumber:00} 号", innerRight, card.Top + 36f, SKTextAlign.Right,
            seatFont, seatPaint);

        // 中部：考号
        if (options.ShowNumber && sticker.Number.Length > 0)
        {
            using var numberFont = new SKFont(RegularTypeface.Value, 14f);
            using var numberPaint = new SKPaint { Color = InkSoft, IsAntialias = true };
            canvas.DrawText(sticker.Number, innerLeft, card.Top + 70f, SKTextAlign.Left, numberFont, numberPaint);
        }

        // 底部：班级 · 考场
        var footer = string.Join("　·　",
            new[] { sticker.ClassName, sticker.ExamRoom }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (footer.Length > 0)
        {
            using var footerFont = new SKFont(RegularTypeface.Value, 10f);
            using var footerPaint = new SKPaint { Color = Muted, IsAntialias = true };
            canvas.DrawText(footer, innerLeft, card.Bottom - 14f, SKTextAlign.Left, footerFont, footerPaint);
        }
    }

    private static SKColor Hex(string hex)
    {
        var normalized = Models.SeatChartSettings.NormalizeColor(hex, "#000000");
        var value = uint.Parse(normalized[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }
}
