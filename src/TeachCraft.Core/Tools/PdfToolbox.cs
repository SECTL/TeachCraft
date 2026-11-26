using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using TeachCraft.Shared;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace TeachCraft.Core.Tools;

/// <summary>PDF / Word 处理：合并、拆分、互转、图片合成。</summary>
public static class PdfToolbox
{
    static PdfToolbox()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
    }

    public static string Merge(IReadOnlyList<string> inputs, string output)
    {
        if (inputs.Count < 2) throw new ToolboxException("合并至少需要两份 PDF。");
        EnsureDistinct(inputs, output);

        try
        {
            using var target = new PdfDocument();
            var pages = 0;
            for (var index = 0; index < inputs.Count; index++)
            {
                var file = inputs[index];
                // 逐文件包一层：8 份合并到第 5 份坏了，必须说清是第几份、叫什么，
                // 否则老师只能一份份重试排查（审计 P2-27）。
                try
                {
                    using var source = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (var i = 0; i < source.PageCount; i++)
                    {
                        target.AddPage(source.Pages[i]);
                        pages++;
                    }
                }
                catch (Exception ex) when (ex is not ToolboxException)
                {
                    throw new ToolboxException(
                        $"第 {index + 1} 份「{Path.GetFileName(file)}」读取失败：{ExplainPdfError(ex)}");
                }
            }

            target.Save(output);
            return $"{inputs.Count} 份文件 → {pages} 页 · {Path.GetFileName(output)}";
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex)
        {
            throw new ToolboxException($"合并失败：{ExplainPdfError(ex)}");
        }
    }

    public static string Split(string input, string output, string pageRange)
    {
        EnsureDistinct(new[] { input }, output);
        try
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            var pages = PdfPageRange.Parse(pageRange, source.PageCount);

            using var target = new PdfDocument();
            foreach (var page in pages) target.AddPage(source.Pages[page - 1]);
            target.Save(output);

            return $"第 {PdfPageRange.Describe(pages)} 页 · 共 {pages.Count} 页 → {Path.GetFileName(output)}";
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex)
        {
            throw new ToolboxException($"拆分失败：{ExplainPdfError(ex)}");
        }
    }

    public static string ImagesToPdf(
        IReadOnlyList<string> images, string output, IProgress<int>? progress = null)
    {
        if (images.Count == 0) throw new ToolboxException("请先选择图片。");

        try
        {
            var index = 0;
            var total = Math.Max(1, images.Count);
            Document.Create(container =>
            {
                foreach (var file in images)
                {
                    var (width, height) = ReadPixelSize(file);
                    container.Page(page =>
                    {
                        page.Size(width > height ? PageSizes.A4.Landscape() : PageSizes.A4);
                        page.Margin(24);
                        page.PageColor(Colors.White);
                        page.Content().AlignCenter().AlignMiddle().Image(file).FitArea();
                    });

                    // 30 张照片合成 PDF 要几秒到十几秒，逐张上报（审计 P2-30）。
                    progress?.Report(++index * 100 / total);
                }
            }).GeneratePdf(output);

            return $"{images.Count} 张图片 → {images.Count} 页 PDF · {Path.GetFileName(output)}";
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex)
        {
            throw new ToolboxException(ErrorText.Describe(ex, "合成 PDF"));
        }
    }

    public static string PdfToImages(
        string input,
        string outputDirectory,
        int dpi,
        IProgress<int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(input));

        try
        {
            var pageCount = PageCount(input);
            using var stream = File.OpenRead(input);

            // 一次加载整份 PDF，再逐页惰性渲染；逐页调用 ToImage 会让 pdfium 每页重新解析整份文档。
            var options = new PDFtoImage.RenderOptions { Dpi = Math.Clamp(dpi, 72, 300) };
            var pages = Enumerable.Range(0, pageCount);
            var index = 0;
            foreach (var bitmap in PDFtoImage.Conversion.ToImages(
                         stream, pages, leaveOpen: true, password: null, options: options))
            {
                using (bitmap)
                {
                    var path = Path.Combine(outputDirectory, $"{baseName}-第{index + 1}页.png");
                    using var file = File.Create(path);
                    if (!bitmap.Encode(file, SkiaSharp.SKEncodedImageFormat.Png, 100))
                        throw new ToolboxException($"第 {index + 1} 页编码失败，请检查输出目录。");
                }

                index++;
                progress?.Report(index * 100 / pageCount);
            }

            return $"{pageCount} 页 → {pageCount} 张 PNG · {Path.GetFileName(outputDirectory)}";
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex)
        {
            throw new ToolboxException($"PDF 转图片失败：{ExplainPdfError(ex)}");
        }
    }

    /// <summary>PDF 转 Word。keepLayout 为真时把每页渲染成图片嵌入 DOCX，版式不变但不可编辑。</summary>
    public static string PdfToWord(
        string input, string output, bool keepLayout, int dpi = 150, IProgress<int>? progress = null)
    {
        EnsureDistinct(new[] { input }, output);
        try
        {
            var pageCount = PageCount(input);
            if (keepLayout)
            {
                using var word = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
                var main = word.AddMainDocumentPart();
                var body = new W.Body();
                main.Document = new W.Document(body);

                using var pdfStream = File.OpenRead(input);
                for (var index = 0; index < pageCount; index++)
                {
                    using var bitmap = PDFtoImage.Conversion.ToImage(
                        pdfStream,
                        index,
                        leaveOpen: true,
                        password: null,
                        // 与 PdfToImages 及界面 / 说明文案一致：72 起步。曾钳到 96，
                        // 老师选 72 得到的实际是 96 的清晰度，与承诺不符（调研 P2-27）。
                        options: new PDFtoImage.RenderOptions { Dpi = Math.Clamp(dpi, 72, 300) });

                    using var png = new MemoryStream();
                    using (var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92))
                    {
                        data.SaveTo(png);
                    }

                    png.Position = 0;
                    var imagePart = main.AddImagePart(ImagePartType.Png);
                    imagePart.FeedData(png);
                    body.Append(CreateImageParagraph(main, imagePart, bitmap.Width, bitmap.Height, index + 1));
                    if (index < pageCount - 1) body.Append(PageBreakParagraph());

                    // 逐页上报：80 页扫描件按 300 DPI 保留版面要跑几分钟，
                    // 没有进度的话进度条恒为 0%，老师会以为死机并强关（审计 P2-30）。
                    progress?.Report((index + 1) * 100 / Math.Max(1, pageCount));
                }

                AppendSectionProperties(body);
                main.Document.Save();
                return $"{pageCount} 页 · 保留版面（图片页）→ {Path.GetFileName(output)}";
            }

            using var pdf = PigDocument.Open(input);
            var paragraphs = new List<string>();
            foreach (var page in pdf.GetPages())
            {
                string text;
                try { text = ContentOrderTextExtractor.GetText(page); }
                catch { text = page.Text; }

                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
                    .Split('\n')
                    .Select(line => line.TrimEnd())
                    .ToList();

                if (lines.All(string.IsNullOrWhiteSpace))
                {
                    paragraphs.Add("（本页未提取到文字，可能需要选择“保留版面”模式）");
                }
                else
                {
                    paragraphs.AddRange(lines.Where(line => !string.IsNullOrWhiteSpace(line)));
                }

                if (page.Number < pdf.NumberOfPages) paragraphs.Add("<<PAGEBREAK>>");
            }

            using (var word = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document))
            {
                var main = word.AddMainDocumentPart();
                var body = new W.Body();
                main.Document = new W.Document(body);

                foreach (var text in paragraphs)
                {
                    if (text == "<<PAGEBREAK>>") { body.Append(PageBreakParagraph()); continue; }
                    body.Append(CreateTextParagraph(text));
                }

                AppendSectionProperties(body);
                main.Document.Save();
            }

            return $"{pageCount} 页 · 可编辑文字 → {Path.GetFileName(output)}";
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex)
        {
            throw new ToolboxException($"PDF 转 Word 失败：{ExplainPdfError(ex)}");
        }
    }

    public static string WordToPdf(string input, string output)
    {
        EnsureDistinct(new[] { input }, output);

        // .doc 是旧二进制格式，OpenXml 读不了。给它一句明确的出路，
        // 而不是让底层抛一堆英文（审计 P2-26）。
        if (string.Equals(Path.GetExtension(input), ".doc", StringComparison.OrdinalIgnoreCase))
            throw new ToolboxException("这是旧版 Word 文档（.doc），请先用 Word 或 WPS 另存为 .docx 再转换。");

        try
        {
            using var word = WordprocessingDocument.Open(input, false);
            var body = word.MainDocumentPart?.Document?.Body
                ?? throw new ToolboxException("这个 DOCX 里没有正文内容。");

            var blocks = body.ChildElements.ToList();
            var font = CjkFont.Family;

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(style => style
                        .FontFamily(font)
                        .FontSize(10.5f)
                        .FontColor(Colors.Black)
                        .LineHeight(1.55f));

                    page.Footer()
                        .AlignRight()
                        .Text(text =>
                        {
                            text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Medium));
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });

                    page.Content().Column(column =>
                    {
                        column.Spacing(4);
                        foreach (var block in blocks)
                        {
                            switch (block)
                            {
                                case W.Paragraph paragraph:
                                    AppendParagraph(column, paragraph, word.MainDocumentPart);
                                    break;
                                case W.Table table:
                                    AppendTable(column, table);
                                    break;
                            }
                        }
                    });
                });
            }).GeneratePdf(output);

            return $"{Path.GetFileName(output)} · A4 · 已生成";
        }
        catch (ToolboxException) { throw; }
        catch (Exception ex)
        {
            throw new ToolboxException(ErrorText.Describe(ex, "Word 转 PDF"));
        }
    }

    // ----------------------------- DOCX 写入 -----------------------------

    private static void AppendSectionProperties(W.Body body)
    {
        body.Append(new W.SectionProperties(
            new W.PageSize { Width = 11906, Height = 16838 },
            new W.PageMargin
            {
                Top = 1134,
                Bottom = 1134,
                Left = 1134,
                Right = 1134,
                Header = 720,
                Footer = 720,
                Gutter = 0,
            }));
    }

    private static W.Paragraph CreateTextParagraph(string text)
    {
        var run = new W.Run(
            new W.RunProperties(
                new W.RunFonts { Ascii = "Microsoft YaHei", EastAsia = "微软雅黑", HighAnsi = "Microsoft YaHei" },
                new W.FontSize { Val = "22" }),
            new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });

        return new W.Paragraph(run);
    }

    private static W.Paragraph PageBreakParagraph() =>
        new(new W.Run(new W.Break { Type = W.BreakValues.Page }));

    private static W.Paragraph CreateImageParagraph(
        MainDocumentPart main,
        ImagePart imagePart,
        int pixelWidth,
        int pixelHeight,
        int pageNumber)
    {
        const long maxWidthEmu = 6_120_000;   // 17cm
        const long maxHeightEmu = 9_180_000;  // 25.5cm
        var aspect = pixelHeight / (double)Math.Max(1, pixelWidth);

        var width = maxWidthEmu;
        var height = (long)(width * aspect);
        if (height > maxHeightEmu)
        {
            height = maxHeightEmu;
            width = (long)(height / aspect);
        }

        var relationshipId = main.GetIdOfPart(imagePart);
        var imageName = $"page-{pageNumber}.png";

        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = (uint)pageNumber, Name = imageName },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = (uint)pageNumber, Name = imageName },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0, Y = 0 },
                                    new A.Extents { Cx = width, Cy = height }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                {
                                    Preset = A.ShapeTypeValues.Rectangle,
                                }))) {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                    })) {
                DistanceFromTop = 0,
                DistanceFromBottom = 0,
                DistanceFromLeft = 0,
                DistanceFromRight = 0,
            });

        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.Run(drawing));
    }

    // ----------------------------- DOCX 读取 -----------------------------

    private static void AppendParagraph(QuestPDF.Fluent.ColumnDescriptor column, W.Paragraph paragraph, MainDocumentPart? mainPart)
    {
        if (HasPageBreak(paragraph))
        {
            column.Item().PageBreak();
            return;
        }

        // 内嵌图片（PDF 转 Word 的“保留版面”模式）直接按原样放回 PDF。
        var drawing = paragraph.Descendants<W.Drawing>().FirstOrDefault();
        if (drawing is not null && mainPart is not null)
        {
            var relationshipId = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
            if (!string.IsNullOrEmpty(relationshipId) &&
                mainPart.TryGetPartById(relationshipId, out var part) &&
                part is ImagePart imagePart)
            {
                using var stream = imagePart.GetStream();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                column.Item().Image(memory.ToArray()).FitWidth();
                return;
            }
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
        var outlineLevel = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;

        var (size, bold) = ResolveParagraphStyle(styleId, outlineLevel);
        var justification = paragraph.ParagraphProperties?.Justification?.Val?.Value;

        var runs = paragraph.Descendants<W.Run>().ToList();
        if (runs.Count == 0 || runs.All(run => string.IsNullOrEmpty(RunText(run))))
        {
            column.Item().Height(size * 0.55f);
            return;
        }

        var container = column.Item();
        if (justification == W.JustificationValues.Center)
            container = container.AlignCenter();
        else if (justification == W.JustificationValues.Right || justification == W.JustificationValues.End)
            container = container.AlignRight();

        var hasText = false;
        container.Text(text =>
        {
            if (justification == W.JustificationValues.Both) text.Justify();

            foreach (var run in runs)
            {
                var content = RunText(run);
                if (string.IsNullOrEmpty(content)) continue;

                var span = text.Span(content);
                var properties = run.RunProperties;
                if (bold || IsOn(properties?.Bold)) span.Bold();
                if (IsOn(properties?.Italic)) span.Italic();
                if (properties?.Underline is not null) span.Underline();

                if (properties?.FontSize?.Val?.Value is { } halfPoints &&
                    double.TryParse(halfPoints, out var points) && points > 0)
                    span.FontSize((float)(points / 2.0));
                else
                    span.FontSize(size);

                hasText = true;
            }
        });

        if (hasText) return;
    }

    private static void AppendTable(QuestPDF.Fluent.ColumnDescriptor column, W.Table table)
    {
        var rows = table.Elements<W.TableRow>().ToList();
        if (rows.Count == 0) return;

        var columnCount = Math.Max(1, rows.Max(row => row.Elements<W.TableCell>().Count()));

        column.Item().Table(grid =>
        {
            grid.ColumnsDefinition(columns =>
            {
                for (var i = 0; i < columnCount; i++) columns.RelativeColumn();
            });

            foreach (var row in rows)
            {
                foreach (var cell in row.Elements<W.TableCell>())
                {
                    var text = string.Join("\n", cell.Elements<W.Paragraph>()
                        .Select(p => string.Concat(p.Descendants<W.Text>().Select(t => t.Text)))
                        .Where(t => !string.IsNullOrWhiteSpace(t)));

                    grid.Cell()
                        .Border(0.5f)
                        .BorderColor(Colors.Grey.Lighten2)
                        .Padding(4)
                        .Text(text)
                        .FontSize(9);
                }
            }
        });
    }

    private static (float Size, bool Bold) ResolveParagraphStyle(string styleId, int? outlineLevel)
    {
        var id = styleId.ToLowerInvariant();
        if (id.Contains("title") || id.Contains("标题"))
            return (22, true);
        if (id.Contains("heading1") || id.Contains("标题 1") || id.Contains("heading 1") || outlineLevel == 0)
            return (17, true);
        if (id.Contains("heading2") || id.Contains("标题 2") || id.Contains("heading 2") || outlineLevel == 1)
            return (14, true);
        if (id.Contains("heading3") || id.Contains("标题 3") || id.Contains("heading 3") || outlineLevel == 2)
            return (12, true);
        return (10.5f, false);
    }

    private static string RunText(W.Run run)
    {
        var pieces = new List<string>();
        foreach (var element in run.ChildElements)
        {
            switch (element)
            {
                case W.Text text: pieces.Add(text.Text); break;
                case W.TabChar: pieces.Add("\t"); break;
                case W.Break: pieces.Add("\n"); break;
            }
        }
        return string.Concat(pieces);
    }

    private static bool HasPageBreak(W.Paragraph paragraph) =>
        paragraph.Descendants<W.Break>().Any(b => b.Type?.Value == W.BreakValues.Page);

    private static bool IsOn(W.OnOffType? value) => value is not null && (value.Val is null || value.Val.Value);

    // ----------------------------- 工具方法 -----------------------------

    private static int PageCount(string path)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount;
        }
        catch (Exception ex)
        {
            throw new ToolboxException($"无法读取 PDF：{ExplainPdfError(ex)}");
        }
    }

    /// <summary>
    /// 供 <see cref="ToolDispatcher"/> 的输出保护使用：PDF 转图片要按页判断
    /// `-第N页.png` 是否已存在。与内部 <see cref="PageCount"/> 同一实现，
    /// 只多一层容错——页数读不出来时按 1 页处理，让后续渲染自己报准确错误。
    /// </summary>
    internal static int PageCountForGuard(string path)
    {
        try
        {
            return PageCount(path);
        }
        catch (ToolboxException)
        {
            return 1;
        }
    }

    private static (int Width, int Height) ReadPixelSize(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SkiaSharp.SKCodec.Create(stream)
            ?? throw new ToolboxException($"无法识别图片：{Path.GetFileName(path)}");
        return (codec.Info.Width, codec.Info.Height);
    }

    private static void EnsureDistinct(IReadOnlyList<string> inputs, string output)
    {
        var target = Path.GetFullPath(output);
        if (inputs.Any(file => string.Equals(Path.GetFullPath(file), target, StringComparison.OrdinalIgnoreCase)))
            throw new ToolboxException("输出文件不能覆盖原始文件，请换一个文件名。");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var clean = Regex.Replace(name, $"[{invalid}]", "_");
        return string.IsNullOrWhiteSpace(clean) ? "文档" : clean;
    }

    internal static string ExplainPdfError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            return "这份 PDF 已加密，请先解除密码保护。";
        if (message.Contains("not a PDF", StringComparison.OrdinalIgnoreCase))
            return "文件不是有效的 PDF。";
        return "文件无法读取，请确认文件未损坏、未加密后重试。";
    }
}
