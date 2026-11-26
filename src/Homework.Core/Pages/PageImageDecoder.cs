using System.Security.Cryptography;
using Homework.Core.Models;
using SkiaSharp;
using TeachCraft.Shared;

namespace Homework.Core.Pages;

/// <summary>一页预览解码结果。<see cref="Width"/> / <see cref="Height"/> 已按 EXIF 摆正。</summary>
public sealed record DecodedPageImage(SKBitmap Bitmap, int Width, int Height, string ContentHash) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// 图片预览解码：按目标边长缩放解码（绝不把几千万像素的照片整张解出来），
/// 再按 EXIF 方向摆正。失败一律抛中文 <see cref="HomeworkException"/>。
/// </summary>
public static class PageImageDecoder
{
    /// <summary>内容哈希所用的灰度缩略图边长。</summary>
    private const int HashEdge = 64;

    /// <summary>
    /// 缩放解码一张图片。maxEdge 限制解码后较长的一边（EXIF 摆正前按原始比例缩放，
    /// 摆正后长边仍不超过 maxEdge）。
    /// </summary>
    public static DecodedPageImage DecodePreview(string path, int maxEdge)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new HomeworkException($"找不到图片：{Path.GetFileName(path)}");
        if (maxEdge < 1) maxEdge = 1;

        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream)
                ?? throw new HomeworkException($"无法识别 {Path.GetFileName(path)} 的图片格式。");

            var srcW = codec.Info.Width;
            var srcH = codec.Info.Height;
            if (srcW < 1 || srcH < 1)
                throw new HomeworkException($"{Path.GetFileName(path)} 的图片尺寸无效。");

            // 关键：把目标尺寸交给 SKImageInfo，SKCodec 会在解码阶段直接缩放，
            // 而不是先解出全尺寸再缩小——后者对手机照片会瞬间吃掉几百 MB。
            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(srcW, srcH));
            var outW = Math.Max(1, (int)Math.Round(srcW * scale));
            var outH = Math.Max(1, (int)Math.Round(srcH * scale));

            var info = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            SKCodecResult result;
            try
            {
                result = codec.GetPixels(info, bitmap.GetPixels());
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }

            if (result == SKCodecResult.InvalidScale)
            {
                bitmap.Dispose();
                bitmap = DecodeThenDownscale(codec, srcW, srcH, outW, outH);
            }
            else if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                bitmap.Dispose();
                throw new HomeworkException($"读取 {Path.GetFileName(path)} 失败（{result}）。");
            }

            var oriented = ApplyOrientation(bitmap, codec.EncodedOrigin);
            var hash = ComputeContentHash(oriented);
            return new DecodedPageImage(oriented, oriented.Width, oriented.Height, hash);
        }
        catch (HomeworkException) { throw; }
        catch (Exception ex)
        {
            throw new HomeworkException(ErrorText.Describe(ex, "读取图片"), ex);
        }
    }

    /// <summary>
    /// 为一页作业解码出用于填涂识别的位图：图片页走 <see cref="DecodePreview"/>；
    /// PDF 页按给定 DPI 渲染 <c>SourcePageNumber - 1</c>（0 基），再按 maxEdge 限制长边。
    /// PDF 页没有内容哈希（返回空串），识别不依赖去重提示。中间位图一律释放。
    /// </summary>
    public static DecodedPageImage DecodePage(HomeworkPage page, int maxEdge, int pdfDpi)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Kind != HomeworkPageKind.PdfPage)
            return DecodePreview(page.SourcePath, maxEdge);

        if (string.IsNullOrWhiteSpace(page.SourcePath) || !File.Exists(page.SourcePath))
            throw new HomeworkException($"找不到 PDF：{Path.GetFileName(page.SourcePath)}");

        try
        {
            using var stream = File.OpenRead(page.SourcePath);
            var pageIndex = Math.Max(0, page.SourcePageNumber - 1);
            var rendered = RenderPdfPage(stream, pageIndex, new PDFtoImage.RenderOptions { Dpi = pdfDpi });
            try
            {
                var capped = CapLongEdge(rendered, maxEdge);
                if (ReferenceEquals(capped, rendered))
                    return new DecodedPageImage(capped, capped.Width, capped.Height, string.Empty);
                rendered.Dispose();
                return new DecodedPageImage(capped, capped.Width, capped.Height, string.Empty);
            }
            catch
            {
                rendered.Dispose();
                throw;
            }
        }
        catch (HomeworkException) { throw; }
        catch (Exception ex)
        {
            throw new HomeworkException(ErrorText.Describe(ex, "读取 PDF 页"), ex);
        }
    }

    /// <summary>渲染结果的长边护栏；返回新位图时不释放原位图，由调用方负责。</summary>
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
            using (var image = SKImage.FromBitmap(bitmap))
            {
                canvas.DrawImage(image,
                    new SKRect(0, 0, bitmap.Width, bitmap.Height),
                    new SKRect(0, 0, w, h),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static SKBitmap DecodeThenDownscale(SKCodec codec, int srcW, int srcH, int outW, int outH)
    {
        var fullInfo = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var full = new SKBitmap(fullInfo);
        var result = codec.GetPixels(fullInfo, full.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new HomeworkException($"读取图片失败（{result}）。");

        var target = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(full))
        {
            canvas.DrawImage(image,
                new SKRect(0, 0, srcW, srcH),
                new SKRect(0, 0, outW, outH),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        return target;
    }

    /// <summary>
    /// 64×64 灰度缩略图的 SHA-256（小写十六进制）。用于「内容相同的页」提示，
    /// 不参与去重判定。算不出来时返回空串，绝不因此阻断导入。
    /// </summary>
    public static string ComputeContentHash(SKBitmap bitmap)
    {
        try
        {
            using var gray = new SKBitmap(HashEdge, HashEdge, SKColorType.Gray8, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(gray))
            {
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(bitmap);
                canvas.DrawImage(image,
                    new SKRect(0, 0, bitmap.Width, bitmap.Height),
                    new SKRect(0, 0, HashEdge, HashEdge),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }

            var bytes = gray.Bytes;
            if (bytes is null || bytes.Length == 0) return string.Empty;
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>按 EXIF 方向把手机拍的照片摆正；返回新位图并释放原位图。</summary>
    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return source;

        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = rotated ? source.Height : source.Width;
        var height = rotated ? source.Width : source.Height;

        var target = new SKBitmap(width, height);
        try
        {
            using (var canvas = new SKCanvas(target))
            {
                switch (origin)
                {
                    case SKEncodedOrigin.TopRight:
                        canvas.Translate(width, 0);
                        canvas.Scale(-1, 1);
                        break;
                    case SKEncodedOrigin.BottomRight:
                        canvas.Translate(width, height);
                        canvas.RotateDegrees(180);
                        break;
                    case SKEncodedOrigin.BottomLeft:
                        canvas.Translate(0, height);
                        canvas.Scale(1, -1);
                        break;
                    case SKEncodedOrigin.RightTop:
                        canvas.Translate(width, 0);
                        canvas.RotateDegrees(90);
                        break;
                    case SKEncodedOrigin.LeftBottom:
                        canvas.Translate(0, height);
                        canvas.RotateDegrees(270);
                        break;
                    case SKEncodedOrigin.LeftTop:
                        canvas.Translate(width, 0);
                        canvas.RotateDegrees(90);
                        canvas.Scale(1, -1);
                        break;
                    case SKEncodedOrigin.RightBottom:
                        canvas.Translate(0, height);
                        canvas.RotateDegrees(270);
                        canvas.Scale(1, -1);
                        break;
                }

                canvas.DrawBitmap(source, 0, 0);
            }

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
        finally
        {
            source.Dispose();
        }
    }

    internal static SKBitmap RenderPdfPage(Stream stream, int pageIndex, PDFtoImage.RenderOptions options)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return PDFtoImage.Conversion.ToImage(stream, pageIndex, leaveOpen: true, password: null, options: options);
        throw new PlatformNotSupportedException("当前系统不支持 PDF 页面渲染。");
    }
}
