using GradeWeave.App;
using GradeWeave.Core.Import;
using GradeWeave.Core.Models;
using GradeWeave.Core.Project;

namespace GradeWeave.Tests;

/// <summary>
/// 拖拽导入的两条不变量（审计 P1-4 / T12）：有工程或恢复文件时先确认；
/// 解析成功后才清恢复文件。窗口不能 headless 构造，可测面放在
/// <see cref="WorkbookImportGuard"/> 与真实导入器的组合上。
/// </summary>
public sealed class DragImportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "GradeWeave.DragImport", Guid.NewGuid().ToString("N"));

    public DragImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // --------------------------- 确认触发条件 ---------------------------

    [Fact]
    public void DropWithExistingProjectNeedsConfirm()
    {
        Assert.True(WorkbookImportGuard.NeedsSwitchConfirm(hasProject: true, recovery: null));
    }

    [Fact]
    public void DropWithOnlyRecoveryFileNeedsConfirm()
    {
        // 没有工程但本机有崩溃恢复文件：导入会把它清掉，同样必须先问。
        var store = new WeaveAutosaveStore(_directory);
        var project = WeaveSamples.CreateDemo();
        var info = WeaveAutosaveInfo.FromProject(project, null);
        store.SaveJson(WeaveProjectIO.Serialize(project), null, info);

        Assert.True(WorkbookImportGuard.NeedsSwitchConfirm(
            hasProject: false, recovery: store.TryGetInfo()));
    }

    [Fact]
    public void EmptyStateDropSkipsConfirm()
    {
        // 空状态（无工程、无恢复文件）走查与首次使用都不该被确认挡住。
        Assert.False(WorkbookImportGuard.NeedsSwitchConfirm(hasProject: false, recovery: null));
    }

    // --------------------------- 恢复文件存活 ---------------------------

    [Fact]
    public async Task CancelledDropKeepsRecoveryFile()
    {
        var store = new WeaveAutosaveStore(_directory);
        var project = WeaveSamples.CreateDemo();
        store.SaveJson(
            WeaveProjectIO.Serialize(project), null, WeaveAutosaveInfo.FromProject(project, null));
        Assert.NotNull(store.TryGetInfo());

        var workbook = SyntheticWorkbookFactory.CreateWorkbook();
        try
        {
            // 老师点「留在当前工程」：确认返回 false，窗口在 OnDrop 里直接 return，
            // 导入器不会被调用，恢复文件自然原样留着。
            var confirmAsked = false;
            var proceed = await WorkbookImportGuard.ConfirmDropAsync(
                workbook,
                hasProject: false,
                recovery: store.TryGetInfo(),
                confirmSwitch: (_, _) =>
                {
                    confirmAsked = true;
                    return Task.FromResult(false);
                },
                onStatus: _ => { });

            Assert.True(confirmAsked, "有恢复文件时拖入必须先问一句");
            Assert.False(proceed);
            Assert.NotNull(store.TryGetInfo());
            Assert.True(store.TryLoad(out _, out _), "取消后恢复文件必须仍然可恢复");
        }
        finally
        {
            SyntheticWorkbookFactory.Cleanup(workbook);
        }
    }

    [Fact]
    public async Task EmptyStateDropProceedsWithoutConfirm()
    {
        var workbook = SyntheticWorkbookFactory.CreateWorkbook();
        try
        {
            var confirmAsked = false;
            var proceed = await WorkbookImportGuard.ConfirmDropAsync(
                workbook,
                hasProject: false,
                recovery: null,
                confirmSwitch: (_, _) =>
                {
                    confirmAsked = true;
                    return Task.FromResult(true);
                },
                onStatus: _ => { });

            Assert.False(confirmAsked, "空状态拖入不该被确认挡住");
            Assert.True(proceed);
        }
        finally
        {
            SyntheticWorkbookFactory.Cleanup(workbook);
        }
    }

    [Fact]
    public async Task ExistingProjectDropProceedsOnlyAfterConfirm()
    {
        var workbook = SyntheticWorkbookFactory.CreateWorkbook();
        try
        {
            var askedAction = string.Empty;
            var proceed = await WorkbookImportGuard.ConfirmDropAsync(
                workbook,
                hasProject: true,
                recovery: null,
                confirmSwitch: (action, _) =>
                {
                    askedAction = action;
                    return Task.FromResult(true);
                },
                onStatus: _ => { });

            Assert.Equal("拖入导入", askedAction);
            Assert.True(proceed);
        }
        finally
        {
            SyntheticWorkbookFactory.Cleanup(workbook);
        }
    }

    [Fact]
    public async Task UnsupportedExtensionIsRejectedBeforeConfirm()
    {
        var statuses = new List<string>();
        var confirmAsked = false;
        var proceed = await WorkbookImportGuard.ConfirmDropAsync(
            Path.Combine(_directory, "座位表.pdf"),
            hasProject: true,
            recovery: null,
            confirmSwitch: (_, _) =>
            {
                confirmAsked = true;
                return Task.FromResult(true);
            },
            onStatus: statuses.Add);

        Assert.False(proceed);
        Assert.False(confirmAsked, "扩展名不符时先报格式，不该先弹切换确认");
        Assert.Contains(statuses, text => text.Contains(".xlsx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedImportKeepsRecoveryFile()
    {
        var store = new WeaveAutosaveStore(_directory);
        var project = WeaveSamples.CreateDemo();
        store.SaveJson(
            WeaveProjectIO.Serialize(project), null, WeaveAutosaveInfo.FromProject(project, null));

        // 坏文件：扩展名合法但内容不是工作簿。
        var broken = Path.Combine(_directory, "坏掉的课表.xlsx");
        await File.WriteAllTextAsync(broken, "这不是一份 Excel 工作簿");

        var resetCalled = false;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            WorkbookImportGuard.ImportThenResetSessionAsync(
                broken,
                path => new WeaveWorkbookImporter().Import(path),
                () => resetCalled = true));

        Assert.False(resetCalled, "解析失败时不得开始新会话（那会清掉恢复文件）");
        Assert.NotNull(store.TryGetInfo());
        Assert.True(store.TryLoad(out var restored, out _), "导入失败后恢复文件必须仍然可恢复");
        Assert.Equal(project.Classes.Count, restored!.Classes.Count);
    }

    [Fact]
    public async Task SuccessfulImportResetsSessionAfterParse()
    {
        var store = new WeaveAutosaveStore(_directory);
        var project = WeaveSamples.CreateDemo();
        store.SaveJson(
            WeaveProjectIO.Serialize(project), null, WeaveAutosaveInfo.FromProject(project, null));

        var workbook = SyntheticWorkbookFactory.CreateWorkbook();
        try
        {
            var resetCalled = false;
            var result = await WorkbookImportGuard.ImportThenResetSessionAsync(
                workbook,
                path => new WeaveWorkbookImporter().Import(path),
                () =>
                {
                    resetCalled = true;
                    store.Clear();
                });

            Assert.True(resetCalled);
            Assert.NotEmpty(result.Project.Classes);
            Assert.Null(store.TryGetInfo());
        }
        finally
        {
            SyntheticWorkbookFactory.Cleanup(workbook);
        }
    }

    [Fact]
    public async Task ResetSessionRunsBeforeImportResultIsHandedToViewModel()
    {
        // 顺序是「解析 → 开新会话 → LoadImportResult」：先开会话再装数据，
        // 否则自动保存的版本号会对着旧工程拍快照。
        var order = new List<string>();
        var workbook = SyntheticWorkbookFactory.CreateWorkbook();
        try
        {
            await WorkbookImportGuard.ImportThenResetSessionAsync(
                workbook,
                path =>
                {
                    order.Add("import");
                    return new WeaveWorkbookImporter().Import(path);
                },
                () => order.Add("reset"));

            Assert.Equal(["import", "reset"], order);
        }
        finally
        {
            SyntheticWorkbookFactory.Cleanup(workbook);
        }
    }
}
