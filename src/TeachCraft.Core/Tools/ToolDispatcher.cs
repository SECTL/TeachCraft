namespace TeachCraft.Core.Tools;

public enum ToolKind
{
    ImageConvert,
    ImageResize,
    ImageCompress,
    ImageWatermark,
    PdfToWord,
    WordToPdf,
    ImagesToPdf,
    PdfToImages,
    PdfMerge,
    PdfSplit,
    RosterCompare,
    ExcelClassSplit,
    ExamPaperLayout,
    CertificateBatch,
}

/// <summary>一次工具运行所需的全部输入与选项。</summary>
public sealed class ToolRequest
{
    public ToolKind Kind { get; init; }
    /// <summary>有序输入；名单核对固定为第 1 份应交名单、第 2 份已交名单。</summary>
    public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();
    public string Output { get; init; } = string.Empty;

    public string? Format { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Percent { get; init; }
    public bool KeepAspect { get; init; } = true;

    public int Quality { get; init; } = 85;
    public int? MaxEdge { get; init; }

    public string? WatermarkText { get; init; }
    public WatermarkPosition WatermarkPosition { get; init; } = WatermarkPosition.BottomRight;
    public double WatermarkOpacity { get; init; } = 0.8;
    public double WatermarkScale { get; init; } = 1.0;

    public string? PageRange { get; init; }
    public bool KeepLayout { get; init; }
    public int Dpi { get; init; } = 150;
    public string LayoutMode { get; init; } = "two-up";
    public int ExamPaperDpi { get; init; } = 600;
    public string? CertificateWorksheet { get; init; }
    public int CertificateHeaderRow { get; init; } = 1;
    public int CertificateNameColumn { get; init; } = -1;
    public int CertificateAwardColumn { get; init; } = -1;
    public CertificateTextPlacement CertificatePlacement { get; init; } = CertificateTextPlacement.Default;
}

public static class ToolDispatcher
{
    public static string Run(ToolRequest request, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Inputs.Count == 0)
            throw new ToolboxException("请先选择要处理的文件。");
        if (string.IsNullOrWhiteSpace(request.Output))
            throw new ToolboxException("请先选择输出位置。");
        if (request.Kind == ToolKind.CertificateBatch && request.Inputs.Count != 2)
            throw new ToolboxException("奖状生成需要一张底图和一份 Excel 名单。");

        foreach (var input in request.Inputs)
        {
            if (!File.Exists(input))
                throw new ToolboxException($"找不到文件：{Path.GetFileName(input)}");
        }

        // 输出保护只在这一层做一次，覆盖全部工具。
        // 各工具的实现不再各写一套——曾因只有 PdfToolbox 写了 EnsureDistinct、
        // 图片四件与 PDF 转图片没写，老师可以在保存框里选中原图把原件覆盖掉。
        EnsureOutputDoesNotOverwriteInput(request);

        var first = request.Inputs[0];
        return request.Kind switch
        {
            ToolKind.ImageConvert => ImageToolbox.Convert(first, request.Output, request.Format ?? "png", request.Quality),
            ToolKind.ImageResize => ImageToolbox.Resize(
                first, request.Output, request.Width, request.Height, request.Percent, request.KeepAspect),
            ToolKind.ImageCompress => RunCompress(request),
            ToolKind.ImageWatermark => ImageToolbox.Watermark(
                first,
                request.Output,
                request.WatermarkText ?? string.Empty,
                request.WatermarkPosition,
                request.WatermarkOpacity,
                request.WatermarkScale),
            ToolKind.PdfToWord => PdfToolbox.PdfToWord(
                first, request.Output, request.KeepLayout, request.Dpi, progress),
            ToolKind.WordToPdf => PdfToolbox.WordToPdf(first, request.Output),
            ToolKind.ImagesToPdf => PdfToolbox.ImagesToPdf(request.Inputs, request.Output, progress),
            ToolKind.PdfToImages => PdfToolbox.PdfToImages(first, request.Output, request.Dpi, progress),
            ToolKind.PdfMerge => PdfToolbox.Merge(request.Inputs, request.Output),
            ToolKind.PdfSplit => PdfToolbox.Split(first, request.Output, request.PageRange ?? string.Empty),
            ToolKind.RosterCompare => RosterComparer.WriteReport(request.Inputs, request.Output),
            ToolKind.ExcelClassSplit => ExcelClassSplitter.Split(first, request.Output),
            ToolKind.ExamPaperLayout => ExamPaperLayoutToolbox.Layout(first, request.Output,
                request.LayoutMode == "booklet" ? ExamPaperLayoutMode.Booklet : ExamPaperLayoutMode.TwoUp,
                request.ExamPaperDpi),
            ToolKind.CertificateBatch => CertificateBatchToolbox.Generate(new CertificateBatchRequest(
                request.Inputs[0], request.Inputs[1], request.CertificateWorksheet ?? string.Empty,
                request.CertificateHeaderRow, request.CertificateNameColumn, request.CertificateAwardColumn,
                request.CertificatePlacement, request.Output), progress, cancellationToken),
            _ => throw new ToolboxException("未知的工具类型。"),
        };
    }

    private static string RunCompress(ToolRequest request)
    {
        var summary = ImageToolbox.Compress(
            request.Inputs[0], request.Output, request.Quality, request.MaxEdge, out var before);
        _ = before;
        return summary;
    }

    /// <summary>
    /// 输出不得覆盖任何输入文件。
    ///
    /// 两类语义要分开：绝大部分工具把 <see cref="ToolRequest.Output"/> 当**文件路径**，
    /// 同路径即拒绝；<see cref="ToolKind.PdfToImages"/> 把它当**目录**，写的是
    /// `<源文件基名>-第N页.png` 新文件——目录与输入所在目录相同是合法的，但只要那些
    /// 目标文件名已存在，就会被 File.Create 静默截断（P2-12），所以对目录形态检查
    /// 「将要写出的具体文件名」而不是目录本身。
    /// </summary>
    private static void EnsureOutputDoesNotOverwriteInput(ToolRequest request)
    {
        if (request.Kind == ToolKind.PdfToImages)
        {
            EnsurePdfToImagesTargetsFree(request.Inputs[0], request.Output);
            return;
        }

        var target = Path.GetFullPath(request.Output);
        foreach (var input in request.Inputs)
        {
            if (string.Equals(Path.GetFullPath(input), target, StringComparison.OrdinalIgnoreCase))
                throw new ToolboxException("输出文件不能覆盖原始文件，请换一个文件名。");
        }
    }

    /// <summary>PDF 转图片：输出目录允许与源 PDF 同目录，但 `-第N页.png` 不得已存在。</summary>
    private static void EnsurePdfToImagesTargetsFree(string input, string outputDirectory)
    {
        var baseName = Path.GetFileNameWithoutExtension(input);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "文档";

        var pageCount = PdfToolbox.PageCountForGuard(input);
        for (var page = 1; page <= pageCount; page++)
        {
            var candidate = Path.Combine(outputDirectory, $"{baseName}-第{page}页.png");
            if (File.Exists(candidate))
                throw new ToolboxException(
                    $"输出文件夹里已有「{Path.GetFileName(candidate)}」，继续会覆盖它，请换一个文件夹。");
        }
    }
}
