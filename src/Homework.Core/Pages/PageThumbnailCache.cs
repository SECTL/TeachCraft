using Homework.Core.Models;
using SkiaSharp;

namespace Homework.Core.Pages;

/// <summary>
/// 一次导入会话的缩略图缓存。所有 PNG 落在系统临时目录下的独立会话文件夹，
/// <see cref="Dispose"/> 时整夹删除；<see cref="CleanStale"/> 清理上次崩溃遗留的旧夹。
/// 缓存只服务于本次会话，绝不写进仓库或上传。
/// </summary>
public sealed class PageThumbnailCache : IDisposable
{
    /// <summary>缓存根目录，供清理与测试定位。</summary>
    public static readonly string CacheRoot =
        Path.Combine(Path.GetTempPath(), "TeachCraft", "Homework", "cache");

    private readonly string _sessionDir;
    private bool _disposed;

    public PageThumbnailCache()
    {
        _sessionDir = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDir);
    }

    /// <summary>本次会话的文件夹绝对路径。</summary>
    public string SessionDirectory => _sessionDir;

    /// <summary>把一页预览写成 PNG，返回其绝对路径。</summary>
    public string SaveThumbnail(string sourcePath, int pageNumber, SKBitmap preview)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(preview);

        var stem = Sanitize(Path.GetFileNameWithoutExtension(sourcePath ?? string.Empty));
        if (string.IsNullOrEmpty(stem)) stem = "page";
        var name = $"{stem}_p{Math.Max(1, pageNumber)}_{Guid.NewGuid():N}.png";
        var path = Path.Combine(_sessionDir, name);

        using var stream = File.Create(path);
        if (!preview.Encode(stream, SKEncodedImageFormat.Png, 100))
            throw new HomeworkException("缩略图保存失败，请检查临时目录权限。");
        return path;
    }

    private string PreviewDirectory => Path.Combine(_sessionDir, "preview");

    /// <summary>保存按原始分辨率渲染的高清预览，返回路径；与 1600px 缩略图分目录存放。</summary>
    public string SavePreview(HomeworkPage page, SKBitmap bitmap, int edge)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(page);

        Directory.CreateDirectory(PreviewDirectory);
        var stem = Sanitize(Path.GetFileNameWithoutExtension(page.SourcePath));
        if (string.IsNullOrEmpty(stem)) stem = "page";
        var name = $"{stem}_p{Math.Max(1, page.SourcePageNumber)}_hi{Math.Max(1, edge)}_{Guid.NewGuid():N}.png";
        var path = Path.Combine(PreviewDirectory, name);

        using var stream = File.Create(path);
        if (!bitmap.Encode(stream, SKEncodedImageFormat.Png, 100))
            throw new HomeworkException("高清预览保存失败，请检查临时目录权限。");
        return path;
    }

    /// <summary>查找已渲染的高清预览；找不到返回空串。</summary>
    public string? FindPreview(HomeworkPage page, int edge)
    {
        if (_disposed) return null;
        try
        {
            var stem = Sanitize(Path.GetFileNameWithoutExtension(page.SourcePath));
            if (string.IsNullOrEmpty(stem)) stem = "page";
            var prefix = $"{stem}_p{Math.Max(1, page.SourcePageNumber)}_hi{Math.Max(1, edge)}_";
            return Directory.EnumerateFiles(PreviewDirectory, prefix + "*.png").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TryDelete(_sessionDir);
    }

    /// <summary>删除最后写入时间早于 cutoff 的旧会话夹；best effort，不抛异常。</summary>
    public static void CleanStale(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var dir in Directory.EnumerateDirectories(CacheRoot))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LastWriteTimeUtc < cutoff) info.Delete(recursive: true);
                }
                catch
                {
                    // 单个夹删不掉（被占用等）不影响其它夹。
                }
            }
        }
        catch
        {
            // 根目录不存在或无权限时静默返回。
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 会话结束时的清理是尽力而为，失败也不影响主流程。
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
