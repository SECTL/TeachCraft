using Homework.App;
using Homework.Core.Models;
using Homework.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>
/// 名单重导后的状态提示。全部使用合成姓名（苏晚晴 / 陈屿 / 许砚），
/// 页来源用临时文件，测试结束即删除。清空 Recognition 已由 HomeworkRecognitionTests 覆盖。
/// </summary>
public sealed class HomeworkRosterReloadTests : IDisposable
{
    private readonly string _root;

    public HomeworkRosterReloadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hw-reload-" + Guid.NewGuid().ToString("N"));
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

    private static (HomeworkBatch Batch, HomeworkClass Cls) NewBatchWithPage(string sourcePath)
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "001", "苏晚晴");
        batch.LoadRoster([cls], [a]);
        Assert.True(batch.TryAddPage(sourcePath, HomeworkPageKind.Image, 1, "h" + Guid.NewGuid().ToString("N"),
            100, 200, out _, out var error), error);
        return (batch, cls);
    }

    private static RosterImportResult ImportResult(HomeworkClass cls, params HomeworkStudent[] students) =>
        new([cls], students, [], 0);

    private static RosterPasteResult PasteResult(HomeworkClass cls, params HomeworkStudent[] students) =>
        new([cls], students, []);

    [Fact]
    public void ApplyRoster_SetsReRecognitionStatus()
    {
        var (batch, cls) = NewBatchWithPage(NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        var next = new HomeworkStudent(Guid.NewGuid(), cls.Id, "002", "陈屿");

        vm.ApplyRoster(ImportResult(cls, next));

        Assert.Contains("名单已更换，请重新识别", vm.StatusText);
    }

    [Fact]
    public void ApplyPaste_SetsReRecognitionStatus()
    {
        var (batch, cls) = NewBatchWithPage(NewSource("a"));
        var vm = new BatchWorkspaceViewModel(batch);
        var next = new HomeworkStudent(Guid.NewGuid(), cls.Id, "002", "陈屿");

        vm.ApplyPaste(PasteResult(cls, next));

        Assert.Contains("名单已更换，请重新识别", vm.StatusText);
    }

    [Fact]
    public void ApplyRoster_ExplicitColumnsDifferFromAuto_AppendsWarning()
    {
        var (batch, cls) = NewBatchWithPage(NewSource("a"));
        batch.SetFillInColumns(4); // 显式 4 列
        var vm = new BatchWorkspaceViewModel(batch);
        // 新名单最长学号 3 位（"003"），AutoColumns()==3，与显式 4 不同
        var next = new HomeworkStudent(Guid.NewGuid(), cls.Id, "003", "许砚");

        vm.ApplyRoster(ImportResult(cls, next));

        Assert.Contains("名单已更换，请重新识别", vm.StatusText);
        Assert.Contains("当前列数 4 与名单最长学号 3 位不同，请确认模板列数。", vm.StatusText);
    }
}
