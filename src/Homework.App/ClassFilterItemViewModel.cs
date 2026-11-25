using Homework.Core.Models;

namespace Homework.App;

/// <summary>右侧班级筛选的一项。Class 为空表示「全部班级」。</summary>
public sealed class ClassFilterItemViewModel
{
    public ClassFilterItemViewModel(HomeworkClass? homeworkClass)
    {
        Class = homeworkClass;
    }

    public HomeworkClass? Class { get; }
    public string DisplayName => Class?.Name ?? "全部班级";
    public Guid? ClassId => Class?.Id;
}
