using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TeachCraft.App.ViewModels;
using TeachCraft.Shared;

namespace TeachCraft.App.Controls;

/// <summary>
/// 首次引导遮罩的交互层与动效层。
///
/// 键位表（也是无障碍约定）：Enter / Space 下一步（末步为完成），← 上一步，
/// → 下一步，Esc 跳过并记录。Tab 由 KeyboardNavigation.TabNavigation=Cycle 圈在遮罩内。
///
/// Esc 必须走「跳过并记录」而不是「悄悄关掉」：只关不记会让下次启动又弹一次，
/// 那正是用户抱怨的那类行为。
///
/// 动效原则：这一层只在首次运行出现，属于「稀有 / 首次」档，是唯一值得花动效预算的地方。
/// 但动效只做三件事——让内容从哪儿来能看出来、让状态变化不被漏掉、让进出有先后；
/// 不做循环、不做装饰、不让用户在读的时候东西还在动。
/// </summary>
public partial class OobeOverlayView : UserControl
{
    // 进场 / 退场 / 翻页共用一条强 ease-out。内置 ease-out 太弱，UI 上看着像没动。
    private const string EaseOutCurve = "0.23,1,0.32,1";

    private const double EnterMs = 240;
    private const double ExitMs = 140;
    private const double StepMs = 200;
    private const double StepDistance = 24;

