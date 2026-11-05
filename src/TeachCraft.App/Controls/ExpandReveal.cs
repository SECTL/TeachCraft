using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TeachCraft.Shared;

namespace TeachCraft.App.Controls;

/// <summary>
/// 卡片里的「说明区」展开 / 收起容器：高度按 0 → 自然高度做一次缓出揭示，
/// 同时淡入并轻微上移，收起源路返回。
///
/// 为什么不用 XAML 动画写死高度：说明区高度取决于文本换行后的实际行高，
/// 不同窗口宽度下都不一样。这里先按当前宽度量一次子内容（约束为无限高），
/// 记住自然高度，之后每帧只改自己的高度并让父级重新排列——
/// 子内容宽度没变，Avalonia 的量算缓存会命中，逐帧成本只有一次 Bounds 更新。
///
/// 系统开启「减弱动效」时不做逐帧动画，直接落位（见 MotionPreference）。
/// </summary>
public sealed class ExpandReveal : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ExpandReveal, bool>(nameof(IsExpanded));

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ExpandReveal, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(260));

    private const double FrameSeconds = 1.0 / 60.0;

    /// <summary>0 = 完全收起，1 = 完全展开。中间值同时驱动高度、透明度与位移。</summary>
    private double _progress;

    /// <summary>子内容在「当前宽度 + 无限高度」下的自然高度，收起的卡片也照样量一次。</summary>
    private double _contentHeight;

    private DispatcherTimer? _timer;
    private Stopwatch? _clock;
    private double _from;
    private double _to;
    private double _startedAt;

    public ExpandReveal()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>当前进度，供走查与测试断言使用（0–1）。</summary>
    public double Progress => _progress;

    /// <summary>子内容在「当前宽度 + 无限高度」下的自然高度，供走查核对有没有被裁掉。</summary>
    public double ContentHeight => _contentHeight;

    /// <summary>走查用：MeasureOverride 里真正量过子内容的次数（判断宽度变化后有没有重新量）。</summary>
    public int MeasurePassCount => _measurePassCount;
    private int _measurePassCount;

    /// <summary>走查用：最后一次量算时子内容占用的宽度。</summary>
    public double LastChildWidth { get; private set; } = -1;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 初次挂载时按绑定值直接落位，不做「从 0 弹开」的回放。
        _progress = IsExpanded ? 1 : 0;
        ApplyProgress();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsExpandedProperty)
            AnimateTo(change.GetNewValue<bool>() ? 1 : 0);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var child = Child;
        if (child is null) return default;

        // 与宽度同约束、高度放开：换行结果与展开后完全一致。
        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        _contentHeight = child.DesiredSize.Height;
        _measurePassCount++;
        LastChildWidth = child.DesiredSize.Width;

        var width = double.IsInfinity(availableSize.Width) ? child.DesiredSize.Width : availableSize.Width;
        return new Size(width, Math.Max(0, _contentHeight * _progress));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 子内容始终按自然高度排列，多出来的部分由 ClipToBounds 裁掉，
        // 这样揭示过程中文字不会因为高度被压而逐行跳动。
        Child?.Arrange(new Rect(0, 0, finalSize.Width, _contentHeight));
        return finalSize;
    }

    private void AnimateTo(double target)
    {
        if (Math.Abs(target - _progress) < 0.0001 && _timer is null) return;

        // 还没挂到可视树（初次绑定 / 被换出容器）时直接落位，不让计时器在后台空转。
        if (MotionPreference.ReduceMotion || VisualRoot is null)
        {
            StopTimer();
            _progress = target;
            ApplyProgress();
            return;
        }

        _from = _progress;
        _to = target;

        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FrameSeconds) };
            _timer.Tick += OnTick;
            _clock = Stopwatch.StartNew();
        }

        _startedAt = _clock!.Elapsed.TotalSeconds;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var duration = Math.Max(0.05, Duration.TotalSeconds);
        var elapsed = _clock!.Elapsed.TotalSeconds - _startedAt;
        var t = Math.Clamp(elapsed / duration, 0, 1);

        _progress = t >= 1 ? _to : _from + (_to - _from) * EaseOutCubic(t);
        ApplyProgress();

        if (t < 1) return;

        StopTimer();
    }

    private void ApplyProgress()
    {
        Opacity = _progress;
        IsHitTestVisible = _progress > 0.001;
        InvalidateMeasure();
    }

    private void StopTimer()
    {
        // 只停逐帧计时器，秒表保持走表。旧实现连秒表一起 Stop，下次展开 / 收起时
        // 只重启了计时器而秒表仍冻结，Elapsed 永远不前进，进度就卡在起点不动
        // （表现为「能展开、收不回去」）。秒表常驻的成本可以忽略。
        _timer?.Stop();
    }

    private static double EaseOutCubic(double t)
    {
        var inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }
}
