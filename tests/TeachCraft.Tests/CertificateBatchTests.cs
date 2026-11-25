using ClosedXML.Excel;
using NPOI.HSSF.UserModel;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class CertificateBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));

    public CertificateBatchTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Reader_UsesExplicitWorksheetAndColumns_AndKeepsDuplicateRows()
    {
        var workbookPath = MakeRoster();
        var worksheets = CertificateRosterReader.InspectWorkbook(workbookPath);
        Assert.Equal(new[] { "Cover", "Awards" }, worksheets.Worksheets.Select(sheet => sheet.Name));

        var sheet = CertificateRosterReader.InspectWorksheet(workbookPath, "Awards", 3);
        Assert.Equal(new[] { "编号", "姓名", "奖项" }, sheet.Columns.Select(column => column.Header));

        var records = CertificateRosterReader.Read(workbookPath, "Awards", 3, 1, 2);
        Assert.Collection(records,
            record => Assert.Equal(new CertificateRecord("合成甲", "优秀学生"), record),
            record => Assert.Equal(new CertificateRecord("合成甲", "学习进步"), record));
    }

    [Fact]
    public void Renderer_PreviewAndPdfShareCertificateLayout()
    {
        var templatePath = MakeTemplate();
        var rosterPath = MakeRoster();
        var preview = CertificateRenderer.RenderPreview(templatePath,
            new CertificateRecord("合成甲", "优秀学生"), CertificateTextPlacement.Default);
        Assert.True(preview.Png.Length > 1000);

        var output = Path.Combine(_root, "awards.pdf");
        var request = new CertificateBatchRequest(templatePath, rosterPath, "Awards", 3, 1, 2,
            CertificateTextPlacement.Default, output);
        CertificateBatchToolbox.Generate(request);

        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
    }

    [Fact]
    public void Batch_RejectsOverwritingTemplateOrRoster()
    {
        var templatePath = MakeTemplate();
        var rosterPath = MakeRoster();
        var request = new CertificateBatchRequest(templatePath, rosterPath, "Awards", 3, 1, 2,
            CertificateTextPlacement.Default, templatePath);
        Assert.Throws<ToolboxException>(() => CertificateBatchToolbox.Generate(request));

        var rosterOutput = request with { OutputPath = rosterPath };
        Assert.Throws<ToolboxException>(() => CertificateBatchToolbox.Generate(rosterOutput));
    }

    [Fact]
    public void Reader_LegacyXlsUsesChosenSheetHeaderAndColumnsWithoutDeduplicating()
    {
        var path = Path.Combine(_root, "legacy.xls");
        using (var workbook = new HSSFWorkbook())
        {
            workbook.CreateSheet("Cover").CreateRow(0).CreateCell(0).SetCellValue("说明");
            var sheet = workbook.CreateSheet("Chosen");
            sheet.CreateRow(0).CreateCell(0).SetCellValue("标题");
            var header = sheet.CreateRow(2);
            header.CreateCell(0).SetCellValue("奖项");
            header.CreateCell(1).SetCellValue("编号");
            header.CreateCell(2).SetCellValue("姓名");
            foreach (var rowIndex in new[] { 3, 4 })
            {
                var row = sheet.CreateRow(rowIndex);
                row.CreateCell(0).SetCellValue(" 优秀\n 学生 ");
                row.CreateCell(1).SetCellValue(rowIndex);
                row.CreateCell(2).SetCellValue(" 合成乙 ");
            }
            sheet.CreateRow(5).CreateCell(0).SetCellValue("无姓名的行");
            using var stream = File.Create(path);
            workbook.Write(stream);
        }

        Assert.Equal(new[] { "Cover", "Chosen" },
            CertificateRosterReader.InspectWorkbook(path).Worksheets.Select(sheet => sheet.Name));
        Assert.Equal(new[] { "奖项", "编号", "姓名" },
            CertificateRosterReader.InspectWorksheet(path, "Chosen", 3).Columns.Select(column => column.Header));
        var records = CertificateRosterReader.Read(path, "Chosen", 3, 2, 0);
        Assert.Equal(new[] { new CertificateRecord("合成乙", "优秀 学生"),
            new CertificateRecord("合成乙", "优秀 学生") }, records);
        var noAward = CertificateRosterReader.Read(path, "Chosen", 3, 2, -1);
        Assert.Equal(2, noAward.Count);
        Assert.All(noAward, record => Assert.Equal(new CertificateRecord("合成乙", string.Empty), record));
    }

    [Theory]
    [InlineData("missing", 3, 1, 2)]
    [InlineData("awards", 3, 1, 2)]
    [InlineData("Awards", 0, 1, 2)]
    [InlineData("Awards", 6, 1, 2)]
    [InlineData("Awards", 3, -1, 2)]
    [InlineData("Awards", 3, 3, 2)]
    [InlineData("Awards", 3, 1, -2)]
    [InlineData("Awards", 3, 1, 3)]
    [InlineData("Awards", 3, 1, 1)]
    [InlineData("Awards", 5, 1, 2)]
    public void Reader_RejectsInvalidMappingsInsteadOfGuessing(string worksheet, int header, int name, int award)
    {
        var path = MakeRoster();
        var before = File.ReadAllBytes(path);
        Assert.Throws<ToolboxException>(() => CertificateRosterReader.Read(path, worksheet, header, name, award));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RendererAndDispatcher_ProtectEitherSourceIncludingNormalizedAliases(bool dispatcher, bool rosterTarget)
    {
        var template = MakeTemplate();
        var roster = MakeRoster();
        var templateBefore = File.ReadAllBytes(template);
        var rosterBefore = File.ReadAllBytes(roster);
        var target = rosterTarget ? roster : template;
        var alias = Path.Combine(_root, ".", Path.GetFileName(target));

        var error = Assert.Throws<ToolboxException>(() =>
        {
            if (dispatcher)
                ToolDispatcher.Run(new ToolRequest
                {
                    Kind = ToolKind.CertificateBatch, Inputs = new[] { template, roster }, Output = alias,
                    CertificateWorksheet = "Awards", CertificateHeaderRow = 3,
                    CertificateNameColumn = 1, CertificateAwardColumn = 2,
                });
            else
                CertificateRenderer.RenderPdf(new CertificateRenderRequest(template, roster,
                    new[] { new CertificateRecord("合成甲", "优秀") }, CertificateTextPlacement.Default, alias));
        });

        Assert.Contains("不能覆盖", error.Message);
        Assert.Equal(templateBefore, File.ReadAllBytes(template));
        Assert.Equal(rosterBefore, File.ReadAllBytes(roster));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp.pdf"));
    }

    [Theory]
    [InlineData(900, 600, 1200, 848)]
    [InlineData(600, 900, 1200, 1697)]
    public void Renderer_NormalizesPreviewAndEveryPdfPageToA4(int width, int height, int previewWidth, int previewHeight)
    {
        var template = MakeTemplate(width, height);
        var preview = CertificateRenderer.RenderPreview(template, new CertificateRecord("", ""), CertificateTextPlacement.Default);
        Assert.Equal((previewWidth, previewHeight), (preview.Width, preview.Height));
        using var bitmap = SKBitmap.Decode(preview.Png);
        Assert.Equal((previewWidth, previewHeight), (bitmap.Width, bitmap.Height));
        Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));
        Assert.Equal(new SKColor(255, 250, 230), bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));

        var output = Path.Combine(_root, "a4.pdf");
        CertificateBatchToolbox.Generate(new CertificateBatchRequest(template, MakeRoster(), "Awards", 3, 1, 2,
            CertificateTextPlacement.Default, output));
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
        foreach (var page in pdf.Pages)
        {
            var mediaBox = page.MediaBox;
            var expectedWidth = width > height ? 841.89 : 595.28;
            var expectedHeight = width > height ? 595.28 : 841.89;
            Assert.Equal(0d, mediaBox.X1);
            Assert.Equal(0d, mediaBox.Y1);
            Assert.True(Math.Abs(mediaBox.Width - expectedWidth) <= 0.5,
                $"MediaBox width {mediaBox.Width} must be within 0.5 pt of A4 {expectedWidth}: Skia rounds its 72-DPI page device to whole pixels before writing MediaBox.");
            Assert.True(Math.Abs(mediaBox.Height - expectedHeight) <= 0.5,
                $"MediaBox height {mediaBox.Height} must be within 0.5 pt of A4 {expectedHeight}: Skia rounds its 72-DPI page device to whole pixels before writing MediaBox.");
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Placement_RejectsNonFiniteValuesInEveryFieldBeforeRendering(double value)
    {
        var placement = CertificateTextPlacement.Default;
        var invalid = new[] { placement with { NameX = value }, placement with { NameY = value },
            placement with { AwardX = value }, placement with { AwardY = value },
            placement with { NameFontScale = value }, placement with { AwardFontScale = value } };
        var template = MakeTemplate();
        var roster = MakeRoster();
        var output = Path.Combine(_root, "invalid.pdf");
        foreach (var candidate in invalid)
        {
            Assert.Throws<ToolboxException>(() => candidate.Clamp());
            Assert.Throws<ToolboxException>(() => CertificateRenderer.RenderPreview(template,
                new CertificateRecord("合成甲", "优秀"), candidate));
            Assert.Throws<ToolboxException>(() => CertificateRenderer.RenderPdf(new CertificateRenderRequest(
                template, roster, new[] { new CertificateRecord("合成甲", "优秀") }, candidate, output)));
        }
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp.pdf"));
    }

    [Fact]
    public void Batch_ExplicitNoAwardGeneratesEveryXlsxRecordWithoutChangingInputs()
    {
        var template = MakeTemplate();
        var roster = MakeRoster();
        var templateBefore = File.ReadAllBytes(template);
        var rosterBefore = File.ReadAllBytes(roster);
        var records = CertificateRosterReader.Read(roster, "Awards", 3, 1, -1);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(string.Empty, record.Award));
        var output = Path.Combine(_root, "no-award.pdf");
        CertificateBatchToolbox.Generate(new CertificateBatchRequest(template, roster, "Awards", 3, 1, -1,
            CertificateTextPlacement.Default, output));
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(templateBefore, File.ReadAllBytes(template));
        Assert.Equal(rosterBefore, File.ReadAllBytes(roster));
    }

    [Fact]
    public void Batch_RejectsExistingOutputWithoutChangingIt()
    {
        var output = Path.Combine(_root, "existing.pdf");
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(output, original);
        var request = new CertificateBatchRequest(MakeTemplate(), MakeRoster(), "Awards", 3, 1, 2,
            CertificateTextPlacement.Default, output);

        Assert.Throws<ToolboxException>(() => CertificateBatchToolbox.Generate(request));

        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp.pdf"));
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(100, false)]
    public void Batch_CancellationBeforePublishPreservesDestinationOwnership(int cancelAt, bool createDestination)
    {
        var output = Path.Combine(_root, "cancelled.pdf");
        byte[] foreignBytes = [1, 2, 3, 4];
        var request = new ToolRequest
        {
            Kind = ToolKind.CertificateBatch,
            Inputs = [MakeTemplate(), MakeRoster()],
            Output = output,
            CertificateWorksheet = "Awards",
            CertificateHeaderRow = 3,
            CertificateNameColumn = 1,
            CertificateAwardColumn = 2,
        };
        var before = TeachCraft.App.Views.MainWindow.CaptureOutputSnapshot(output);
        var started = DateTime.UtcNow;
        using var cancellation = new CancellationTokenSource();
        var reported = new List<int>();
        var progress = new InlineProgress(value =>
        {
            reported.Add(value);
            if (value != cancelAt) return;

            Assert.Single(Directory.GetFiles(_root, "*.certificate.tmp.pdf"));
            if (createDestination)
            {
                using var destination = new FileStream(output, FileMode.CreateNew);
                destination.Write(foreignBytes);
            }
            cancellation.Cancel();
        });

        var error = Assert.Throws<OperationCanceledException>(() =>
            ToolDispatcher.Run(request, progress, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancelAt == 50 ? new[] { 50 } : new[] { 50, 100 }, reported.ToArray());
        Assert.False(TeachCraft.App.Views.MainWindow.DeleteCancelledOutput(request.Kind, output, before, started));
        if (createDestination)
            Assert.Equal(foreignBytes, File.ReadAllBytes(output));
        else
            Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, "*.certificate.tmp.pdf"));
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private string MakeRoster()
    {
        var path = Path.Combine(_root, "roster.xlsx");
        using var book = new XLWorkbook();
        book.AddWorksheet("Cover").Cell(1, 1).Value = "名单说明";
        var sheet = book.AddWorksheet("Awards");
        sheet.Cell(1, 1).Value = "获奖名单";
        sheet.Cell(3, 1).Value = "编号";
        sheet.Cell(3, 2).Value = "姓名";
        sheet.Cell(3, 3).Value = "奖项";
        sheet.Cell(4, 1).Value = "001";
        sheet.Cell(4, 2).Value = " 合成甲 ";
        sheet.Cell(4, 3).Value = "优秀学生";
        sheet.Cell(5, 1).Value = "002";
        sheet.Cell(5, 2).Value = "合成甲";
        sheet.Cell(5, 3).Value = "学习进步";
        book.SaveAs(path);
        return path;
    }

    private string MakeTemplate(int width = 900, int height = 600)
    {
        var path = Path.Combine(_root, "certificate.png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 250, 230));
            using var paint = new SKPaint { Color = new SKColor(170, 80, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 8 };
            canvas.DrawRect(10, 10, width - 20, height - 20, paint);
        }
        using var stream = File.Create(path);
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        return path;
    }
}
