using Homework.Core.Models;
using Homework.Core.Pages;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Homework.Core.Export;

/// <summary>一次导出执行的结果。</summary>
/// <param name="FilesWritten">本次真正写出的文件数（含索引工作簿）。</param>
/// <param name="StudentsWritten">涉及的学生数（按 StudentId 去重）。</param>
/// <param name="IndexPath">索引工作簿路径；非索引模式为 null。</param>
/// <param name="Warnings">规划阶段提示 + 执行阶段提示。</param>
public sealed record HomeworkExportResult(
    int FilesWritten,
    int StudentsWritten,
    string? IndexPath,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 导出执行器：把 <see cref="HomeworkExportPlanner"/> 算出的条目真正写到磁盘。
/// 只读导入来源，绝不改动 / 重命名 / 删除任何源文件；取消时只回收本次新建的文件。
/// </summary>
public static class HomeworkExporter
{
    public static HomeworkExportResult Execute(
        HomeworkBatch batch,
        HomeworkExportOptions options,
        HomeworkExportPlan plan,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        // 冲突优先：一条都不写，连输出目录都不创建。
        if (plan.Conflicts.Count > 0)
        {
            var sample = string.Join(Environment.NewLine, plan.Conflicts.Take(3));
            throw new HomeworkException($"{sample}{Environment.NewLine}请先处理冲突后再导出。");
        }

        var warnings = new List<string>(plan.Warnings);
        var created = new List<string>();
        var sources = batch.Pages.Select(p => p.SourcePath).ToList();
        var hasIndex = options.Mode == HomeworkExportMode.ArchiveWithIndex;
        var total = plan.Entries.Count + (hasIndex ? 1 : 0);
        var completed = 0;
        var written = 0;

        try
        {
            Directory.CreateDirectory(options.OutputDirectory);

            foreach (var entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(entry, batch, options, sources, created);
                written++;
                completed++;
                if (total > 0) progress?.Report(completed * 100 / total);
            }

            string? indexPath = null;
            if (hasIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(plan.IndexPath))
                    throw new HomeworkException("索引文件路径为空，无法写出索引工作簿。");

                var indexTarget = plan.IndexPath!;
                if (HomeworkPathGuard.IsBlockedTarget(indexTarget, sources))
                    throw new HomeworkException($"索引文件「{indexTarget}」是导入来源，禁止覆盖。");

                var indexExisted = File.Exists(indexTarget);
                if (indexExisted && !options.OverwriteExisting)
                    throw new HomeworkException($"索引文件「{indexTarget}」已存在，未开启覆盖时不能写出。");

                var indexDir = Path.GetDirectoryName(indexTarget);
                if (!string.IsNullOrEmpty(indexDir)) Directory.CreateDirectory(indexDir);
                HomeworkIndexWorkbook.Write(indexTarget, batch, plan);
                if (!indexExisted) created.Add(indexTarget);
                written++;
                indexPath = indexTarget;
                completed++;
                if (total > 0) progress?.Report(completed * 100 / total);
            }

            progress?.Report(100);

            var students = plan.Entries
                .Where(e => e.StudentId.HasValue)
                .Select(e => e.StudentId!.Value)
                .Distinct()
                .Count();

            return new HomeworkExportResult(written, students, indexPath, warnings);
        }
        catch (OperationCanceledException)
        {
            // 只回收本次新建的文件；已存在的目标即使被覆盖也不删。
            foreach (var file in created)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception)
                {
                    warnings.Add("取消后未能删除临时文件，请手动清理。");
                }
            }
            throw;
        }
    }

    private static void WriteEntry(
        HomeworkExportEntry entry,
        HomeworkBatch batch,
        HomeworkExportOptions options,
        IReadOnlyList<string> sources,
        List<string> created)
    {
        var target = entry.TargetPath;

        if (HomeworkPathGuard.IsBlockedTarget(target, sources))
            throw new HomeworkException($"目标「{target}」是导入来源，禁止覆盖。");

        var existed = File.Exists(target);
        if (existed && !options.OverwriteExisting)
            throw new HomeworkException($"目标「{target}」已存在，未开启覆盖时不能写出。");

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        switch (entry.Kind)
        {
            case HomeworkExportEntryKind.ImageCopy:
            case HomeworkExportEntryKind.FolderCopy:
                // 图片字节原样复制，绝不重新编码。
                File.Copy(entry.SourcePath, target, options.OverwriteExisting);
                break;

            case HomeworkExportEntryKind.PdfPageExtract:
                ExtractPdfPage(entry.SourcePath, entry.SourcePageNumber, target);
                break;

            case HomeworkExportEntryKind.MergedPdf:
                WriteMergedPdf(entry, batch, target);
                break;

            default:
                throw new HomeworkException($"未知的导出类型：{entry.Kind}。");
        }

        if (!existed) created.Add(target);
    }

    private static void ExtractPdfPage(string source, int pageNumber, string target)
    {
        using var src = PdfReader.Open(source, PdfDocumentOpenMode.Import);
        if (pageNumber < 1 || pageNumber > src.PageCount)
            throw new HomeworkException(
                $"PDF「{Path.GetFileName(source)}」没有第 {pageNumber} 页，无法提取。");

        using var doc = new PdfDocument();
        doc.AddPage(src.Pages[pageNumber - 1]);
        doc.Save(target);
    }

    private static void WriteMergedPdf(HomeworkExportEntry entry, HomeworkBatch batch, string target)
    {
        if (!entry.StudentId.HasValue)
            throw new HomeworkException("合并条目缺少学生信息，无法导出。");

        var pages = batch.PagesOf(entry.StudentId.Value).OrderBy(p => p.Index).ToList();
        using var doc = new PdfDocument();
        // PdfSharp 可能把图片数据的读取推迟到 Save；图片流必须活到保存结束，提前 Dispose 会抛 ObjectDisposedException。
        var keepAlive = new List<IDisposable>();

        try
        {
            foreach (var page in pages)
            {
                if (page.Kind == HomeworkPageKind.Image)
                {
                    // 走与预览相同的 EXIF 摆正解码：XImage.FromFile 不认 EXIF，手机竖拍会横着进 PDF。
                    using var decoded = PageImageDecoder.DecodePreview(page.SourcePath, 4000);
                    var w = Math.Max(1, decoded.Width);
                    var h = Math.Max(1, decoded.Height);
                    var pdfPage = doc.AddPage();
                    // 先定方向再定尺寸：无论 Orientation 的 setter 是否交换宽高，最终尺寸都精确等于像素点数。
                    pdfPage.Orientation = w > h ? PageOrientation.Landscape : PageOrientation.Portrait;
                    pdfPage.Width = XUnit.FromPoint(w);
                    pdfPage.Height = XUnit.FromPoint(h);

                    var png = new MemoryStream();
                    decoded.Bitmap.Encode(png, SkiaSharp.SKEncodedImageFormat.Png, 100);
                    png.Position = 0;
                    keepAlive.Add(png);
                    var image = XImage.FromStream(png);
                    keepAlive.Add(image);
                    using var gfx = XGraphics.FromPdfPage(pdfPage);
                    gfx.DrawImage(image, 0, 0, pdfPage.Width.Point, pdfPage.Height.Point);
                }
                else
                {
                    using var src = PdfReader.Open(page.SourcePath, PdfDocumentOpenMode.Import);
                    if (page.SourcePageNumber < 1 || page.SourcePageNumber > src.PageCount)
                        throw new HomeworkException(
                            $"PDF「{Path.GetFileName(page.SourcePath)}」没有第 {page.SourcePageNumber} 页，无法合并。");
                    doc.AddPage(src.Pages[page.SourcePageNumber - 1]);
                }
            }

            doc.Save(target);
        }
        finally
        {
            foreach (var item in keepAlive) item.Dispose();
        }
    }
}
