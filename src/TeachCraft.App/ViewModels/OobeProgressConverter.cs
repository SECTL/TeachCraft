using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace TeachCraft.App.ViewModels;

/// <summary>
/// 把「当前第几页 / 共几页」换算成进度轨的已填充宽度。
/// 放在 ViewModel 命名空间是为了让 XAML 与状态机共用同一套页数语义。
/// </summary>
public sealed class OobeProgressConverter : IMultiValueConverter
{
    public static readonly OobeProgressConverter Instance = new();

    /// <summary>进度轨的总宽（DIP），与 OobeOverlayView 里的 Border.Width 一致。</summary>
    private const double TrackWidth = 96;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return 0d;
        if (!TryNumber(values[0], out var index) || !TryNumber(values[1], out var count)) return 0d;
        if (count <= 0) return 0d;

        var progress = Math.Clamp(index / count, 0, 1);
        return Math.Max(2, TrackWidth * progress);
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case double d: number = d; return true;
            case long l: number = l; return true;
            default: number = 0; return false;
        }
    }
}
