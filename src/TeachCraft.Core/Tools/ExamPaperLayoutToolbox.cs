using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp;

namespace TeachCraft.Core.Tools;

public enum ExamPaperLayoutMode
{
    TwoUp,
    Booklet,
}

public static class ExamPaperLayoutToolbox
{
    public static string Layout(string input, string output, ExamPaperLayoutMode mode, int dpi = 600)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ToolboxException("输出文件不能覆盖原始 PDF，请换一个文件名。");
        if (dpi is not (300 or 600 or 1200))
            throw new ToolboxException("试卷排版清晰度只能选择 300、600 或 1200 DPI。");

        try
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            if (source.PageCount == 0) throw new ToolboxException("这份 PDF 没有页面，无法排版。");
            var slots = ExamPaperLayoutPlanner.BuildSlots(source.PageCount, mode);
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output))!;
            var temporaryFiles = new List<string>();
            try
            {
                using var pdfStream = File.OpenRead(input);
                for (var index = 0; index < slots.Count; index += 2)
                {
                    var temporaryPath = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.side.pdf");
                    temporaryFiles.Add(temporaryPath);
                    CreateSide(input, source.Pages, pdfStream, slots[index], slots[index + 1], dpi, temporaryPath);
                }

                MergeSides(temporaryFiles, output);
                var outputSides = temporaryFiles.Count;
                var blankCount = slots.Count(slot => slot is null);
                var blankNote = blankCount == 0 ? string.Empty : $"，补 {blankCount} 页空白";
                var modeText = mode == ExamPaperLayoutMode.Booklet ? "小册子" : "2 合 1";
                return $"{source.PageCount} 页 → {modeText} {outputSides} 个版面{blankNote} · {dpi} DPI · {Path.GetFileName(output)}";
            }
            finally
            {
                foreach (var temporaryPath in temporaryFiles)
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex) { throw new ToolboxException($"试卷排版失败：{PdfToolbox.ExplainPdfError(ex)}"); }
    }

    private static void CreateSide(string input, PdfPages sourcePages, Stream stream,
        int? leftIndex, int? rightIndex, int dpi, string output)
    {
        using var target = new PdfDocument();
        var page = target.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = PageOrientation.Landscape;
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            var halfWidth = page.Width.Point / 2;
            DrawSlot(graphics, sourcePages, stream, leftIndex,
                new XRect(0, 0, halfWidth, page.Height.Point), dpi);
            DrawSlot(graphics, sourcePages, stream, rightIndex,
                new XRect(halfWidth, 0, halfWidth, page.Height.Point), dpi);
        }
        target.Save(output);
    }

    private static void DrawSlot(XGraphics graphics, PdfPages sourcePages, Stream stream,
        int? pageIndex, XRect slot, int dpi)
    {
        if (pageIndex is null) return;
        var page = sourcePages[pageIndex.Value];
        var rect = ExamPaperLayoutPlanner.VisiblePageRect(page, slot);
        var (width, height) = ExamPaperLayoutPlanner.RenderPixelSize(page, slot, dpi);
        using var bitmap = PDFtoImage.Conversion.ToImage(stream, pageIndex.Value, leaveOpen: true,
            password: null, options: new PDFtoImage.RenderOptions
            {
                Width = width,
                Height = height,
                UseTiling = true,
            });
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var image = XImage.FromStream(png.AsStream());
        graphics.DrawImage(image, rect);
    }

    private static void MergeSides(IReadOnlyList<string> sides, string output)
    {
        using var target = new PdfDocument();
        foreach (var path in sides)
        {
            using var side = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            target.AddPage(side.Pages[0]);
        }
        target.Save(output);
    }
}
