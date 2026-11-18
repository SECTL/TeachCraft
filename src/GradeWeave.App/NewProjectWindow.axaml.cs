using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TeachCraft.Shared;

namespace GradeWeave.App;

public partial class NewProjectWindow : Window
{
    private readonly NewProjectViewModel _viewModel = new();
    private bool _closeReady;
    private bool _guardOpen;

    public NewProjectWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Opened += (_, _) => WindowFit.Clamp(this);
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var config = _viewModel.TryBuild(out var error);
        if (config is null)
        {
            _viewModel.Error = error;
            return;
        }

        _closeReady = true;
        Close(config);
    }

    /// <summary>
    /// 设计走查：把课时合计推到指定档位（超过 35 = 超额红字，恰好 35 = 满额中性灰）。
    /// 平均分摊而不是逐个填满——填满会把后面的学科留成 0，画面里多出一条零课时提示。
    /// 只改内存里的草稿，不落盘、不影响任何真实配置。
    /// </summary>
    public void SetHoursForCapture(int total)
    {
        var count = _viewModel.Subjects.Count;
        if (count == 0) return;

        var each = Math.Max(0, total) / count;
        var remainder = Math.Max(0, total) % count;
        for (var i = 0; i < count; i++)
            _viewModel.Subjects[i].WeeklyHours = each + (i < remainder ? 1 : 0);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _closeReady = true;
        Close(null);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeReady || _guardOpen || !_viewModel.IsDirty) return;

        e.Cancel = true;
        _guardOpen = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var discard = await EditorCloseGuard.ShowAsync(this, "「全新制作」的设置尚未创建工程，放弃并关闭？");
        _guardOpen = false;
        if (discard)
        {
            _closeReady = true;
            Close();
        }
    }
}
