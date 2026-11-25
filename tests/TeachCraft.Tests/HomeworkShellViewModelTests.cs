using Homework.App;
using Homework.Core.Models;

namespace TeachCraft.Tests;

/// <summary>作业整理外壳：批次导航、忙碌状态与关窗守卫。全部使用合成姓名与临时文件。</summary>
public sealed class HomeworkShellViewModelTests : IDisposable
{
    private readonly string _root;

    public HomeworkShellViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hw-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string NewSource(string name)
    {
        var path = Path.Combine(_root, name + ".png");
        File.WriteAllText(path, "x");
        return path;
    }

    private static HomeworkPage Add(HomeworkBatch batch, string path)
    {
        Assert.True(batch.TryAddPage(path, HomeworkPageKind.Image, 1, "h" + Guid.NewGuid().ToString("N"),
            100, 200, out var page, out var error), error);
        return page!;
    }

    [Fact]
    public void BeginNewBatch_CreatesBatchAndOpensWorkspace()
    {
        var vm = new HomeworkViewModel();

        vm.BeginNewBatch();

        Assert.Single(vm.Batches);
        Assert.Equal("批次1", vm.Batches[0].Title);
        Assert.Equal("编辑中", vm.Batches[0].StateText);
        Assert.NotNull(vm.Workspace);
        Assert.True(vm.IsWorkspaceOpen);
        Assert.False(vm.IsHomeVisible);
    }

    [Fact]
    public void BeginNewBatch_Twice_IncrementsName()
    {
        var vm = new HomeworkViewModel();

        vm.BeginNewBatch();
        vm.BeginNewBatch();

        Assert.Equal(2, vm.Batches.Count);
        Assert.Equal("批次1", vm.Batches[0].Title);
        Assert.Equal("批次2", vm.Batches[1].Title);
        Assert.Equal("批次2", vm.Workspace!.Batch.Name);
    }

    [Fact]
    public void OpenBatch_AndBackToHome_RefreshesSummary()
    {
        var vm = new HomeworkViewModel();
        vm.BeginNewBatch();
        var first = vm.Batches[0];
        Add(vm.Workspace!.Batch, NewSource("a"));

        vm.BackToHome();

        Assert.Null(vm.Workspace);
        Assert.False(vm.IsWorkspaceOpen);
        Assert.True(vm.IsHomeVisible);
        Assert.Equal(1, first.PageCount);
        Assert.Contains("1 张页", first.Subtitle);

        vm.OpenBatch(first);
        Assert.NotNull(vm.Workspace);
        Assert.Equal(first.Batch.Id, vm.Workspace!.Batch.Id);
        Assert.True(vm.IsWorkspaceOpen);
        Assert.False(vm.IsHomeVisible);
    }

    [Fact]
    public void ShouldConfirmClose_OnlyForEditingBatchWithPages()
    {
        var vm = new HomeworkViewModel();
        Assert.False(vm.ShouldConfirmClose());

        vm.BeginNewBatch();
        Assert.False(vm.ShouldConfirmClose());          // 还没有页

        Add(vm.Workspace!.Batch, NewSource("a"));
        Assert.True(vm.ShouldConfirmClose());

        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var student = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        var workspace = vm.Workspace!;
        workspace.Batch.LoadRoster([cls], [student]);
        workspace.Batch.Match(workspace.Batch.Pages[0].Id, student.Id);
        workspace.MarkCommitted();
        Assert.False(vm.ShouldConfirmClose());          // 已提交
    }

    [Fact]
    public void ShouldConfirmClose_StillTrueAfterReturningHome()
    {
        var vm = new HomeworkViewModel();
        vm.BeginNewBatch();
        Add(vm.Workspace!.Batch, NewSource("a"));
        Assert.True(vm.ShouldConfirmClose());

        vm.BackToHome();

        Assert.Null(vm.Workspace);
        Assert.True(vm.ShouldConfirmClose());
    }

    [Fact]
    public void AbandonFromWorkspace_ReturnsHome()
    {
        var vm = new HomeworkViewModel();
        vm.BeginNewBatch();
        Add(vm.Workspace!.Batch, NewSource("a"));

        vm.Workspace!.Abandon();

        Assert.Null(vm.Workspace);
        Assert.True(vm.IsHomeVisible);
        Assert.Equal("已放弃", vm.Batches[0].StateText);
    }

    [Fact]
    public void BusyState_BeginCancelEnd()
    {
        var vm = new HomeworkViewModel();

        vm.BeginBusy("正在导入");
        Assert.True(vm.IsBusy);
        Assert.Equal("正在导入", vm.BusyText);
        Assert.False(vm.CancelRequested);

        vm.RequestCancel();
        Assert.True(vm.CancelRequested);
        Assert.Contains("取消", vm.BusyText);

        vm.EndBusy();
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.BusyText);
        Assert.False(vm.CancelRequested);
    }
}
