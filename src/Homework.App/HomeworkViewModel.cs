using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core.Models;

namespace Homework.App;

/// <summary>辑册窗口外壳：首页批次列表与单个批次工作区之间导航，
/// 并持有长任务的忙碌状态与关窗守卫。</summary>
public sealed partial class HomeworkViewModel : ObservableObject
{
    private BatchWorkspaceViewModel? _workspace;
    private bool _isWorkspaceOpen;
    private bool _isHomeVisible = true;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private bool _cancelRequested;

    public ObservableCollection<BatchSummaryViewModel> Batches { get; } = [];

    public bool HasBatches => Batches.Count > 0;
    public bool IsHomeEmpty => !HasBatches;

    public BatchWorkspaceViewModel? Workspace
    {
        get => _workspace;
        private set => SetProperty(ref _workspace, value);
    }

    public bool IsWorkspaceOpen
    {
        get => _isWorkspaceOpen;
        private set => SetProperty(ref _isWorkspaceOpen, value);
    }

    public bool IsHomeVisible
    {
        get => _isHomeVisible;
        private set => SetProperty(ref _isHomeVisible, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public bool CancelRequested
    {
        get => _cancelRequested;
        private set => SetProperty(ref _cancelRequested, value);
    }

    public void BeginNewBatch()
    {
        var batch = HomeworkBatch.Create($"批次{Batches.Count + 1}");
        var summary = new BatchSummaryViewModel(batch);
        Batches.Add(summary);
        OnPropertyChanged(nameof(HasBatches));
        OnPropertyChanged(nameof(IsHomeEmpty));
        OpenWorkspace(batch, summary);
        StatusText = $"已创建 {batch.Name}，可以导入名单与作业页了。";
    }

    public void OpenBatch(BatchSummaryViewModel summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenWorkspace(summary.Batch, summary);
        StatusText = $"已打开 {summary.Title}。";
    }

    public void BackToHome()
    {
        // 回首页前把工作区里的改动同步到卡片，避免首页数字停留在进入时的旧值。
        foreach (var summary in Batches)
            if (Workspace is not null && ReferenceEquals(summary.Batch, Workspace.Batch))
                summary.Refresh();

        Workspace = null;
        IsWorkspaceOpen = false;
        IsHomeVisible = true;
        StatusText = "已返回批次列表。";
    }

    public void BeginBusy(string text)
    {
        BusyText = text;
        CancelRequested = false;
        IsBusy = true;
    }

    public void EndBusy()
    {
        IsBusy = false;
        BusyText = string.Empty;
        CancelRequested = false;
    }

    /// <summary>忙碌遮罩上的「取消」按钮入口；Core 导入器会在下一个检查点停下。</summary>
    public void RequestCancel()
    {
        if (!IsBusy || CancelRequested) return;
        CancelRequested = true;
        BusyText = string.IsNullOrEmpty(BusyText) ? "正在取消…" : BusyText + "（正在取消…）";
    }

    /// <summary>任意未提交且含页的批次（包括已返回首页的）在关窗前都要确认。</summary>
    public bool ShouldConfirmClose() =>
        Batches.Any(summary => summary.Batch.State == HomeworkBatchState.Editing
                               && summary.Batch.Pages.Count > 0);

    private void OpenWorkspace(HomeworkBatch batch, BatchSummaryViewModel summary)
    {
        var workspace = new BatchWorkspaceViewModel(batch);
        workspace.AbandonRequested += (_, _) => BackToHome();
        Workspace = workspace;
        IsWorkspaceOpen = true;
        IsHomeVisible = false;
        summary.Refresh();
    }
}
