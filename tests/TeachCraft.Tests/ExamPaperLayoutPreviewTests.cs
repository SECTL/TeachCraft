using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class ExamPaperLayoutPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Render_UsesBoundedPreviewResolution_ForHighOutputDpi()
    {
        Directory.CreateDirectory(_root);
        var input = MakePdf("exam.pdf", 5);

        var preview = ExamPaperLayoutPreviewRenderer.Render(input, ExamPaperLayoutMode.Booklet, 1200);

        using var bitmap = SKBitmap.Decode(preview.Png);
        Assert.NotNull(bitmap);
        Assert.Equal((1404, 993), (bitmap!.Width, bitmap.Height));
        Assert.Contains(Enumerable.Range(0, bitmap.Width * bitmap.Height)
            .Select(index => bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width)),
            color => color != SKColors.White);
        Assert.Contains("小册子首面预览", preview.Caption);
        Assert.Contains("输出 1200 DPI（预览 120 DPI）", preview.Caption);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string MakePdf(string name, int pageCount)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        using var document = new PdfDocument();
        for (var index = 0; index < pageCount; index++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XPens.Black, 20 + index, 20, 120, 180);
        }
        document.Save(path);
        return path;
    }
}
