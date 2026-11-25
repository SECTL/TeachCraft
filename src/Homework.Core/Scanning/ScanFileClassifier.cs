namespace Homework.Core.Scanning;

public enum ScanFileKind
{
    Unknown,
    Image,
    Pdf,
}

/// <summary>
/// 判断收件文件夹里的文件是否值得导入。只接受作业整理已有的图片格式与 PDF；
/// 扫描仪写入中的临时文件、系统缩略图与 Office 锁文件一律跳过。
/// </summary>
public static class ScanFileClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp",
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
    };

    private static readonly string[] TemporarySuffixes =
    [
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download",
    ];

    public static ScanFileKind KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ScanFileKind.Unknown;
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return ScanFileKind.Image;
        if (PdfExtensions.Contains(extension)) return ScanFileKind.Pdf;
        return ScanFileKind.Unknown;
    }

    public static bool IsSupported(string? path) => KindOf(path) != ScanFileKind.Unknown;

    /// <summary>
    /// 临时文件判定：以 ~$ / ~ 开头、以 . 开头的隐藏文件、常见下载后缀，
    /// 以及 Windows / macOS 自己写的目录元数据。
    /// </summary>
    public static bool IsTemporary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.StartsWith("~", StringComparison.Ordinal)) return true;
        if (name.StartsWith(".", StringComparison.Ordinal)) return true;

        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var suffix in TemporarySuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>可以直接导入的文件：格式受支持且不是临时文件。</summary>
    public static bool IsImportable(string? path) => !IsTemporary(path) && IsSupported(path);
}
