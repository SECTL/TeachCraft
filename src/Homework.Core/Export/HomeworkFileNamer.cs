namespace Homework.Core.Export;

/// <summary>
/// 作业成品的文件名 / 文件夹名生成。所有片段都过 Sanitize，保证落到磁盘的名字安全。
/// </summary>
public static class HomeworkFileNamer
{
    private const int MaxLength = 120;
    private const string Fallback = "未命名";

    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    /// <summary>
    /// 把任意文本变成安全的文件名片段：非法字符替换为 _，去掉结尾的点与空格，
    /// Windows 保留设备名加前导 _，空串回落到「未命名」，最长 120 字符。
    /// </summary>
    public static string Sanitize(string value, string fallback = Fallback)
    {
        var text = value ?? string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        text = builder.ToString();

        text = text.TrimEnd('.', ' ');

        var dot = text.IndexOf('.');
        var stem = dot >= 0 ? text[..dot] : text;
        if (ReservedNames.Contains(stem)) text = "_" + text;

        if (text.Length == 0) text = string.IsNullOrEmpty(fallback) ? Fallback : fallback;

        if (text.Length > MaxLength) text = text[..MaxLength];
        text = text.TrimEnd('.', ' ');
        if (text.Length == 0) text = string.IsNullOrEmpty(fallback) ? Fallback : fallback;

        return text;
    }

    /// <summary>班级_学号_姓名；学号为空时省略，不产生双下划线。</summary>
    public static string StudentStem(string className, string number, string name)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(className)) parts.Add(Sanitize(className));
        if (!string.IsNullOrWhiteSpace(number)) parts.Add(Sanitize(number));
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(Sanitize(name));
        return parts.Count == 0 ? Fallback : string.Join("_", parts);
    }

    /// <summary>第{n}页。</summary>
    public static string PageSuffix(int pageNumber) => $"第{pageNumber}页";

    /// <summary>班级_学号_姓名_第N页.ext；扩展名转小写并容忍前导点。</summary>
    public static string StudentFileName(
        string className, string number, string name, int pageNumber, string extension)
    {
        var ext = (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) ext = "pdf";
        return $"{StudentStem(className, number, name)}_{PageSuffix(pageNumber)}.{ext}";
    }

    /// <summary>整份学生作业合并为一个 PDF 时的文件名：班级_学号_姓名.pdf。</summary>
    public static string StudentPdfFileName(string className, string number, string name) =>
        $"{StudentStem(className, number, name)}.pdf";

    /// <summary>按班级建的输出文件夹名。</summary>
    public static string ClassFolderName(string className) => Sanitize(className);

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
        };
        for (var i = 1; i <= 9; i++)
        {
            names.Add($"COM{i}");
            names.Add($"LPT{i}");
        }
        return names;
    }
}
