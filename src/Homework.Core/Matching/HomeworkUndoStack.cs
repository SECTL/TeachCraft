using Homework.Core.Models;

namespace Homework.Core.Matching;

/// <summary>
/// 整份快照式撤销栈：每条记录同时保存编辑前后的快照，撤销退回 before、重做前进到 after。
/// <para>
/// 内容相同的前后快照不入栈（滑块连发不能要按几十次）；撤销后再改动清空重做栈；
/// 超过容量丢最老的一步而不是停止记录。撤销 / 重做自身不入栈。
/// </para>
/// </summary>
public sealed class HomeworkUndoStack
{
    private readonly int _capacity;
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];

    public HomeworkUndoStack(int capacity = 100)
    {
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public void Record(string label, BatchEditSnapshot before, BatchEditSnapshot after)
    {
        if (before.Equals(after)) return;
        _undo.Add(new Edit(label, before, after));
        if (_undo.Count > _capacity) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>
    /// 退回上一步的 before 快照。<paramref name="current"/> 是调用方即将离开的状态；
    /// 栈内已存成对的 before / after，整条移到 redo 即可，无需用它重建。
    /// </summary>
    public BatchEditSnapshot? Undo(BatchEditSnapshot current)
    {
        if (_undo.Count == 0) return null;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(edit);
        return edit.Before;
    }

    /// <summary>前进到下一步的 after 快照；<paramref name="current"/> 语义同 Undo。</summary>
    public BatchEditSnapshot? Redo(BatchEditSnapshot current)
    {
        if (_redo.Count == 0) return null;
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(edit);
        return edit.After;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private sealed record Edit(string Label, BatchEditSnapshot Before, BatchEditSnapshot After);
}
