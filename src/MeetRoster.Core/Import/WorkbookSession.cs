using ClosedXML.Excel;

namespace MeetRoster.Core.Import;

/// <summary>
/// 一次导入会话：把「共享读的文件流」与「ClosedXML 工作簿」绑在一起，
/// 让「先判断这是什么表、再整本读取」只打开一次文件。
/// <para>
/// 为什么需要它：秩序册导入原先分三处各自 <c>new XLWorkbook(流)</c>——判断类型、
/// 读名单、读赛程各一遍，60 个班的秩序册要把同一份文件解析三次。现在由调用方开一次会话，
/// 把 <see cref="Workbook"/> 直接交给 <see cref="OrderBookImporter.DetectKind(XLWorkbook)"/>
/// 与 <see cref="OrderBookImporter.Import(string, XLWorkbook)"/>。
/// </para>
/// <para>
/// 生命周期：文件流与工作簿都归本类释放，调用方只管 <c>using</c>；导入器只读使用，
/// 不接管工作簿。文件以共享读打开，会话存活期间 Excel 仍能同时打开同一份文件。
/// </para>
/// </summary>
public sealed class WorkbookSession : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private WorkbookSession(FileStream stream, XLWorkbook workbook)
    {
        _stream = stream;
        Workbook = workbook;
    }

    /// <summary>已经打开、可以直接交给导入器的工作簿；由本会话负责释放，调用方不要 Dispose 它。</summary>
    public XLWorkbook Workbook { get; }

    /// <summary>打开一份工作簿。读不了时抛中文 <see cref="InvalidDataException"/>。</summary>
    public static WorkbookSession Open(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("找不到工作簿：" + path);

        var stream = WorkbookIO.OpenReadShared(path);
        try
        {
            return new WorkbookSession(stream, WorkbookIO.OpenWorkbook(path, stream));
        }
        catch
        {
            // 构造工作簿失败时流没人接手，必须在这里收掉，否则文件句柄一直挂着。
            stream.Dispose();
            throw;
        }
    }

    /// <summary>重复调用安全：先放工作簿（ClosedXML 释放时可能还要读包结构），再关文件流。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Workbook.Dispose();
        _stream.Dispose();
    }
}
