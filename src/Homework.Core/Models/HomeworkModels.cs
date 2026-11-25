using Homework.Core.Recognition;

namespace Homework.Core.Models;

/// <summary>一页作业的来源形态：单张图片，或 PDF 中的一页。</summary>
public enum HomeworkPageKind
{
    Image,
    PdfPage,
}

/// <summary>批次的生命周期。只有 Editing 可以改动，提交 / 放弃后整批只读。</summary>
public enum HomeworkBatchState
{
    Editing,
    Committed,
    Abandoned,
}

/// <summary>一个班级。Id 是批次内稳定标识，不随名单重新导入而复用。</summary>
public sealed record HomeworkClass(Guid Id, string Name);

/// <summary>一名学生。Number 允许为空（名单缺学号列时）。</summary>
public sealed record HomeworkStudent(Guid Id, Guid ClassId, string Number, string Name);

/// <summary>
/// 一页已加入批次的作业。Index 是屏幕顺序（0 基、密集），StudentId 为 null 表示未匹配。
/// UnknownLabel 在加入时分配且永不重编号——删页后剩下的页仍保留原来的「未知 N」。
/// </summary>
public sealed class HomeworkPage
{
    public Guid Id { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public HomeworkPageKind Kind { get; init; }
    /// <summary>1 基页码；图片恒为 1。</summary>
    public int SourcePageNumber { get; init; }
    public int Index { get; internal set; }
    public Guid? StudentId { get; internal set; }
    public string UnknownLabel { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    /// <summary>内容哈希与更早的页相同——只作提示，绝不阻止加入或提交。</summary>
    public bool IsDuplicateHint { get; internal set; }
    /// <summary>本页的填涂识别结果；null 表示尚未识别。</summary>
    public PageRecognition? Recognition { get; internal set; }
    /// <summary>单页框选覆盖；非空时优先于批次级 FillInRegion。App 框选后需直接写入，故为 public set。</summary>
    public NormalizedRect? FillInRegionOverride { get; set; }
}
