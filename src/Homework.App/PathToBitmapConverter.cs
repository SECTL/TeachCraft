using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Homework.App;

/// <summary>
/// 文件路径 → 位图。空路径 / 文件不存在 / 解码失败一律返回 null（界面显示占位），
/// 绝不把异常抛回绑定。
/// <para>
/// 缓存有上限（32 张），但**淘汰时只从字典移除、绝不 Dispose**：同一个位图可能仍被
/// 缩略图列表或预览 Image 引用，提前释放会让下一帧渲染抛 ObjectDisposedException。
/// 不再显示后由 GC 回收。
/// </para>
/// <para>用 FileShare.ReadWrite | Delete 打开，缩略图临时夹在会话结束时能顺利删掉。</para>
/// </summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    private const int MaxCache = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) return null;

        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                Touch(path);
                return cached;
            }
        }

        Bitmap bitmap;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bitmap = new Bitmap(stream);
        }
        catch
        {
            // 文件被占用、损坏或不是图片：留空占位，不影响其它页。
            return null;
        }

        lock (_gate)
        {
            // 并发下另一个线程可能已经载入同一路径，只保留一份。
            if (_cache.TryGetValue(path, out var existing))
            {
                bitmap.Dispose();
                Touch(path);
                return existing;
            }

            _cache[path] = bitmap;
            _lru.AddLast(path);
            while (_lru.Count > MaxCache)
            {
                var oldest = _lru.First!.Value;
                _lru.RemoveFirst();
                _cache.Remove(oldest);
            }
        }

        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private void Touch(string path)
    {
        var node = _lru.Find(path);
        if (node is null) return;
        _lru.Remove(node);
        _lru.AddLast(node);
    }
}

/// <summary>选中的页 → 「第 N 张」标签。N 取物理 Index + 1，删页后仍与列表顺序一致。</summary>
public sealed class PageIndexConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PageItemViewModel page) return "未选择页";
        return $"第 {page.Page.Index + 1} 张";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>学生列表 → 右侧字母导航条：按 Initial 分组去重，A–Z 在前、# 收尾。</summary>
public sealed class InitialsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<StudentRowViewModel> students) return Array.Empty<string>();

        var letters = students
            .Select(s => s.Initial)
            .Where(initial => !string.IsNullOrWhiteSpace(initial))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(initial => initial == "#" ? 1 : 0)
            .ThenBy(initial => initial, StringComparer.Ordinal)
            .ToArray();

        return letters;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
