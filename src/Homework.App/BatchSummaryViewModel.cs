using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core.Models;

namespace Homework.App;

/// <summary>首页批次卡片。所有数字都从 <see cref="Batch"/> 现算，
/// 从工作区返回首页时由外壳调用 <see cref="Refresh"/> 同步。</summary>
public sealed partial class BatchSummaryViewModel : ObservableObject
{
    public BatchSummaryViewModel(HomeworkBatch batch)
    {
        Batch = batch;
    }

    public HomeworkBatch Batch { get; }

    public string Title => Batch.Name;
    public int PageCount => Batch.Pages.Count;
    public int StudentCount => Batch.Students.Count;

    public string StateText => Batch.State switch
    {
        HomeworkBatchState.Committed => "已提交",
        HomeworkBatchState.Abandoned => "已放弃",
        _ => "编辑中",
    };

    public string Subtitle => $"{PageCount} 张页 · {StudentCount} 名学生";

    /// <summary>批次在工作区里被改动后，通知绑定重新读取上面的派生属性。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(StudentCount));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Subtitle));
    }
}
