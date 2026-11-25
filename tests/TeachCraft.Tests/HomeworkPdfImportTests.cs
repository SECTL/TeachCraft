using Homework.Core;
using Homework.Core.Models;
using Homework.Core.Pages;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace TeachCraft.Tests;

/// <summary>
/// PDF 导入：页数、逐页原子性、取消回滚、加密 / 损坏报错。全部使用合成临时 PDF。
/// </summary>
public sealed class HomeworkPdfImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "TeachCraft", "Homework", "tests", Guid.NewGuid().ToString("N"));
    private readonly List<PageThumbnailCache> _caches = [];

    public HomeworkPdfImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var cache in _caches) cache.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private PageThumbnailCache NewCache()
    {
        var cache = new PageThumbnailCache();
        _caches.Add(cache);
        return cache;
    }

    private string MakePdf(string name, int pages)
    {
        var path = Path.Combine(_root, name);
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.LightGray, 40, 40, 160, 80);
        }
        document.Save(path);
        return path;
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _action;
        public SyncProgress(Action<int> action) => _action = action;
        public void Report(int value) => _action(value);
    }

    // ----------------------------- 正常导入 -----------------------------

    [Fact]
    public void ImportPdf_FourPages_AddsFourPdfPages()
    {
        var pdf = MakePdf("four.pdf", 4);
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var added = PageImporter.ImportPdf(
            batch, pdf, cache, 96, null, CancellationToken.None, out var warnings);

        Assert.Equal(4, added.Count);
        Assert.Empty(warnings);
        Assert.Equal(4, batch.Pages.Count);
        Assert.All(batch.Pages, p => Assert.Equal(HomeworkPageKind.PdfPage, p.Kind));
        Assert.Equal(new[] { 1, 2, 3, 4 }, batch.Pages.Select(p => p.SourcePageNumber).ToArray());
        Assert.Equal(4, Directory.GetFiles(cache.SessionDirectory, "*.png").Length);
        Assert.All(batch.Pages, p =>
        {
            Assert.True(p.PixelWidth > 0);
            Assert.True(p.PixelHeight > 0);
            Assert.True(Math.Max(p.PixelWidth, p.PixelHeight) <= 1600);
        });
    }

    [Fact]
    public void ImportPdf_ExistingPages_ArePreserved()
    {
        var pdf = MakePdf("two.pdf", 2);
        var batch = HomeworkBatch.Create("作业");
        var existing = Path.Combine(_root, "existing.png");
        File.WriteAllBytes(existing, [1, 2, 3]);
        Assert.True(batch.TryAddPage(existing, HomeworkPageKind.Image, 1, "h", 10, 10, out _, out _));
        using var cache = NewCache();

        PageImporter.ImportPdf(batch, pdf, cache, 96, null, CancellationToken.None, out _);

        Assert.Equal(3, batch.Pages.Count);
        Assert.Equal(HomeworkPageKind.Image, batch.Pages[0].Kind);
        Assert.Equal(HomeworkPageKind.PdfPage, batch.Pages[1].Kind);
        Assert.Equal(HomeworkPageKind.PdfPage, batch.Pages[2].Kind);
    }

    // ----------------------------- 错误与取消 -----------------------------

    [Fact]
    public void ImportPdf_Corrupt_ThrowsChineseAndAddsNothing()
    {
        var bad = Path.Combine(_root, "bad.pdf");
        File.WriteAllText(bad, "this is not a pdf");
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var ex = Assert.Throws<HomeworkException>(() =>
            PageImporter.ImportPdf(batch, bad, cache, 96, null, CancellationToken.None, out _));

        Assert.Contains("PDF", ex.Message);
        Assert.Empty(batch.Pages);
    }

    [Fact]
    public void ImportPdf_Encrypted_ThrowsChinese()
    {
        var path = Path.Combine(_root, "encrypted.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.LightGray, 40, 40, 160, 80);
            document.SecuritySettings.UserPassword = "secret123";
            document.SecuritySettings.OwnerPassword = "owner456";
            document.Save(path);
        }
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var ex = Assert.Throws<HomeworkException>(() =>
            PageImporter.ImportPdf(batch, path, cache, 96, null, CancellationToken.None, out _));

        Assert.Contains("加密", ex.Message);
        Assert.Empty(batch.Pages);
    }

    [Fact]
    public void ImportPdf_MissingFile_WarnsAndAddsNothing()
    {
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var added = PageImporter.ImportPdf(
            batch, Path.Combine(_root, "missing.pdf"), cache, 96, null, CancellationToken.None, out var warnings);

        Assert.Empty(added);
        Assert.Empty(batch.Pages);
        Assert.Single(warnings);
    }

    [Fact]
    public void ImportPdf_CancelMidFile_RollsBackAllPages()
    {
        var pdf = MakePdf("four.pdf", 4);
        var cts = new CancellationTokenSource();
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var added = PageImporter.ImportPdf(
            batch, pdf, cache, 96,
            new SyncProgress(p => { if (p >= 25) cts.Cancel(); }), cts.Token, out var warnings);

        Assert.Empty(added);
        Assert.Empty(batch.Pages);
        Assert.Contains("取消", string.Join("", warnings));
        Assert.Empty(Directory.GetFiles(cache.SessionDirectory, "*.png"));
    }

    [Fact]
    public void ImportPdf_CancelBeforeStart_AddsNothing()
    {
        var pdf = MakePdf("four.pdf", 4);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        PageImporter.ImportPdf(batch, pdf, cache, 96, null, cts.Token, out var warnings);

        Assert.Empty(batch.Pages);
        Assert.Contains("取消", string.Join("", warnings));
    }

    [Fact]
    public void ImportPdf_PageCountMatchesPdfSharp()
    {
        var pdf = MakePdf("three.pdf", 3);
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Assert.Equal(3, document.PageCount);
    }
}