    public OobeOverlayView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnOverlayPointerPressed, RoutingStrategies.Tunnel);

        // 不能在 AttachedToVisualTree 里 Focus()：那一刻遮罩的 IsVisible 还是 false，
        // 焦点请求会被静默丢弃，键盘事件就永远到不了遮罩。
        // 改为监听 IsVisible 变为 true 之后再抢焦点。
        this.GetObservable(IsVisibleProperty).Subscribe(
            new AnonymousObserver<bool>(visible =>
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
                    PlayEnter();
                }
            }));
    }

    private OobeViewModel? _hooked;

    private void HookViewModel()
    {
        if (_hooked is not null) _hooked.PropertyChanged -= OnVmPropertyChanged;
        _hooked = DataContext as OobeViewModel;
        if (_hooked is not null)
        {
            _hooked.PropertyChanged += OnVmPropertyChanged;
            // 退场：先播 140ms 淡出，再让 ViewModel 真正落盘并关闭。
            // 直接切会像「窗口闪没了」，而用户刚刚点的是「开始使用」或「跳过」，
            // 那一下应该有个收束。
            _hooked.ExitAnimation = how =>
            {
                if (MotionPreference.ReduceMotion) return false;
                PlayExit(() => _hooked.CompleteDismiss(how));
                return true;
            };
        }
        _lastIndex = _hooked?.CurrentIndex ?? 1;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OobeViewModel.CurrentIndex) && ViewModel is { } vm)
            AnimateStepChange(vm.CurrentIndex);
    }

    private OobeViewModel? ViewModel => DataContext as OobeViewModel;

    private TranslateTransform? _slide;
    private int _lastIndex = 1;

    private Border? Card => this.GetVisualDescendants().OfType<Border>()
        .FirstOrDefault(b => b.Classes.Contains("oobe-card"));

    /// <summary>
    /// 翻页。方向必须跟着走：前进时内容从右侧进来，后退时从左侧进来。
    /// 两个方向都往同一边飘，会让「上一步」看起来像「下一步」，这是每天翻页都在发生的错误。
    ///
    /// 减弱动效时不位移，只保留一次极短的透明度过渡——完全不淡的话，内容会像被替换而非翻页，
    /// 反而更难读；但位移与时长都归零。
    /// </summary>
    private void AnimateStepChange(int newIndex)
    {
        if (newIndex == _lastIndex) return;

        var goingForward = newIndex > _lastIndex;
        _lastIndex = newIndex;

        var card = Card;
        if (card is null) return;

        if (MotionPreference.ReduceMotion)
        {
            // 减弱动效：不位移、不淡入，直接落位。
            // 曾经想「淡一下更容易看清换页」，但那是把位移换成透明度，
            // 仍然是一次可见的过渡——用户的偏好是不动，不是换一种动法。
            card.RenderTransform = null;
            card.Opacity = 1;
            return;
        }

        _slide ??= new TranslateTransform();
        card.RenderTransform = _slide;

        // 新内容从行进方向的反面进来：前进 → 从 +24 滑到 0；后退 → 从 -24 滑到 0。
        var from = goingForward ? StepDistance : -StepDistance;
        Animate(EnterOrStepMs, progress =>
        {
            var eased = EaseOutCubic(progress);
            _slide!.X = from * (1 - eased);
            card.Opacity = eased;
        }, () =>
        {
            _slide!.X = 0;
            card.Opacity = 1;
        });
    }

    /// <summary>
    /// 遮罩首次出现。卡片从略小、略淡浮上来，背景纸色不做任何动画——
    /// 背景是铺满全窗的，让它动会让整个屏幕闪一下。
    /// </summary>
    private void PlayEnter()
    {
        var card = Card;
        if (card is null) return;

        if (MotionPreference.ReduceMotion)
        {
            card.Opacity = 1;
            return;
        }

        _slide ??= new TranslateTransform();
        card.RenderTransform = _slide;

        var scale = new ScaleTransform(0.97, 0.97);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(_slide);
        card.RenderTransform = group;

        card.Opacity = 0;
        _slide.Y = 8;

        Animate(EnterMs, progress =>
        {
            var eased = EaseOutCubic(progress);
            card.Opacity = eased;
            _slide.Y = 8 * (1 - eased);
            scale.ScaleX = scale.ScaleY = 0.97 + 0.03 * eased;
        }, () =>
        {
            card.Opacity = 1;
            _slide.Y = 0;
            scale.ScaleX = scale.ScaleY = 1;
            // 进场结束后把变换组还原成单一平移，避免后续翻页叠在缩放上。
            card.RenderTransform = _slide;
        });
    }

    /// <summary>遮罩关闭：比进场快，用户已经决定离开，不该再等。</summary>
    private void PlayExit(Action after)
    {
        var card = Card;
        if (card is null || MotionPreference.ReduceMotion)
        {
            after();
            return;
        }

        Animate(ExitMs, progress =>
        {
            var eased = EaseInOutCubic(progress);
            card.Opacity = 1 - eased;
            if (card.RenderTransform is TranslateTransform t) t.Y = -6 * eased;
        }, after);
    }

    private const double EnterOrStepMs = StepMs;

    /// <summary>cubic-bezier(0.23,1,0.32,1) 的近似：起点即快，尾部极缓。</summary>
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private static void Animate(double milliseconds, Action<double> onFrame, Action? onDone = null)
    {
        var clock = Stopwatch.StartNew();
        var seconds = milliseconds / 1000.0;
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(clock.Elapsed.TotalSeconds / seconds, 0, 1);
            onFrame(progress);
            if (progress < 1) return;
            timer!.Stop();
            onDone?.Invoke();
        };
        timer.Start();
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.IsOpen) return;

        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                PrimaryAction(vm);
                e.Handled = true;
                break;
            case Key.Right:
                vm.Next();
                e.Handled = true;
                break;
            case Key.Left:
                vm.Back();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Skip();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 吞掉遮罩上的点击，避免落到下面被禁用的内容区。
    /// 但按钮自己的点击要放行——它们在遮罩内部，是合法的操作。
    /// </summary>
    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        e.Handled = true;
        Focus();
    }

    private void PrimaryAction(OobeViewModel vm)
    {
        if (vm.IsLastStep) vm.Finish();
        else vm.Next();
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) PrimaryAction(vm);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => ViewModel?.Back();

    private void OnSkipClick(object? sender, RoutedEventArgs e) => ViewModel?.Skip();

    /// <summary>
    /// 第 4 页的三个入口。它们只做「关掉引导 + 打开同一个入口」，
    /// 不选工具、不读文件、不载示例数据；「看看检查更新」只开面板，不触发检查。
    /// </summary>
    /// <summary>
    /// 第 4 页的三个入口按钮错开 60ms 淡入。用 AttachedToVisualTree 而不是在
    /// ItemsControl 上做整体动画，是因为每条按钮只在「这一页真的出现」时才挂到树上。
    /// 减弱动效时直接落位。
    /// </summary>
    private void OnActionAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control button) return;
        if (MotionPreference.ReduceMotion)
        {
            button.Opacity = 1;
            return;
        }

        var index = this.GetVisualDescendants().OfType<Button>()
            .Count(b => b.Classes.Contains("oobe-action") && b.IsVisible) - 1;
        var delay = Math.Max(0, index) * 60.0;

        button.Opacity = 0;
        var slide = new TranslateTransform(0, 6);
        button.RenderTransform = slide;

        DispatcherTimer? timer = null;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer!.Stop();
            Animate(180, progress =>
            {
                var eased = EaseOutCubic(progress);
                button.Opacity = eased;
                slide.Y = 6 * (1 - eased);
            }, () =>
            {
                button.Opacity = 1;
                slide.Y = 0;
            });
        };
        timer.Start();
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not OobeActionKind kind) return;
        ViewModel?.Finish();
        ActionRequested?.Invoke(kind);
    }

    /// <summary>由 MainWindow 订阅，转交给与侧栏完全相同的处理器。</summary>
    public event Action<OobeActionKind>? ActionRequested;

    /// <summary>走查专用：重新播一次进场，供取证采样。生产路径不调用。</summary>
    public void ReplayEnterForDiagnostics() => PlayEnter();
}
