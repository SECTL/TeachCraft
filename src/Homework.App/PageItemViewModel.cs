using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core.Models;

namespace Homework.App;

/// <summary>左侧页列表的一行。只读字段来自 Core 的 <see cref="HomeworkPage"/>，
/// 只有选中态与缩略图路径由界面维护。</summary>
public sealed partial class PageItemViewModel : ObservableObject
{
    public PageItemViewModel(HomeworkPage page, HomeworkStudent? student, string thumbnailPath = "")
    {
        Page = page;
        Student = student;
        _thumbnailPath = thumbnailPath;
    }

    public HomeworkPage Page { get; }
    public HomeworkStudent? Student { get; }

    public Guid Id => Page.Id;
    public string Label => Page.UnknownLabel;
    public string StatusText => Student?.Name ?? "未匹配";
    public string SourceFileName => Path.GetFileName(Page.SourcePath);
    public bool IsDuplicateHint => Page.IsDuplicateHint;

    /// <summary>本页是否已有识别结果——只控制徽标 / 说明的可见性，不参与匹配语义。</summary>
    public bool HasRecognition => Page.Recognition is not null;

    /// <summary>行内小徽标：填涂读取失败显示「填涂失败」，其余显示读到的数字串，未识别为空。</summary>
    public string RecognitionBadge
    {
        get
        {
            var recognition = Page.Recognition;
            if (recognition is null) return string.Empty;
            return recognition.FailureReason is not null ? "填涂失败" : $"填涂 {recognition.Digits}";
        }
    }

    /// <summary>徽标悬浮提示：匹配失败原因优先，其次填涂失败原因，最后说明读到的学号。</summary>
    public string RecognitionReason
    {
        get
        {
            var recognition = Page.Recognition;
            if (recognition is null) return string.Empty;
            if (recognition.MatchFailureReason is not null) return recognition.MatchFailureReason;
            if (recognition.FailureReason is not null) return recognition.FailureReason;
            return $"识别为 {recognition.Digits}";
        }
    }

    /// <summary>预览下方的完整说明：区分匹配失败、填涂失败与成功三种状态。</summary>
    public string RecognitionSummary
    {
        get
        {
            var recognition = Page.Recognition;
            if (recognition is null) return string.Empty;
            if (recognition.MatchFailureReason is not null)
                return $"识别：{recognition.Digits} · {recognition.MatchFailureReason}";
            if (recognition.FailureReason is not null)
                return $"识别失败：{recognition.FailureReason}";
            return $"识别：{recognition.Digits} · 已匹配 {Student?.Name ?? "（待匹配）"}";
        }
    }

    public string PageNumberText => Page.Kind == HomeworkPageKind.PdfPage
        ? $"第{Page.SourcePageNumber}页"
        : "图片";

    [ObservableProperty] private string _thumbnailPath = string.Empty;
    [ObservableProperty] private bool _isSelected;
}
