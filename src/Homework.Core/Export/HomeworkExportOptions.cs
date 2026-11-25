namespace Homework.Core.Export;

/// <summary>作业导出的四种形态。</summary>
public enum HomeworkExportMode
{
    /// <summary>按「班级/班级_学号_姓名_第N页.ext」逐页重命名归档。</summary>
    RenameArchive,
    /// <summary>每名学生已匹配的页合并成一份 PDF。</summary>
    MergePdf,
    /// <summary>逐页重命名归档，并额外产出一份索引工作簿。</summary>
    ArchiveWithIndex,
    /// <summary>按「班级/学生/原文件名」复制到文件夹。</summary>
    CopyToFolders,
}

/// <summary>导出选项。覆盖开关只影响「目标已存在」这一类冲突，对导入来源永远无效。</summary>
public sealed record HomeworkExportOptions(
    string OutputDirectory,
    HomeworkExportMode Mode,
    bool OverwriteExisting = false);
