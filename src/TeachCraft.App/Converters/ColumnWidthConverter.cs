using System;
using Avalonia.Data.Converters;

namespace TeachCraft.App.Converters;

/// <summary>
/// 把容器宽度换算成「N 列卡片」里单张卡的宽度。
/// 工具卡改成可展开后不能再用 UniformGrid：它会把同一行两张卡强行拉成等高，
/// 一张展开就会在另一张下面留一大块空白。改用 WrapPanel + 这里算出的定宽，
/// 既保持双列对齐，又让每张卡按自己的内容高度收尾。
/// </summary>
public static class ColumnWidthConverter
{
    /// <summary>卡片之间的间距（与 XAML 里 Margin 的右 / 下留白一致）。</summary>
    public const double Gap = 7;

    /// <summary>双列：卡宽 = (容器宽 - 两列间距) / 2，再留 1px 余量避免浮点误差挤成三列。</summary>
    public static readonly IValueConverter TwoColumns = new FuncValueConverter<double, double>(
        width => width <= Gap ? 0 : Math.Max(0, (width - Gap * 2) / 2 - 0.5));
}
