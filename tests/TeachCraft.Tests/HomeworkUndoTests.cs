using Homework.Core.Matching;
using Homework.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 作业整理的快照撤销栈：内容去重、重做清空、容量丢最老、快照值相等。
/// </summary>
public sealed class HomeworkUndoTests
{
    private static BatchEditSnapshot Snap(Guid[] order, params (Guid Page, Guid? Student)[] matches)
    {
        var dict = new Dictionary<Guid, Guid?>();
        foreach (var (page, student) in matches) dict[page] = student;
        return new BatchEditSnapshot(order, dict);
    }

    // ----------------------------- 栈行为 -----------------------------

    [Fact]
    public void EmptyStack_CannotUndoOrRedo()
    {
        var stack = new HomeworkUndoStack();
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Null(stack.UndoLabel);
        Assert.Null(stack.RedoLabel);
        Assert.Null(stack.Undo(Snap([])));
        Assert.Null(stack.Redo(Snap([])));
    }

    [Fact]
    public void RecordThenUndo_ReturnsBefore()
    {
        var stack = new HomeworkUndoStack();
        var p = Guid.NewGuid();
        var before = Snap([p], (p, null));
        var after = Snap([p], (p, Guid.NewGuid()));

        stack.Record("匹配学生", before, after);
        Assert.True(stack.CanUndo);
        Assert.Equal("匹配学生", stack.UndoLabel);

        var restored = stack.Undo(after);
        Assert.Equal(before, restored);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
        Assert.Equal("匹配学生", stack.RedoLabel);
    }

    [Fact]
    public void UndoThenRedo_ReturnsAfter()
    {
        var stack = new HomeworkUndoStack();
        var p = Guid.NewGuid();
        var before = Snap([p], (p, null));
        var after = Snap([p], (p, Guid.NewGuid()));

        stack.Record("匹配学生", before, after);
        var undone = stack.Undo(after)!;
        var redone = stack.Redo(undone)!;

        Assert.Equal(after, redone);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void ContentEqualRecord_IsSkipped()
    {
        var stack = new HomeworkUndoStack();
        var p = Guid.NewGuid();
        var state = Snap([p], (p, null));

        stack.Record("第一次", state, state);
        stack.Record("第二次同内容", state, Snap([p], (p, null)));

        Assert.False(stack.CanUndo);
        Assert.Null(stack.Undo(state));
    }

    [Fact]
    public void NewRecordAfterUndo_ClearsRedo()
    {
        var stack = new HomeworkUndoStack();
        var p = Guid.NewGuid();
        stack.Record("A", Snap([p], (p, null)), Snap([p], (p, Guid.NewGuid())));
        stack.Record("B", Snap([p], (p, Guid.NewGuid())), Snap([p], (p, Guid.NewGuid())));

        stack.Undo(Snap([p]));
        Assert.True(stack.CanRedo);

        stack.Record("C", Snap([p]), Snap([p], (p, Guid.NewGuid())));
        Assert.False(stack.CanRedo);
        Assert.Equal("C", stack.UndoLabel);
    }

    [Fact]
    public void CapacityDropsOldestNotNewest()
    {
        var stack = new HomeworkUndoStack(capacity: 3);
        var p = Guid.NewGuid();
        for (var i = 1; i <= 5; i++)
        {
            stack.Record($"第{i}步", Snap([p], (p, null)), Snap([p], (p, Guid.NewGuid())));
        }

        Assert.Equal("第5步", stack.UndoLabel);
        stack.Undo(Snap([p]));
        Assert.Equal("第4步", stack.UndoLabel);
        stack.Undo(Snap([p]));
        Assert.Equal("第3步", stack.UndoLabel);
        stack.Undo(Snap([p]));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var stack = new HomeworkUndoStack();
        var p = Guid.NewGuid();
        stack.Record("A", Snap([p], (p, null)), Snap([p], (p, Guid.NewGuid())));
        stack.Undo(Snap([p]));
        Assert.True(stack.CanRedo);

        stack.Clear();
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Null(stack.UndoLabel);
        Assert.Null(stack.RedoLabel);
    }

    // ----------------------------- 快照值相等 -----------------------------

    [Fact]
    public void SnapshotEquality_UsesSequenceForOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(Snap([a, b]), Snap([a, b]));
        Assert.NotEqual(Snap([a, b]), Snap([b, a]));
    }

    [Fact]
    public void SnapshotEquality_DictionaryIsOrderIndependent()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        var left = new BatchEditSnapshot([p1, p2], new Dictionary<Guid, Guid?>
        {
            [p1] = s1, [p2] = s2,
        });
        var right = new BatchEditSnapshot([p1, p2], new Dictionary<Guid, Guid?>
        {
            [p2] = s2, [p1] = s1,   // 插入顺序相反
        });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void SnapshotEquality_DifferentMatchIsNotEqual()
    {
        var p = Guid.NewGuid();
        Assert.NotEqual(Snap([p], (p, null)), Snap([p], (p, Guid.NewGuid())));
    }
}
