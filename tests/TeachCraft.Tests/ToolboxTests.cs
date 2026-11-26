using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class ToolboxTests : IDisposable
{
    private readonly string _root;

    public ToolboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    [Fact]
    public void ImageConvert_PngToWebp_KeepsDimensions()
    {
        var source = MakeImage("source.png", 800, 400);
        var output = Path.Combine(_root, "converted.webp");

        ImageToolbox.Convert(source, output, "webp", 90);

        Assert.True(File.Exists(output));
        Assert.Equal((800, 400), ImageSize(output));
    }

    [Fact]
    public void ImageResize_Percent_ScalesBothSides()
    {
        var source = MakeImage("resize.png", 800, 400);
        var output = Path.Combine(_root, "resized.png");

        ImageToolbox.Resize(source, output, null, null, 50, true);

        Assert.Equal((400, 200), ImageSize(output));
    }

    [Fact]
    public void ImageCompress_ReducesJpegSize()
    {
        var source = Path.Combine(_root, "photo.jpg");
        using (var bitmap = MakeBitmap(1200, 800))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 96))
        using (var stream = File.Create(source)) data.SaveTo(stream);

        var before = new FileInfo(source).Length;
        var output = Path.Combine(_root, "compressed.jpg");
        ImageToolbox.Compress(source, output, 45, 900, out _);

        var after = new FileInfo(output).Length;
        Assert.True(after < before, $"压缩后应更小：{before} → {after}");
    }

    [Fact]
    public void ImageWatermark_KeepsSizeAndWritesFile()
    {
        var source = MakeImage("mark.png", 640, 480);
        var output = Path.Combine(_root, "marked.png");

        ImageToolbox.Watermark(source, output, "仅供教学使用", WatermarkPosition.BottomRight, 0.8, 1.0);

        Assert.True(File.Exists(output));
        Assert.Equal((640, 480), ImageSize(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void PdfMergeAndSplit_ProducesExpectedPages()
    {
        var first = Path.Combine(_root, "first.pdf");
        var second = Path.Combine(_root, "second.pdf");
        PdfToolbox.ImagesToPdf(new[] { MakeImage("a.png", 600, 400) }, first);
        PdfToolbox.ImagesToPdf(new[] { MakeImage("b.png", 400, 600) }, second);

        var merged = Path.Combine(_root, "merged.pdf");
        PdfToolbox.Merge(new[] { first, second }, merged);
        Assert.Equal(2, PageCount(merged));

        var extracted = Path.Combine(_root, "page2.pdf");
        PdfToolbox.Split(merged, extracted, "2");
        Assert.Equal(1, PageCount(extracted));
    }

    [Fact]
    public void PdfToImages_WritesOnePngPerPage()
    {
        var first = MakeImage("p1.png", 600, 400);
        var second = MakeImage("p2.png", 400, 600);
        var pdf = Path.Combine(_root, "two-pages.pdf");
        PdfToolbox.ImagesToPdf(new[] { first, second }, pdf);

        var folder = Path.Combine(_root, "pages");
        PdfToolbox.PdfToImages(pdf, folder, 96, new Progress<int>());

        Assert.Equal(2, Directory.GetFiles(folder, "*.png").Length);
    }

    [Fact]
    public void ExamPaperLayout_TwoUp_HalvesPageCount()
    {
        var input = MakeNumberedPdf("exam.pdf", 5);
        var output = Path.Combine(_root, "two-up.pdf");

        ExamPaperLayoutToolbox.Layout(input, output, ExamPaperLayoutMode.TwoUp);

        Assert.Equal(3, PageCount(output));
    }

    [Fact]
    public void ExamPaperLayout_Booklet_PadsToMultipleOfFour()
    {
        var order = ExamPaperLayoutPlanner.BuildSlots(5, ExamPaperLayoutMode.Booklet);

        Assert.Equal(8, order.Count);
        Assert.Equal(new int?[] { null, 0, 1, null, null, 2, 3, 4 }, order);
    }

    [Fact]
    public void ExamPaperLayout_RejectsOverwritingInput()
    {
        var input = MakeNumberedPdf("same.pdf", 2);

        Assert.Throws<ToolboxException>(() =>
            ExamPaperLayoutToolbox.Layout(input, input, ExamPaperLayoutMode.TwoUp));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    [InlineData(1200)]
    public void ExamPaperLayout_RenderPixelSize_TracksFinalPrintDpi(int dpi)
    {
        var page = new PdfSharp.Pdf.PdfPage { Width = PdfSharp.Drawing.XUnit.FromPoint(595.28), Height = PdfSharp.Drawing.XUnit.FromPoint(841.89) };
        var slot = new PdfSharp.Drawing.XRect(0, 0, 420.945, 595.28);

        var (width, height) = ExamPaperLayoutPlanner.RenderPixelSize(page, slot, dpi);

        Assert.Equal((int)Math.Ceiling(420.945 * dpi / 72), width);
        Assert.Equal((int)Math.Ceiling(595.28 * dpi / 72), height);
    }

    [Theory]
    [InlineData("300 DPI", 300)]
    [InlineData("600 DPI", 600)]
    [InlineData("1200 DPI", 1200)]
    public void ExamPaperLayoutViewModel_CarriesSelectedDpi(string label, int expectedDpi)
    {
        var tool = new TeachCraft.App.ViewModels.ToolItemViewModel(
            ToolKind.ExamPaperLayout, "13", "exam-paper-layout", "试卷排版",
            "PDF 排版", "file", "档", "PDF", "PDF");
        tool.SelectedExamPaperDpi = label;

        Assert.Equal(expectedDpi, tool.ToRequest("output.pdf").ExamPaperDpi);
        Assert.Equal("文件-排版.pdf", tool.SuggestedFileName());
        Assert.True(tool.ShowPlainNote);
    }

    [Fact]
    public void CertificateRosterReader_RequiresExplicitMappingsAndPreservesDuplicateRows()
    {
        var roster = MakeCertificateRoster("awards.xlsx");
        var sheets = CertificateRosterReader.InspectWorkbook(roster);
        Assert.Equal(new[] { "Cover", "Awards" }, sheets.Worksheets.Select(sheet => sheet.Name));

        var columns = CertificateRosterReader.InspectWorksheet(roster, "Awards", 3);
        Assert.Equal(new[] { "A · 编号", "B · 姓名", "C · 奖项" }, columns.Columns.Select(column => column.DisplayName));

        var records = CertificateRosterReader.Read(roster, "Awards", 3, 1, 2);
        Assert.Equal(2, records.Count);
        Assert.Equal(new CertificateRecord("合成甲", "优秀学生"), records[0]);
        Assert.Equal(new CertificateRecord("合成甲", "学习进步"), records[1]);
    }

    [Fact]
    public void CertificateRenderer_PreviewAndPdfUseTemplateGeometry()
    {
        var template = MakeCertificateTemplate("template.png");
        var preview = CertificateRenderer.RenderPreview(template,
            new CertificateRecord("合成甲", "优秀学生"), CertificateTextPlacement.Default);
        Assert.True(preview.Png.Length > 1000);
        Assert.Equal((1200, 848), (preview.Width, preview.Height));

        var roster = MakeCertificateRoster("awards.xlsx");
        var output = Path.Combine(_root, "certificates.pdf");
        var request = new CertificateBatchRequest(template, roster, "Awards", 3, 1, 2,
            CertificateTextPlacement.Default, output);
        var summary = CertificateBatchToolbox.Generate(request);

        Assert.Equal(2, PageCount(output));
        Assert.Contains("2 张奖状", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificateRenderer_RejectsOverwritingEitherInput()
    {
        var template = MakeCertificateTemplate("template.png");
        var roster = MakeCertificateRoster("awards.xlsx");
        var templateOutput = new CertificateBatchRequest(template, roster, "Awards", 3, 1, 2,
            CertificateTextPlacement.Default, template);
        var rosterOutput = templateOutput with { OutputPath = roster };

        Assert.Throws<ToolboxException>(() => CertificateBatchToolbox.Generate(templateOutput));
        Assert.Throws<ToolboxException>(() => CertificateBatchToolbox.Generate(rosterOutput));
    }

    private string MakeCertificateRoster(string name)
    {
        var path = Path.Combine(_root, name);
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Cover").Cell(1, 1).Value = "名单说明页";
        var sheet = workbook.AddWorksheet("Awards");
        sheet.Cell(1, 1).Value = "校级评优名单";
        sheet.Cell(3, 1).Value = "编号";
        sheet.Cell(3, 2).Value = "姓名";
        sheet.Cell(3, 3).Value = "奖项";
        sheet.Cell(4, 1).Value = "001";
        sheet.Cell(4, 2).Value = " 合成甲 ";
        sheet.Cell(4, 3).Value = "优秀学生";
        sheet.Cell(5, 1).Value = "002";
        sheet.Cell(5, 2).Value = "合成甲";
        sheet.Cell(5, 3).Value = "学习进步";
        workbook.SaveAs(path);
        return path;
    }

    private string MakeCertificateTemplate(string name)
    {
        var path = Path.Combine(_root, name);
        using var bitmap = new SKBitmap(900, 600);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 250, 230));
            using var paint = new SKPaint { Color = new SKColor(170, 80, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 8 };
            canvas.DrawRect(12, 12, 876, 576, paint);
        }
        using var stream = File.Create(path);
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        return path;
    }

    [Fact]
    public void ExamPaperLayoutViewModel_RefreshesPrintQualityNote()
    {
        var tool = new TeachCraft.App.ViewModels.ToolItemViewModel(
            ToolKind.ExamPaperLayout, "13", "exam-paper-layout", "试卷排版",
            "PDF 排版", "file", "档", "PDF", "PDF");
        var notified = new List<string?>();
        tool.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        tool.SelectedExamPaperDpi = "1200 DPI";

        Assert.Contains(nameof(tool.PlainNote), notified);
        Assert.True(tool.ShowPlainNote);
    }

    [Fact]
    public void ExamPaperLayoutPreview_RendersFirstTwoUpSide()
    {
        var input = MakeNumberedPdf("preview.pdf", 4);

        var preview = ExamPaperLayoutPreviewRenderer.Render(input, ExamPaperLayoutMode.TwoUp, 600);

        using var bitmap = SKBitmap.Decode(preview.Png);
        Assert.Equal(1404, bitmap.Width);
        Assert.Equal(993, bitmap.Height);
        Assert.Contains("第 1 页", preview.Detail, StringComparison.Ordinal);
        Assert.Contains("第 2 页", preview.Detail, StringComparison.Ordinal);
        Assert.Contains("600 DPI", preview.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void ExamPaperLayoutPreview_BookletUsesFirstImposedSide()
    {
        var input = MakeNumberedPdf("booklet-preview.pdf", 5);

        var preview = ExamPaperLayoutPreviewRenderer.Render(input, ExamPaperLayoutMode.Booklet, 1200);

        Assert.Contains("小册子首面", preview.Caption, StringComparison.Ordinal);
        Assert.Contains("空白页", preview.Detail, StringComparison.Ordinal);
        Assert.Contains("第 1 页", preview.Detail, StringComparison.Ordinal);
        Assert.Contains("1200 DPI", preview.Caption, StringComparison.Ordinal);
    }

    // ---- 输出保护：ToolDispatcher 入口的 EnsureOutputDoesNotOverwriteInput ----

    [Fact]
    public void Run_RejectsOutputEqualToInput_ForImageTool()
    {
        var source = MakeImage("guard.png", 200, 200);
        var request = new ToolRequest
        {
            Kind = ToolKind.ImageCompress,
            Inputs = new[] { source },
            Output = source,
        };

        var ex = Assert.Throws<ToolboxException>(() => ToolDispatcher.Run(request));
        Assert.Contains("不能覆盖", ex.Message);
    }

    [Fact]
    public void Run_RejectsOutputEqualToInput_CaseInsensitive()
    {
        var source = MakeImage("CaseGuard.png", 200, 200);
        var samePathDifferentCase = Path.Combine(_root, "caseguard.PNG");
        var request = new ToolRequest
        {
            Kind = ToolKind.ImageResize,
            Inputs = new[] { source },
            Output = samePathDifferentCase,
            Percent = 50,
        };

        var ex = Assert.Throws<ToolboxException>(() => ToolDispatcher.Run(request));
        Assert.Contains("不能覆盖", ex.Message);
    }

    [Fact]
    public void Run_AllowsDistinctOutput()
    {
        var source = MakeImage("ok.png", 200, 200);
        var output = Path.Combine(_root, "ok-converted.webp");
        var summary = ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.ImageConvert,
            Inputs = new[] { source },
            Output = output,
            Format = "webp",
        });

        Assert.Contains("ok-converted", summary);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Run_PdfToImages_RejectsWhenTargetPagePngExists()
    {
        var first = MakeImage("guard1.png", 300, 200);
        var pdf = Path.Combine(_root, "guard-two.pdf");
        PdfToolbox.ImagesToPdf(new[] { first }, pdf);

        var folder = Path.Combine(_root, "guard-pages");
        Directory.CreateDirectory(folder);
        // 预先放一个与将要写出的文件名相同的文件：PdfToImages 会 File.Create 截断它
        File.WriteAllBytes(Path.Combine(folder, "guard-two-第1页.png"), Array.Empty<byte>());

        var ex = Assert.Throws<ToolboxException>(() => ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.PdfToImages,
            Inputs = new[] { pdf },
            Output = folder,
            Dpi = 96,
        }));
        Assert.Contains("已有", ex.Message);
    }

    [Fact]
    public void Run_PdfToImages_AllowsOutputFolderSameAsSourceFolder()
    {
        var image = MakeImage("same-folder.png", 300, 200);
        var pdf = Path.Combine(_root, "same-folder.pdf");
        PdfToolbox.ImagesToPdf(new[] { image }, pdf);

        // 输出目录 == 源 PDF 所在目录，且没有同名页图：合法
        var summary = ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.PdfToImages,
            Inputs = new[] { pdf },
            Output = _root,
            Dpi = 96,
        });

        Assert.Contains("PNG", summary);
        Assert.True(File.Exists(Path.Combine(_root, "same-folder-第1页.png")));
    }


    [Fact]
    public void WordToPdf_And_PdfToWord_RoundTripText()
    {
        if (CjkFont.Family == "Lato")
            return; // CI 环境缺少中文字体时跳过，避免误报

        var docx = MakeDocx("report.docx");
        var pdf = Path.Combine(_root, "report.pdf");
        PdfToolbox.WordToPdf(docx, pdf);

        Assert.True(new FileInfo(pdf).Length > 1024);
        Assert.Equal(1, PageCount(pdf));

        var extracted = Path.Combine(_root, "extracted.docx");
        PdfToolbox.PdfToWord(pdf, extracted, keepLayout: false);
        Assert.Contains("学情", ReadDocxText(extracted));
    }

    [Fact]
    public void PdfToWord_LayoutMode_EmbedsPageImages()
    {
        if (CjkFont.Family == "Lato") return;

        var docx = MakeDocx("layout.docx");
        var pdf = Path.Combine(_root, "layout.pdf");
        PdfToolbox.WordToPdf(docx, pdf);

        var layoutDocx = Path.Combine(_root, "layout-extracted.docx");
        PdfToolbox.PdfToWord(pdf, layoutDocx, keepLayout: true);

        using var word = WordprocessingDocument.Open(layoutDocx, false);
        Assert.True(word.MainDocumentPart!.ImageParts.Count() >= 1);
    }

    [Fact]
    public void PageRange_ParsesAndRejectsOutOfRange()
    {
        var pages = PdfPageRange.Parse("1-2,4", 5);
        Assert.Equal(new[] { 1, 2, 4 }, pages);
        Assert.Equal("1-2,4", PdfPageRange.Describe(pages));

        Assert.Throws<ToolboxException>(() => PdfPageRange.Parse("9", 5));
        Assert.Throws<ToolboxException>(() => PdfPageRange.Parse("3-1", 5));
    }

    // ---- 水印实时预览保真（调研 P2-21，T28） ----

    [Fact]
    public void WatermarkPreview_ScaleChangesThePreview()
    {
        // 修复前：240 宽预览下字号恒被 8px 地板卡住，0.4 与 1.0 渲染出**逐字节相同**的图，
        // 老师拖「字号」滑块（0.4–1.0 整段）完全没反应。
        var source = MakeImage("preview-scale.png", 640, 480);

        var small = ImageToolbox.RenderWatermarkPreview(source, "仅供教学使用", "右下角", 0.8, 0.4, 240);
        var large = ImageToolbox.RenderWatermarkPreview(source, "仅供教学使用", "右下角", 0.8, 1.0, 240);

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.False(small!.SequenceEqual(large!), "0.4 与 1.0 倍的预览应不同（字号必须随滑块变化）。");
    }

    [Fact]
    public void WatermarkPreview_UsesInkColorOnLightImage()
    {
        // 正式 Watermark 在浅底图上自动换墨字（ImageToolbox.cs:127-131），预览曾硬编码白字，
        // 浅底预览几乎看不见，与成品不是一回事。
        var source = MakeImage("preview-light.png", 640, 480);

        // 预览宽放大到 480：字号按 3.2% 比例算约 15px，笔画有实心核心，
        // 取色判定才稳定（240 宽下约 7.7px，抗锯齿会把核心冲淡）。
        var png = ImageToolbox.RenderWatermarkPreview(source, "仅供教学使用", "右下角", 0.8, 1.0, 480);
        Assert.NotNull(png);

        using var bitmap = SKBitmap.Decode(png!);
        Assert.NotNull(bitmap);

        // 文字落在右下角：取右下四分之一区域的最暗像素。墨字（20,20,19 @ 0.8）压浅底约 60；
        // 旧实现的白字 + 黑影最暗也只到约 150，阈值 100 能把两者分开。
        var darkest = 255;
        for (var y = bitmap!.Height / 2; y < bitmap.Height; y++)
        {
            for (var x = bitmap.Width / 2; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var luminance = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
                if (luminance < darkest) darkest = luminance;
            }
        }

        Assert.True(darkest < 100, $"浅底图上的预览水印应是深色墨字，实测最暗亮度 {darkest}。");
    }

    [Fact]
    public void WatermarkPreview_AppliesExifOrientation()
    {
        // 竖拍手机照片（EXIF Orientation=6，需顺时针转 90°）正式输出会摆正；
        // 预览曾用 SKBitmap.Decode 直接读像素，竖图在预览里横躺。
        var source = MakeJpegWithOrientation("preview-exif.jpg", 200, 100, orientation: 6);

        var png = ImageToolbox.RenderWatermarkPreview(source, "仅供教学使用", "右下角", 0.8, 1.0, 240);
        Assert.NotNull(png);

        using var preview = SKBitmap.Decode(png!);
        Assert.NotNull(preview);
        // 摆正后源图是 100×200（竖），240 宽预览对应 480 高；不摆正会是 240×120。
        Assert.Equal(240, preview!.Width);
        Assert.Equal(480, preview.Height);
    }

    // ---- 取消语义与半成品清理（调研 P2-22，T29） ----

    [Fact]
    public void CancelCopy_SaysItTakesEffectAfterThisRun()
    {
        var panel = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "Views", "ToolPanelView.axaml"));
        Assert.Contains("取消将在本轮处理完成后生效", panel);
        Assert.DoesNotContain("正在取消…", panel);
    }

    [Fact]
    public void DeleteCancelledOutput_RemovesFirstRunPartial()
    {
        var output = Path.Combine(_root, "cancel-first.png");
        var before = TeachCraft.App.Views.MainWindow.CaptureOutputSnapshot(output);
        Assert.False(before.Existed);

        File.WriteAllBytes(output, new byte[] { 1, 2, 3 });

        var deleted = TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.ImageConvert, output, before, DateTime.UtcNow.AddSeconds(-5));

        Assert.True(deleted);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void DeleteCancelledOutput_KeepsPreviousRunProduct()
    {
        // 修复前必红：同路径重跑、取消发生在写盘之前时，盘上是**上一场完好的成品**，
        // 旧判定（File.Exists && LastOutputPath == output）会把它删掉。
        var output = Path.Combine(_root, "cancel-previous.png");
        File.WriteAllBytes(output, new byte[] { 9, 9, 9 });
        var oldTime = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(output, oldTime);

        var before = TeachCraft.App.Views.MainWindow.CaptureOutputSnapshot(output);
        Assert.True(before.Existed);

        var deleted = TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.ImageConvert, output, before, DateTime.UtcNow);

        Assert.False(deleted);
        Assert.True(File.Exists(output), "上一场的成品不能被本轮取消误删。");
    }

    [Fact]
    public void DeleteCancelledOutput_RemovesRewrittenFile()
    {
        // 同路径重跑且本轮已写盘：写入时间晚于开始时刻 → 是本轮半成品，要删。
        var output = Path.Combine(_root, "cancel-rewritten.png");
        File.WriteAllBytes(output, new byte[] { 9, 9, 9 });
        File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(-10));
        var before = TeachCraft.App.Views.MainWindow.CaptureOutputSnapshot(output);

        var started = DateTime.UtcNow;
        File.WriteAllBytes(output, new byte[] { 1, 2, 3, 4 });
        File.SetLastWriteTimeUtc(output, started.AddSeconds(1));

        var deleted = TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.ImageConvert, output, before, started);

        Assert.True(deleted);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void DeleteCancelledOutput_SkipsPdfToImagesFolder()
    {
        var folder = Path.Combine(_root, "cancel-pages");
        Directory.CreateDirectory(folder);

        var deleted = TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.PdfToImages, folder, new TeachCraft.App.Views.MainWindow.OutputSnapshot(false, null), DateTime.UtcNow);

        Assert.False(deleted);
        Assert.True(Directory.Exists(folder));
    }

    // ---- 参数预检前移与 DPI 对齐（调研 P2-24 / P2-27，T30） ----

    [Fact]
    public void ParameterCheck_RejectsSingleFileMerge()
    {
        var message = ToolParameterCheck.Validate(
            ToolKind.PdfMerge, new[] { Path.Combine(_root, "only.pdf") }, null);

        Assert.NotNull(message);
        Assert.Contains("至少", message);
    }

    [Fact]
    public void ParameterCheck_RejectsMalformedPageRange()
    {
        var message = ToolParameterCheck.Validate(
            ToolKind.PdfSplit, new[] { Path.Combine(_root, "doc.pdf") }, "5-2");

        Assert.NotNull(message);
        Assert.Contains("5-2", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("1-2,4")]
    [InlineData("2-")]
    [InlineData("-3")]
    public void ParameterCheck_AcceptsWellFormedPageRange(string? pageRange)
    {
        // 超范围（共 5 页却填 9）要读文件才知道，留给执行期——这里只挡写法错误。
        Assert.Null(ToolParameterCheck.ValidatePageRangeSyntax(pageRange));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("3-1")]
    [InlineData("-")]
    [InlineData("a-2")]
    public void ParameterCheck_RejectsBadPageRangeSyntax(string pageRange)
    {
        Assert.NotNull(ToolParameterCheck.ValidatePageRangeSyntax(pageRange));
    }

    [Fact]
    public void PdfToWord_LayoutMode_Honors72Dpi()
    {
        // 修复前必红：渲染被静默钳到 96，选 72 与选 96 得到同一尺寸的页图。
        var image = MakeImage("dpi-source.png", 600, 400);
        var pdf = Path.Combine(_root, "dpi-source.pdf");
        PdfToolbox.ImagesToPdf(new[] { image }, pdf);

        var at72 = Path.Combine(_root, "dpi-72.docx");
        var at96 = Path.Combine(_root, "dpi-96.docx");
        PdfToolbox.PdfToWord(pdf, at72, keepLayout: true, dpi: 72);
        PdfToolbox.PdfToWord(pdf, at96, keepLayout: true, dpi: 96);

        var width72 = FirstEmbeddedImageWidth(at72);
        var width96 = FirstEmbeddedImageWidth(at96);

        Assert.True(width72 < width96,
            $"72 DPI 的页图应比 96 DPI 窄：72→{width72}px，96→{width96}px（修复前两者相同，说明被钳到 96）。");
        // 源图是横图，落在 A4 横版上：72 DPI 约 842px 宽，96 DPI 约 1123px 宽。
        Assert.InRange(width72, 780, 900);
        Assert.InRange(width96, 1050, 1200);
    }

    [Fact]
    public void DeleteCancelledOutput_PdfToImages_DeletesOnlyNewPagePngsAndPreservesPreexisting()
    {
        var outDir = Path.Combine(_root, "pdf-to-images-cancel");
        Directory.CreateDirectory(outDir);
        var preexistingMatch = Path.Combine(outDir, "old-第1页.png");
        var keepPng = Path.Combine(outDir, "keep.png");
        var keepTxt = Path.Combine(outDir, "readme.txt");
        File.WriteAllBytes(preexistingMatch, [1, 2, 3]);
        File.WriteAllBytes(keepPng, [4, 5, 6]);
        File.WriteAllText(keepTxt, "keep");

        var before = global::TeachCraft.App.Views.MainWindow.CaptureOutputSnapshot(ToolKind.PdfToImages, outDir);
        var runStartedUtc = DateTime.UtcNow;

        Assert.False(global::TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.PdfToImages, outDir, before, runStartedUtc));

        var newPage1 = Path.Combine(outDir, "doc-第1页.png");
        var newPage2 = Path.Combine(outDir, "doc-第2页.png");
        var unrelatedNewPng = Path.Combine(outDir, "extra.png");
        File.WriteAllBytes(newPage1, [7, 8]);
        File.WriteAllBytes(newPage2, [9, 10]);
        File.WriteAllBytes(unrelatedNewPng, [11, 12]);

        Assert.True(global::TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.PdfToImages, outDir, before, runStartedUtc));
        Assert.True(Directory.Exists(outDir));
        Assert.False(File.Exists(newPage1));
        Assert.False(File.Exists(newPage2));
        Assert.True(File.Exists(preexistingMatch));
        Assert.True(File.Exists(keepPng));
        Assert.True(File.Exists(keepTxt));
        Assert.True(File.Exists(unrelatedNewPng));
    }

    [Fact]
    public void DeleteCancelledOutput_ExcelClassSplit_DeletesOnlyNewWorkbooksAndPreservesPreexisting()
    {
        var outDir = Path.Combine(_root, "excel-split-cancel");
        Directory.CreateDirectory(outDir);
        var preexistingXlsx = Path.Combine(outDir, "现有成绩表.xlsx");
        var keepTxt = Path.Combine(outDir, "说明.txt");
        File.WriteAllBytes(preexistingXlsx, [1, 2, 3]);
        File.WriteAllText(keepTxt, "keep");

        var before = global::TeachCraft.App.Views.MainWindow.CaptureOutputSnapshot(ToolKind.ExcelClassSplit, outDir);
        var runStartedUtc = DateTime.UtcNow;

        var newClass1 = Path.Combine(outDir, "八年级1班.xlsx");
        var newClass2 = Path.Combine(outDir, "八年级2班.xlsx");
        File.WriteAllBytes(newClass1, [4, 5]);
        File.WriteAllBytes(newClass2, [6, 7]);

        Assert.True(global::TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(
            ToolKind.ExcelClassSplit, outDir, before, runStartedUtc));
        Assert.True(Directory.Exists(outDir));
        Assert.False(File.Exists(newClass1));
        Assert.False(File.Exists(newClass2));
        Assert.True(File.Exists(preexistingXlsx));
        Assert.True(File.Exists(keepTxt));
    }

    private static int FirstEmbeddedImageWidth(string docxPath)
    {
        using var word = WordprocessingDocument.Open(docxPath, false);
        var part = word.MainDocumentPart!.ImageParts.First();
        using var stream = part.GetStream();
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        return codec!.Info.Width;
    }

    /// <summary>
    /// 生成一张带 EXIF Orientation 的 JPEG：在 SOI 之后插入一个只含 Orientation 项的 APP1。
    /// </summary>
    private string MakeJpegWithOrientation(string name, int width, int height, ushort orientation)
    {
        var path = Path.Combine(_root, name);
        using var bitmap = MakeBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);

        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(new byte[] { 0x49, 0x49, 0x2A, 0x00 });   // 小端 TIFF 头
            writer.Write(8);                                         // IFD0 偏移
            writer.Write((ushort)1);                                 // 1 个条目
            writer.Write((ushort)0x0112);                            // Orientation
            writer.Write((ushort)3);                                 // SHORT
            writer.Write(1);                                         // count
            writer.Write((ushort)orientation);                       // 值（小端，占前 2 字节）
            writer.Write((ushort)0);                                 // 值字段补齐
            writer.Write(0);                                         // 下一个 IFD
        }

        var tiffBytes = tiff.ToArray();
        var payload = new byte[6 + tiffBytes.Length];
        payload[0] = (byte)'E'; payload[1] = (byte)'x'; payload[2] = (byte)'i';
        payload[3] = (byte)'f'; payload[4] = 0; payload[5] = 0;
        tiffBytes.CopyTo(payload, 6);

        var jpegBytes = data.ToArray();
        using var output = File.Create(path);
        output.Write(jpegBytes, 0, 2);                              // SOI
        output.Write(new byte[] { 0xFF, 0xE1 });                    // APP1
        var length = payload.Length + 2;
        output.Write(new[] { (byte)(length >> 8), (byte)(length & 0xFF) });
        output.Write(payload);
        output.Write(jpegBytes, 2, jpegBytes.Length - 2);
        return path;
    }

    private string MakeImage(string name, int width, int height)
    {
        var path = Path.Combine(_root, name);
        using var bitmap = MakeBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static SKBitmap MakeBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0xDD, 0xEB, 0xF7));
        using var paint = new SKPaint { Color = new SKColor(0x70, 0xAD, 0x47) };
        canvas.DrawRect(0, 0, width, height / 3f, paint);
        return bitmap;
    }

    private string MakeNumberedPdf(string name, int pages)
    {
        var path = Path.Combine(_root, name);
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (var index = 0; index < pages; index++)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(PdfSharp.Drawing.XPens.Black, 20 + index * 3, 20, 100, 100);
        }
        document.Save(path);
        return path;
    }

    private static (int Width, int Height) ImageSize(string path)
    {
        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        return (codec!.Info.Width, codec.Info.Height);
    }

    private static int PageCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private string MakeDocx(string name)
    {
        var path = Path.Combine(_root, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();

        var body = new Body(
            new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text("九年级六班 · 学情分析"))),
            new Paragraph(new Run(new Text("本次考试全班 49 人参加，总分均分 514.91 分。"))),
            new Table(
                new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("科目")))),
                    new TableCell(new Paragraph(new Run(new Text("均分"))))),
                new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("语文")))),
                    new TableCell(new Paragraph(new Run(new Text("73.29")))))));

        main.Document = new Document(body);
        main.Document.Save();
        return path;
    }

    private static string ReadDocxText(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }
}
