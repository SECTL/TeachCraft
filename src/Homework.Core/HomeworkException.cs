
namespace Homework.Core;

/// <summary>面向老师的可读失败：文案由抛出方给出，界面原样展示。</summary>
public sealed class HomeworkException : Exception
{
    public HomeworkException(string message) : base(message)
    {
    }

    public HomeworkException(string message, Exception inner) : base(message, inner)
    {
    }

    public string UserFacingMessage => Message;
}
