using Homework.Core.Models;

namespace Homework.Core.Export;

/// <summary>一条导出动作的来源形态。</summary>
public enum HomeworkExportEntryKind
{
    ImageCopy,
    PdfPageExtract,
    MergedPdf,
    FolderCopy,
}

/// <summary>
/// 一条待执行的导出动作。<see cref="PageId"/> 为空表示这是「整份合并」而非单页动作；
/// <see cref="StudentId"/> 为空表示无法归属到名单学生（规划阶段不会产生这种条目）。
/// </summary>
public sealed record HomeworkExportEntry(
    HomeworkExportEntryKind Kind,
    Guid? PageId,
    Guid? StudentId,
    string SourcePath,
    int SourcePageNumber,
    string TargetPath);

/// <summary>导出计划（dry run 结果）：要写什么、撞了什么、漏了谁。</summary>
public sealed record HomeworkExportPlan(
    IReadOnlyList<HomeworkExportEntry> Entries,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<HomeworkStudent> StudentsWithoutPages,
    IReadOnlyList<string> Warnings,
    string? IndexPath,
    int PageCount,
    int StudentCount);
