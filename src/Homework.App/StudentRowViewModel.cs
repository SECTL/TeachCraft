using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core.Models;

namespace Homework.App;

/// <summary>右侧学生列表的一行。页数量与同名标记由工作区在每次刷新时算好传入，
/// 行本身不回查批次，保证刷新是一次性的、可预测的。</summary>
public sealed partial class StudentRowViewModel : ObservableObject
{
    public StudentRowViewModel(
        HomeworkStudent student,
        string className,
        int pageCount,
        int expectedPages,
        bool isDuplicateName)
    {
        Student = student;
        ClassName = className;
        PageCount = pageCount;
        ExpectedPages = expectedPages;
        IsDuplicateName = isDuplicateName;
    }

    public HomeworkStudent Student { get; }

    public Guid Id => Student.Id;
    public string Name => Student.Name;
    public string Number => Student.Number;
    public string ClassName { get; }
    public int PageCount { get; }
    public int ExpectedPages { get; }
    public bool IsUnmatched => PageCount == 0;
    public bool IsDuplicateName { get; }

    /// <summary>还差几页；0 表示已交齐。</summary>
    public int MissingPages => Math.Max(0, ExpectedPages - PageCount);

    public bool IsComplete => MissingPages == 0;

    public string BadgeText => IsUnmatched ? "未匹配"
        : IsComplete ? "已完成"
        : $"缺 {MissingPages} 页";

    public string Initial => PinyinIndex.Initial(Name);
}
