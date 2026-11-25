using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Homework.Core.Recognition;

namespace Homework.App.Controls;

/// <summary>
/// 覆盖在作业预览上的归一化矩形选区控件。控件与图像同尺寸，坐标按控件尺寸归一化，
/// 不做 letterbox 反算。空白处按下开始画、矩形内按下整体移动、手柄上按下缩放；
/// 松开即提交（通过 <see cref="Rect"/> 的双向绑定回传）。
/// </summary>
public sealed class NormalizedRectSelector : Control
{
    public static readonly StyledProperty<NormalizedRect?> RectProperty =
        AvaloniaProperty.Register<NormalizedRectSelector, NormalizedRect?>(nameof(Rect));

    public static readonly StyledProperty<bool> IsSelectionEnabledProperty =
        AvaloniaProperty.Register<NormalizedRectSelector, bool>(nameof(IsSelectionEnabled));

    private const double HandleSize = 10;
    private const double HandleHalf = HandleSize / 2;

    // 纸墨强调色 #D97757：填充 20% 透明，边框与手柄描边不透明。
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0x77, 0x57));
    private static readonly IBrush StrokeBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x77, 0x57));
    private static readonly IPen BorderPen = new Pen(StrokeBrush, 2);
    private static readonly IPen HandlePen = new Pen(StrokeBrush, 1);

    static NormalizedRectSelector()
    {
        AffectsRender<NormalizedRectSelector>(RectProperty, IsSelectionEnabledProperty);
    }

    public NormalizedRect? Rect
    {
        get => GetValue(RectProperty);
        set => SetValue(RectProperty, value);
    }

    public bool IsSelectionEnabled
    {
        get => GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    private enum DragMode { None, Draw, Move, Resize }

    private DragMode _mode;
    private RectHandle _handle;
    private Point _start;
    private NormalizedRect _original = new(0, 0, 0, 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsSelectionEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(this);
        var pixel = PixelRect();

        if (Rect is { } current && pixel.Width > 0 && pixel.Height > 0)
        {
            var handle = HandleAt(point, pixel);
            if (handle != RectHandle.None)
            {
                _mode = DragMode.Resize;
                _handle = handle;
            }
            else if (pixel.Contains(point))
            {
                _mode = DragMode.Move;
                _handle = RectHandle.Move;
            }
            else
            {
                _mode = DragMode.Draw;
            }
            _original = current;
        }
        else
        {
            _mode = DragMode.Draw;
            _original = new NormalizedRect(0, 0, 0, 0);
        }

        _start = point;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mode == DragMode.None) return;

        ApplyDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == DragMode.None) return;

        // 松开时按最终指针位置再算一次，保证最后一帧不丢。
        ApplyDrag(e.GetPosition(this));
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void ApplyDrag(Point point)
    {
        var delta = point - _start;
        var surface = Bounds.Size;
        Rect = _mode switch
        {
            DragMode.Draw => RectSelectionMath.FromDrag(_start, point, surface),
            DragMode.Move => RectSelectionMath.Move(_original, delta, surface),
            DragMode.Resize => RectSelectionMath.Resize(_original, _handle, delta, surface),
            _ => Rect,
        };
    }

    private void EndDrag()
    {
        _mode = DragMode.None;
        _handle = RectHandle.None;
    }

    private Rect PixelRect()
    {
        if (Rect is not { } rect) return default;
        var size = Bounds.Size;
        return new Rect(rect.X * size.Width, rect.Y * size.Height, rect.Width * size.Width, rect.Height * size.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        // 透明底让整个控件参与命中测试：启用时空白处按下也能开始画选区；
        // 禁用时不画任何东西，点击透传给下面的图像。
        if (IsSelectionEnabled)
            context.FillRectangle(Brushes.Transparent, new Rect(size));

        if (Rect is not { } rect) return;
        var pixel = new Rect(rect.X * size.Width, rect.Y * size.Height, rect.Width * size.Width, rect.Height * size.Height);
        context.FillRectangle(FillBrush, pixel);
        context.DrawRectangle(BorderPen, pixel);

        if (!IsSelectionEnabled) return;
        foreach (var (_, point) in HandlePoints(pixel))
        {
            var box = new Rect(point.X - HandleHalf, point.Y - HandleHalf, HandleSize, HandleSize);
            context.FillRectangle(Brushes.White, box);
            context.DrawRectangle(HandlePen, box);
        }
    }

    private static RectHandle HandleAt(Point p, Rect pixel)
    {
        foreach (var (handle, point) in HandlePoints(pixel))
        {
            if (Math.Abs(p.X - point.X) <= HandleHalf && Math.Abs(p.Y - point.Y) <= HandleHalf)
                return handle;
        }
        return RectHandle.None;
    }

    private static IEnumerable<(RectHandle Handle, Point Point)> HandlePoints(Rect pixel)
    {
        var cx = pixel.Left + pixel.Width / 2;
        var cy = pixel.Top + pixel.Height / 2;
        yield return (RectHandle.TopLeft, new Point(pixel.Left, pixel.Top));
        yield return (RectHandle.Top, new Point(cx, pixel.Top));
        yield return (RectHandle.TopRight, new Point(pixel.Right, pixel.Top));
        yield return (RectHandle.Right, new Point(pixel.Right, cy));
        yield return (RectHandle.BottomRight, new Point(pixel.Right, pixel.Bottom));
        yield return (RectHandle.Bottom, new Point(cx, pixel.Bottom));
        yield return (RectHandle.BottomLeft, new Point(pixel.Left, pixel.Bottom));
        yield return (RectHandle.Left, new Point(pixel.Left, cy));
    }
}
