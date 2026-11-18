using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GradeWeave.Core.Models;
using TeachCraft.Shared;

namespace GradeWeave.App;

/// <summary>活动编辑窗口返回：保存的活动，或请求删除。</summary>
public sealed record ActivityEditorResult(ActivityBlock? Activity, bool Delete);

public partial class ActivityEditorWindow : Window
{
    private readonly ActivityEditorViewModel? _viewModel;
    private bool _closeReady;
    private bool _guardOpen;

    public ActivityEditorWindow()
    {
        InitializeComponent();
    }

    public ActivityEditorWindow(WeaveProject project, ActivityBlock? existing) : this()
    {
        _viewModel = new ActivityEditorViewModel(project, existing);
        DataContext = _viewModel;
        Opened += (_, _) => WindowFit.Clamp(this);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;

        var activity = _viewModel.TryBuild(out var error);
        if (activity is null)
        {
            _viewModel.Error = error;
            return;
        }

        _closeReady = true;
        Close(new ActivityEditorResult(activity, false));
    }

    /// <summary>
    /// 删除确认的文案：按打开时的存量活动是否替换型分级；替换型必须写明原课不自动恢复。
    /// 抽成纯函数是为了可测：测试不构造窗口也能锁住分级与措辞。
    /// </summary>
    public static (string Title, string Message) DescribeDeleteConfirm(bool isReplaceMode, string activityName)
    {
        return isReplaceMode
            ? ("删除替换型活动？",
                $"将删除「{activityName}」。被移除的原课不会自动恢复，删除后需要重新排课补上这些课。")
            : ("移除该活动？",
                $"将移除「{activityName}」，只撤掉提醒展示，不影响课程表。");
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsEditing != true) return;

        var (title, message) = DescribeDeleteConfirm(_viewModel.IsExistingReplace, _viewModel.ExistingActivityName);
        var confirmed = await ConfirmDialog.ShowAsync(
            this, title, message,
            confirmText: "删除", cancelText: "取消", danger: _viewModel.IsExistingReplace);
        if (!confirmed) return;

        _closeReady = true;
        Close(new ActivityEditorResult(null, true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _closeReady = true;
        Close(null);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel is null || _closeReady || _guardOpen || !_viewModel.IsDirty) return;

        e.Cancel = true;
        _guardOpen = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var discard = await EditorCloseGuard.ShowAsync(this, "活动改动尚未保存，放弃并关闭？");
        _guardOpen = false;
        if (discard)
        {
            _closeReady = true;
            Close();
        }
    }
}
