using System.Globalization;

namespace Homework.App;

/// <summary>
/// 预览缩放的纯计算：范围钳制、步进与适应窗口。所有入口都返回 10–400 的百分比，
/// 界面只负责把结果写进 ZoomPercent，不自己算边界。
/// </summary>
public static class ZoomMath
{
    public const double MinPercent = 10;
    public const double MaxPercent = 400;

    private static readonly double[] Presets =
    [
        10, 15, 20, 25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400,
    ];

    public static double Clamp(double percent) =>
        Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>按方向跳到相邻预设档位；已在端点时停在端点。</summary>
    public static double Step(double percent, int direction)
    {
        var current = Clamp(percent);
        if (direction == 0) return current;

        if (direction > 0)
        {
            foreach (var preset in Presets)
                if (preset > current + 0.01) return preset;
            return MaxPercent;
        }

        for (var i = Presets.Length - 1; i >= 0; i--)
            if (Presets[i] < current - 0.01) return Presets[i];
        return MinPercent;
    }

    /// <summary>
    /// 把图片按较长边塞进视口，返回百分比并钳制。任一尺寸无效时回落到 100%。
    /// </summary>
    public static double Fit(int imageWidth, int imageHeight, double viewportWidth, double viewportHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return Clamp(100);

        var scale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight) * 100.0;
        if (!double.IsFinite(scale) || scale <= 0) return Clamp(100);
        return Clamp(scale);
    }

    /// <summary>四舍五入到整数百分比，固定不变的文化，避免某些区域设置输出「181,5%」。</summary>
    public static string Format(double percent)
    {
        var rounded = (int)Math.Round(Clamp(percent), MidpointRounding.AwayFromZero);
        return rounded.ToString(CultureInfo.InvariantCulture) + "%";
    }
}
