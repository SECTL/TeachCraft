using Avalonia;
using Homework.Core.Recognition;

namespace Homework.App;

/// <summary>矩形选区的命中点：四角、四边中点，以及整体移动。</summary>
public enum RectHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Move,
}

/// <summary>
/// 归一化矩形选区的纯几何计算。所有坐标都是 0..1，输入像素坐标按控件尺寸归一化；
/// 控件与图像同尺寸，因此这里不做任何 letterbox / Stretch=Uniform 反算。
/// <para>结果一律夹在 0..1 且宽高不小于 <see cref="MinSize"/>，反向拖拽自动归一化。</para>
/// </summary>
public static class RectSelectionMath
{
    /// <summary>最小可用宽 / 高，与 Core 的选区不变量同源。</summary>
    public const double MinSize = NormalizedRect.MinUsableSize;

    /// <summary>
    /// 把控件局部像素坐标的拖拽两端转成归一化矩形。反向拖拽取 min/abs 归一化；
    /// 越界坐标夹到 0..1；过小的拖拽抬到最小尺寸。
    /// </summary>
    public static NormalizedRect FromDrag(Point start, Point end, Size surface)
    {
        if (surface.Width <= 0 || surface.Height <= 0)
            return Clamp(new NormalizedRect(0, 0, 0, 0));

        var sx = start.X / surface.Width;
        var sy = start.Y / surface.Height;
        var ex = end.X / surface.Width;
        var ey = end.Y / surface.Height;

        return Clamp(new NormalizedRect(
            Math.Min(sx, ex),
            Math.Min(sy, ey),
            Math.Abs(ex - sx),
            Math.Abs(ey - sy)));
    }

    /// <summary>
    /// 整体平移：delta 是像素位移，按控件尺寸归一化后加到左上角；
    /// 宽高不变，左上角夹到 [0, 1 - 宽/高] 保证矩形不出界。
    /// </summary>
    public static NormalizedRect Move(NormalizedRect rect, Vector delta, Size surface)
    {
        var c = rect.Clamp();
        if (surface.Width <= 0 || surface.Height <= 0) return Clamp(c);

        var x = c.X + delta.X / surface.Width;
        var y = c.Y + delta.Y / surface.Height;
        return new NormalizedRect(
            Math.Clamp(x, 0, 1 - c.Width),
            Math.Clamp(y, 0, 1 - c.Height),
            c.Width,
            c.Height);
    }

    /// <summary>
    /// 按手柄缩放：delta 是像素位移，只有该手柄负责的边会移动，对边保持不动。
    /// 每条轴独立夹到 0..1 并强制最小尺寸；Move 走 <see cref="Move"/>，None 只夹取。
    /// </summary>
    public static NormalizedRect Resize(NormalizedRect rect, RectHandle handle, Vector delta, Size surface)
    {
        var c = rect.Clamp();
        if (handle == RectHandle.None) return Clamp(c);
        if (handle == RectHandle.Move) return Move(c, delta, surface);
        if (surface.Width <= 0 || surface.Height <= 0) return Clamp(c);

        var dx = delta.X / surface.Width;
        var dy = delta.Y / surface.Height;

        var left = c.X;
        var top = c.Y;
        var right = c.X + c.Width;
        var bottom = c.Y + c.Height;

        var moveLeft = handle is RectHandle.Left or RectHandle.TopLeft or RectHandle.BottomLeft;
        var moveRight = handle is RectHandle.Right or RectHandle.TopRight or RectHandle.BottomRight;
        var moveTop = handle is RectHandle.Top or RectHandle.TopLeft or RectHandle.TopRight;
        var moveBottom = handle is RectHandle.Bottom or RectHandle.BottomLeft or RectHandle.BottomRight;

        if (moveLeft) left += dx;
        if (moveRight) right += dx;
        if (moveTop) top += dy;
        if (moveBottom) bottom += dy;

        (left, right) = EnforceAxis(left, right, moveLeft);
        (top, bottom) = EnforceAxis(top, bottom, moveTop);

        return new NormalizedRect(left, top, right - left, bottom - top);
    }

    /// <summary>夹到 0..1、保证宽高至少 <see cref="MinSize"/>，且矩形整体不出界。</summary>
    public static NormalizedRect Clamp(NormalizedRect rect)
    {
        var c = rect.Clamp(); // Core 负责拒绝非有限坐标并逐项夹到 0..1
        var width = Math.Clamp(c.Width, MinSize, 1);
        var height = Math.Clamp(c.Height, MinSize, 1);
        return new NormalizedRect(
            Math.Clamp(c.X, 0, 1 - width),
            Math.Clamp(c.Y, 0, 1 - height),
            width,
            height);
    }

    /// <summary>
    /// 单轴夹取：先把两条边夹到 0..1，再在跨度不足时保留不动的那条边、
    /// 把移动的那条边推到最小跨度（moveLo 表示移动的是下界）。
    /// </summary>
    private static (double Lo, double Hi) EnforceAxis(double lo, double hi, bool moveLo)
    {
        lo = Math.Clamp(lo, 0, 1);
        hi = Math.Clamp(hi, 0, 1);
        if (hi - lo >= MinSize) return (lo, hi);

        if (moveLo)
        {
            lo = hi - MinSize;
            if (lo < 0) { lo = 0; hi = MinSize; }
        }
        else
        {
            hi = lo + MinSize;
            if (hi > 1) { hi = 1; lo = 1 - MinSize; }
        }
        return (lo, hi);
    }
}
