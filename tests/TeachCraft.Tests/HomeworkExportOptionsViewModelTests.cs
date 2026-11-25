using Homework.App;
using Homework.Core.Export;
using Homework.Core.Models;

namespace TeachCraft.Tests;

/// <summary>导出设置视图模型：预览摘要、冲突优先、选项往返。全部使用合成姓名与临时文件。</summary>
public sealed class HomeworkExportOptionsViewModelTests : IDisposable
{
    private readonly string _root;

    public HomeworkExportOptionsViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hw-export-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string OutputDir => Path.Combine(_root, "out");

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

    private static (HomeworkBatch Batch, HomeworkStudent A, HomeworkStudent B) BatchWithRoster()
    {
        var batch = HomeworkBatch.Create("批次1");
        var cls = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var a = new HomeworkStudent(Guid.NewGuid(), cls.Id, "01", "苏晚晴");
        var b = new HomeworkStudent(Guid.NewGuid(), cls.Id, "02", "陈屿");
        batch.LoadRoster([cls], [a, b]);
        return (batch, a, b);
    }

    private sealed class FakeProbe : IFileProbe
    {
        public Func<string, bool>? OnFileExists { get; init; }
        public bool FileExists(string path) => OnFileExists?.Invoke(path) ?? false;
        public bool DirectoryExists(string path) => false;
    }

    [Fact]
    public void UpdatePreview_SummaryTextAndCounts()
    {
        var (batch, a, b) = BatchWithRoster();
        Add(batch, NewSource("a"));
        Add(batch, NewSource("b"));
        Add(batch, NewSource("c"));
        batch.Match(batch.Pages[0].Id, a.Id);
        batch.Match(batch.Pages[1].Id, a.Id);
        batch.Match(batch.Pages[2].Id, b.Id);
        var vm = new ExportOptionsViewModel { OutputDirectory = OutputDir };

        vm.UpdatePreview(batch, new FakeProbe());

        Assert.Contains("将写入 3 个文件 · 1 个班级 · 2 位学生", vm.PreviewText);
        Assert.Equal(3, vm.PageCount);
        Assert.Equal(2, vm.StudentCount);
        Assert.Empty(vm.Conflicts);
        Assert.True(vm.CanExport);
        Assert.Equal(string.Empty, vm.BlockedReason);
    }

    [Fact]
    public void UpdatePreview_ConflictsTakePrecedence()
    {
        var (batch, a, _) = BatchWithRoster();
        Add(batch, NewSource("a"));
        batch.Match(batch.Pages[0].Id, a.Id);
        var target = Path.Combine(OutputDir, "八年级1班", "八年级1班_01_苏晚晴_第1页.png");
        var probe = new FakeProbe
        {
            OnFileExists = path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase),
        };
        var vm = new ExportOptionsViewModel { OutputDirectory = OutputDir };

        vm.UpdatePreview(batch, probe);

        Assert.NotEmpty(vm.Conflicts);
        Assert.Contains("冲突", vm.PreviewText);
        Assert.False(vm.CanExport);
        Assert.Contains("冲突", vm.BlockedReason);
    }

    [Fact]
    public void UpdatePreview_EmptyBatch_SurfacesChineseError()
    {
        var vm = new ExportOptionsViewModel { OutputDirectory = OutputDir };

        vm.UpdatePreview(HomeworkBatch.Create("空"), new FakeProbe());

        Assert.False(vm.CanExport);
        Assert.NotEmpty(vm.Conflicts);
        Assert.Contains("页", vm.PreviewText);
    }

    [Fact]
    public void ChangingMode_RefreshesPreview()
    {
        var (batch, a, b) = BatchWithRoster();
        Add(batch, NewSource("a"));
        Add(batch, NewSource("b"));
        batch.Match(batch.Pages[0].Id, a.Id);
        batch.Match(batch.Pages[1].Id, b.Id);
        var vm = new ExportOptionsViewModel { OutputDirectory = OutputDir };
        vm.UpdatePreview(batch, new FakeProbe());
        Assert.Equal(2, vm.PageCount);

        vm.Mode = HomeworkExportMode.MergePdf;

        Assert.Equal(0, vm.PageCount);
        Assert.Equal(2, vm.StudentCount);
        Assert.Contains("将写入 2 个文件", vm.PreviewText);
    }

    [Fact]
    public void BuildOptions_RoundTripsSettings()
    {
        var vm = new ExportOptionsViewModel
        {
            OutputDirectory = Path.Combine(_root, "导出"),
            Mode = HomeworkExportMode.CopyToFolders,
            OverwriteExisting = true,
        };

        var options = vm.BuildOptions();

        Assert.Equal(Path.Combine(_root, "导出"), options.OutputDirectory);
        Assert.Equal(HomeworkExportMode.CopyToFolders, options.Mode);
        Assert.True(options.OverwriteExisting);
    }

    [Fact]
    public void Modes_CoversAllFourExportFormats()
    {
        var vm = new ExportOptionsViewModel();

        Assert.Equal(4, vm.Modes.Count);
        Assert.Equal(new[]
        {
            HomeworkExportMode.RenameArchive,
            HomeworkExportMode.MergePdf,
            HomeworkExportMode.ArchiveWithIndex,
            HomeworkExportMode.CopyToFolders,
        }, vm.Modes.Select(m => m.Mode));
        Assert.All(vm.Modes, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Title));
            Assert.False(string.IsNullOrWhiteSpace(m.Description));
        });
    }
}
