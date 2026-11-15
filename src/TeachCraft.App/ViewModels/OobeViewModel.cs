using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using TeachCraft.Core.Oobe;

namespace TeachCraft.App.ViewModels;

/// <summary>
/// 引导遮罩的状态机。刻意不引用任何 Avalonia 类型，可以在没有 UI 线程的测试里直接 new。
///
/// 它只知道「第几页、开没开、结束时怎么记账」，不知道窗口长什么样。
/// </summary>
public partial class OobeViewModel : ObservableObject
{
    private readonly IOobeStateStore _store;

    public OobeViewModel(IOobeStateStore? store = null)
    {
        _store = store ?? new NoopOobeStateStore();
    }

    public IReadOnlyList<OobeStep> Steps => OobeContent.Steps;

    /// <summary>关闭时触发，窗口层据此把焦点还回去。</summary>
    public event Action? Closed;

    /// <summary>
    /// 界面层可挂一个「先播退场动画再真正关闭」的钩子。
    /// 返回 true 表示它接管了关闭（会稍后自己调 <see cref="CompleteDismiss"/>）；
    /// 返回 false / 未挂时立刻关闭。
    /// </summary>
    public Func<OobeDismissal, bool>? ExitAnimation;

    [ObservableProperty] private bool _isOpen;

    /// <summary>1 起的当前页序号。</summary>
    [ObservableProperty] private int _currentIndex = 1;

    /// <summary>本次显示的原因。现在只有 FirstRun 会显示。</summary>
    [ObservableProperty] private OobeShowReason _reason = OobeShowReason.FirstRun;

    public OobeStep CurrentStep => Steps[Math.Clamp(CurrentIndex, 1, Steps.Count) - 1];

    public bool IsFirstStep => CurrentIndex <= 1;

    public bool IsLastStep => CurrentIndex >= Steps.Count;

    public string ProgressText => $"{CurrentIndex:00} / {Steps.Count:00}";

    public string PrimaryButtonText => IsLastStep ? "开始使用" : "下一步";

    /// <summary>打开引导。step 越界会被钳到合法范围。</summary>
    public void OpenAt(int step, OobeShowReason reason = OobeShowReason.FirstRun)
    {
        CurrentIndex = Math.Clamp(step, 1, Steps.Count);
        Reason = reason;
        IsOpen = true;
        RaiseDerived();
    }

    public void Next()
    {
        if (IsLastStep) return;
        CurrentIndex++;
        RaiseDerived();
    }

    public void Back()
    {
        if (IsFirstStep) return;
        CurrentIndex--;
        RaiseDerived();
    }

    /// <summary>看完。最后一步的「开始使用」走这里。</summary>
    public void Finish() => Dismiss(OobeDismissal.Completed);

    /// <summary>跳过。与看完一样被记住，不再打扰。</summary>
    public void Skip() => Dismiss(OobeDismissal.Skipped);

    private void Dismiss(OobeDismissal how)
    {
        if (ExitAnimation?.Invoke(how) == true) return;
        CompleteDismiss(how);
    }

    /// <summary>真正落盘并关闭。退场动画播完后由界面层调用。</summary>
    public void CompleteDismiss(OobeDismissal how)
    {
        _store.Save(new OobeState(DateTimeOffset.UtcNow, how));
        IsOpen = false;
        Closed?.Invoke();
    }

    partial void OnCurrentIndexChanged(int value) => RaiseDerived();

    /// <summary>
    /// 派生属性集中通知。逐个手写 OnPropertyChanged 是已知的漏项来源
    /// （导出弹窗曾因为漏通知一个派生属性而让控件不消失）。
    /// </summary>
    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }
}
