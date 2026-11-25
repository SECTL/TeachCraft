using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace TeachCraft.Core.Tools;

public sealed record ExamPaperLayoutPreview(byte[] Png, string Caption, string Detail);

public static class ExamPaperLayoutPreviewRenderer
{
    public const int PreviewDpi = 120;

    public static ExamPaperLayoutPreview Render(string input, ExamPaperLayoutMode mode, int outputDpi)
    {
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        if (document.PageCount == 0) throw new ToolboxException("这份 PDF 没有页面，无法预览。");
        var slots = ExamPaperLayoutPlanner.BuildSlots(document.PageCount, mode);
        var sheetSize = new XSize(841.89, 595.28);
        var width = (int)Math.Ceiling(sheetSize.Width / 72 * PreviewDpi);
        var height = (int)Math.Ceiling(sheetSize.Height / 72 * PreviewDpi);
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var stream = File.OpenRead(input);
        DrawSlot(canvas, stream, document, slots[0], sheetSize, 0);
        DrawSlot(canvas, stream, document, slots[1], sheetSize, 1);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var first = slots[0] is { } left ? $"第 {left + 1} 页" : "空白页";
        var second = slots[1] is { } right ? $"第 {right + 1} 页" : "空白页";
        return new ExamPaperLayoutPreview(encoded.ToArray(),
            $"{(mode == ExamPaperLayoutMode.Booklet ? "小册子首面预览" : "2 合 1 首页预览")} · 输出 {outputDpi} DPI（预览 {PreviewDpi} DPI）",
            $"横向 A4 · 左：{first} · 右：{second} · 预览渲染为 {PreviewDpi} DPI");
    }

    private static void DrawSlot(SKCanvas canvas, Stream stream, PdfSharp.Pdf.PdfDocument document,
        int? pageIndex, XSize sheet, int slotIndex)
    {
        if (pageIndex is null) return;
        var slot = new XRect(slotIndex * sheet.Width / 2, 0, sheet.Width / 2, sheet.Height);
        var page = document.Pages[pageIndex.Value];
        var rect = ExamPaperLayoutPlanner.VisiblePageRect(page, slot);
        var (width, height) = ExamPaperLayoutPlanner.RenderPixelSize(page, slot, PreviewDpi);
        using var bitmap = PDFtoImage.Conversion.ToImage(stream, pageIndex.Value, leaveOpen: true, password: null,
            options: new PDFtoImage.RenderOptions { Width = width, Height = height, UseTiling = true });
        var canvasBounds = canvas.LocalClipBounds;
        var destination = new SKRect(
            (float)(rect.X / sheet.Width * canvasBounds.Width),
            (float)(rect.Y / sheet.Height * canvasBounds.Height),
            (float)(rect.Right / sheet.Width * canvasBounds.Width),
            (float)(rect.Bottom / sheet.Height * canvasBounds.Height));
        canvas.DrawBitmap(bitmap, SKRect.Create(0, 0, bitmap.Width, bitmap.Height), destination);
    }
}
