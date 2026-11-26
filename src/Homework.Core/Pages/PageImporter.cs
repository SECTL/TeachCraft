using Homework.Core.Models;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using TeachCraft.Shared;

namespace Homework.Core.Pages;

/// <summary>
/// 把图片 / PDF 导入批次。返回本次加入的页 Id（字符串），逐条提示通过 <paramref name="warnings"/> 带出。
/// 两条路径都只读源文件，绝不改动或覆盖它们。
/// </summary>
public static class PageImporter
{
    /// <summary>图片预览解码的较长边上限。</summary>
    private const int ImagePreviewEdge = 1600;
    /// <summary>PDF 预览渲染的较长边上限。</summary>
    private const int PdfPreviewEdge = 1600;
    /// <summary>PDF 渲染 DPI 上限。</summary>
    private const int MaxPdfDpi = 150;

    /// <summary>按指定边长从原始文件渲染高清预览（图片按原图、PDF 按页），不重采样已缩小的缩略图。</summary>
    public static string RenderPreview(
        HomeworkPage page,
        PageThumbnailCache cache,
        int edge,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cache);
        var target = Math.Clamp(edge, 256, 6000);
        token.ThrowIfCancellationRequested();

        using var decoded = page.Kind == HomeworkPageKind.PdfPage
            ? PageImageDecoder.DecodePage(page, target, MaxPdfDpi)
            : PageImageDecoder.DecodePreview(page.SourcePath, target);
        token.ThrowIfCancellationRequested();
        return cache.SavePreview(page, decoded.Bitmap, target);
    }

    /// <summary>
    /// 导入一组图片。按文件名的自然顺序（IMG_2 在 IMG_10 之前）处理，不依赖选择器返回顺序。
    /// 单张失败只记警告并继续；重复来源 + 页码跳过并警告；取消时保留已完成的页。
    /// </summary>
    public static IReadOnlyList<string> ImportImages(
        HomeworkBatch batch,
        IReadOnlyList<string> paths,
        PageThumbnailCache cache,
        IProgress<int>? progress,
        CancellationToken token,
        out IReadOnlyList<string> warnings)
    {
        var warns = new List<string>();
        warnings = warns;
        var added = new List<string>();
        if (paths is null || paths.Count == 0) return added;

        var ordered = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .OrderBy(p => Path.GetFileName(p), NaturalPathComparer.Instance)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                warns.Add("已取消导入，后续图片未处理。");
                break;
            }

            var path = ordered[i];
            var name = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path))
                {
                    warns.Add($"找不到文件：{name}，已跳过。");
                    continue;
                }

                using var decoded = PageImageDecoder.DecodePreview(path, ImagePreviewEdge);
                var ok = batch.TryAddPage(
                    path, HomeworkPageKind.Image, 1, decoded.ContentHash,
                    decoded.Width, decoded.Height, out var page, out var error);
                if (!ok)
                {
                    warns.Add($"{name}：{error}");
                    continue;
                }

                cache.SaveThumbnail(path, 1, decoded.Bitmap);
                added.Add(page!.Id.ToString());
            }
            catch (HomeworkException ex)
            {
                warns.Add($"{name}：{ErrorText.Describe(ex, "读取图片")}");
            }
            catch (Exception ex)
            {
                warns.Add($"{name}：{ErrorText.Describe(ex, "读取图片")}");
            }

            progress?.Report((i + 1) * 100 / ordered.Count);
        }

        return added;
    }

    /// <summary>
    /// 导入一份 PDF，每页加入一批 <see cref="HomeworkPageKind.PdfPage"/>。
    /// 单份文件原子：取消或某页渲染失败时，用批次快照 + Restore 把这份文件加入的页全部撤回
    /// （删除的页仍留在批次内可恢复）；加密 / 损坏的 PDF 抛中文 <see cref="HomeworkException"/>。
    /// </summary>
    public static IReadOnlyList<string> ImportPdf(
        HomeworkBatch batch,
        string path,
        PageThumbnailCache cache,
        int previewDpi,
        IProgress<int>? progress,
        CancellationToken token,
        out IReadOnlyList<string> warnings)
    {
        var warns = new List<string>();
        warnings = warns;
        var added = new List<string>();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            warns.Add($"找不到文件：{Path.GetFileName(path ?? string.Empty)}");
            return added;
        }

        var snapshot = batch.Capture();
        var savedThumbs = new List<string>();

        void Rollback()
        {
            batch.Restore(snapshot);
            foreach (var thumb in savedThumbs)
            {
                try { if (File.Exists(thumb)) File.Delete(thumb); }
                catch { /* 临时文件清理失败不影响主流程 */ }
            }
            savedThumbs.Clear();
        }

        // 先用 PdfSharp 读页数与页面尺寸：加密 / 损坏在这一步就会被拦下，
        // 与「某一页渲染失败」区分开——前者抛异常，后者只回滚并记警告。
        int pageCount;
        List<double> maxPoints;
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            // PdfReaderOptions.IssuesWithDecryption 默认 SilentlyIgnore，加密文档未必抛异常，
            // 因此显式检查，确保加密 PDF 一定得到中文提示而不是半截渲染结果。
            if (document.SecuritySettings.IsEncrypted)
                throw new HomeworkException("这份 PDF 已加密，请先解除密码保护。");
            pageCount = document.PageCount;
            maxPoints = Enumerable.Range(0, pageCount)
                .Select(i => (double)Math.Max(document.Pages[i].Width.Point, document.Pages[i].Height.Point))
                .ToList();
        }
        catch (HomeworkException) { throw; }
        catch (Exception ex)
        {
            throw new HomeworkException($"无法读取 PDF：{ExplainPdfError(ex)}", ex);
        }

        if (pageCount == 0)
        {
            warns.Add("这份 PDF 没有页面。");
            return added;
        }

        try
        {
            using var stream = File.OpenRead(path);
            for (var i = 0; i < pageCount; i++)
            {
                if (token.IsCancellationRequested)
                {
                    Rollback();
                    warns.Add("已取消导入，这份 PDF 的页面已全部移除。");
                    return Array.Empty<string>();
                }

                var pageNumber = i + 1;
                SKBitmap? bitmap = null;
                try
                {
                    var dpi = ComputePreviewDpi(previewDpi, maxPoints[i]);
                    bitmap = PDFtoImage.Conversion.ToImage(
                        stream, i, leaveOpen: true, password: null,
                        options: new PDFtoImage.RenderOptions { Dpi = dpi });
                    bitmap = CapLongEdge(bitmap, PdfPreviewEdge);

                    var hash = PageImageDecoder.ComputeContentHash(bitmap);
                    var ok = batch.TryAddPage(
                        path, HomeworkPageKind.PdfPage, pageNumber, hash,
                        bitmap.Width, bitmap.Height, out var page, out var error);
                    if (!ok)
                    {
                        warns.Add($"第 {pageNumber} 页未加入：{error}");
                    }
                    else
                    {
                        added.Add(page!.Id.ToString());
                        savedThumbs.Add(cache.SaveThumbnail(path, pageNumber, bitmap));
                    }
                }
                finally
                {
                    bitmap?.Dispose();
                }

                progress?.Report((i + 1) * 100 / pageCount);
            }

            return added;
        }
        catch (OperationCanceledException)
        {
            Rollback();
            warns.Add("已取消导入，这份 PDF 的页面已全部移除。");
            return Array.Empty<string>();
        }
        catch (HomeworkException)
        {
            Rollback();
            throw;
        }
        catch (Exception ex)
        {
            Rollback();
            warns.Add($"{ErrorText.Describe(ex, "渲染 PDF")}；这份 PDF 的页面已全部移除。");
            return Array.Empty<string>();
        }
    }

    /// <summary>按页面物理尺寸把 DPI 压到长边不超过上限，且不超过 150。</summary>
    private static int ComputePreviewDpi(int requestedDpi, double maxPoint)
    {
        var baseDpi = Math.Clamp(requestedDpi, 24, MaxPdfDpi);
        var byEdge = maxPoint > 0 ? PdfPreviewEdge * 72.0 / maxPoint : baseDpi;
        return (int)Math.Clamp(Math.Min(baseDpi, byEdge), 12, MaxPdfDpi);
    }

    /// <summary>渲染结果的最后一道长边护栏，防止 DPI 换算的舍入误差放大。</summary>
    private static SKBitmap CapLongEdge(SKBitmap bitmap, int maxEdge)
    {
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= maxEdge) return bitmap;

        var scale = (double)maxEdge / longest;
        var w = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var h = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var target = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using (var canvas = new SKCanvas(target))
            {
                using var image = SKImage.FromBitmap(bitmap);
                canvas.DrawImage(image,
                    new SKRect(0, 0, bitmap.Width, bitmap.Height),
                    new SKRect(0, 0, w, h),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
            bitmap.Dispose();
            return target;
        }
        catch
        {
            target.Dispose();
            return bitmap; // 原图仍有效，交给调用方释放。
        }
    }

    private static string ExplainPdfError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
            ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return "这份 PDF 已加密，请先解除密码保护。";
        return "文件不是有效的 PDF，或已损坏无法读取。";
    }

    /// <summary>数字感知的文件名比较：IMG_2 排在 IMG_10 之前。</summary>
    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static readonly NaturalPathComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var i = 0;
            var j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var nx = x.AsSpan(si, i - si).TrimStart('0');
                    var ny = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (nx.Length != ny.Length) return nx.Length.CompareTo(ny.Length);
                    var c = nx.SequenceCompareTo(ny);
                    if (c != 0) return c;
                }
                else
                {
                    var cx = char.ToUpperInvariant(x[i]);
                    var cy = char.ToUpperInvariant(y[j]);
                    if (cx != cy) return cx.CompareTo(cy);
                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
