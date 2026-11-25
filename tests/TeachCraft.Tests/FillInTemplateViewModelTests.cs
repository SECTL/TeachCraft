using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Homework.App;
using Homework.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 模板对话框视图模型：选项组装、三种范围的目标路径、列数守卫与实际落盘。
/// 全部使用合成班级 / 姓名与临时目录，测试结束即删除；不依赖 UI 线程，不读截图。
/// </summary>
public sealed class FillInTemplateViewModelTests : IDisposable
{
    private readonly string _root;

    public FillInTemplateViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hw-template-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static (HomeworkBatch Batch, HomeworkClass C1, HomeworkClass C2) BatchWithClasses()
    {
        var batch = HomeworkBatch.Create("批次1");
        var c1 = new HomeworkClass(Guid.NewGuid(), "八年级1班");
        var c2 = new HomeworkClass(Guid.NewGuid(), "八年级2班");
        batch.LoadRoster([c1, c2], [
            new HomeworkStudent(Guid.NewGuid(), c1.Id, "001", "苏晚晴"),
            new HomeworkStudent(Guid.NewGuid(), c2.Id, "002", "陈屿"),
        ]);
        return (batch, c1, c2);
    }

    [Fact]
    public void Constructor_DefaultsColumnsToAutoColumns()
    {
        var (batch, _, _) = BatchWithClasses();

        var vm = new FillInTemplateViewModel(batch);

        Assert.Equal(batch.AutoColumns(), vm.Columns);
        Assert.Equal(3, vm.Columns); // 学号 001 / 002 → 最长 3 位
        Assert.Equal(FillInTemplateScope.AllClasses, vm.Scope);
        Assert.Equal(FillInTemplateFormat.Png, vm.Format);
    }

    [Fact]
    public void BuildOptions_CarriesClassNameAndColumns()
    {
        var (batch, _, _) = BatchWithClasses();
        var vm = new FillInTemplateViewModel(batch) { Columns = 4 };

        var options = vm.BuildOptions("八年级1班");

        Assert.Equal("作业填涂卡", options.Title);
        Assert.Equal("八年级1班", options.ClassName);
        Assert.Equal(4, options.Columns);
        Assert.Equal(10, options.Rows);
        Assert.Equal(300, options.Dpi);
    }

    [Fact]
    public void BuildTargets_OneClass_UsesSelectedClassAndFilePath()
    {
        var (batch, c1, _) = BatchWithClasses();
        var path = Path.Combine(_root, "one.png");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.OneClass,
            SelectedClass = c1,
            OutputPath = path,
        };

        var targets = vm.BuildTargets();

        Assert.Single(targets);
        Assert.Equal("八年级1班", targets[0].ClassName);
        Assert.Equal(path, targets[0].OutputPath);
    }

    [Fact]
    public void BuildTargets_OneClassWithoutSelection_IsEmpty()
    {
        var (batch, _, _) = BatchWithClasses();
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.OneClass,
            SelectedClass = null,
            OutputPath = Path.Combine(_root, "one.png"),
        };

        Assert.Empty(vm.BuildTargets());
        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public void BuildTargets_Generic_NullClassNameAndFilePath()
    {
        var (batch, _, _) = BatchWithClasses();
        var path = Path.Combine(_root, "generic.pdf");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.Generic,
            Format = FillInTemplateFormat.Pdf,
            OutputPath = path,
        };

        var targets = vm.BuildTargets();

        Assert.Single(targets);
        Assert.Null(targets[0].ClassName);
        Assert.Equal(path, targets[0].OutputPath);
    }

    [Fact]
    public void BuildTargets_AllClasses_OnePerClassWithPngExtension()
    {
        var (batch, _, _) = BatchWithClasses();
        var outDir = Path.Combine(_root, "out");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.AllClasses,
            Format = FillInTemplateFormat.Png,
            OutputPath = outDir,
        };

        var targets = vm.BuildTargets();

        Assert.Equal(2, targets.Count);
        Assert.Equal("八年级1班", targets[0].ClassName);
        Assert.Equal(Path.Combine(outDir, "填涂卡_八年级1班.png"), targets[0].OutputPath);
        Assert.Equal("八年级2班", targets[1].ClassName);
        Assert.Equal(Path.Combine(outDir, "填涂卡_八年级2班.png"), targets[1].OutputPath);
        Assert.All(targets, t => Assert.EndsWith(".png", t.OutputPath));
    }

    [Fact]
    public void BuildTargets_AllClasses_PdfUsesPdfExtension()
    {
        var (batch, _, _) = BatchWithClasses();
        var outDir = Path.Combine(_root, "out");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.AllClasses,
            Format = FillInTemplateFormat.Pdf,
            OutputPath = outDir,
        };

        var targets = vm.BuildTargets();

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.EndsWith(".pdf", t.OutputPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void ColumnsOutOfRange_DisablesGenerate(int columns)
    {
        var (batch, _, _) = BatchWithClasses();
        var vm = new FillInTemplateViewModel(batch)
        {
            OutputPath = Path.Combine(_root, "x.png"),
            Columns = columns,
        };

        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public void ColumnsInRange_EnablesGenerate()
    {
        var (batch, _, _) = BatchWithClasses();
        var vm = new FillInTemplateViewModel(batch)
        {
            OutputPath = Path.Combine(_root, "x.png"),
            Columns = 3,
        };

        Assert.True(vm.CanGenerate);
    }

    [Fact]
    public async Task Generate_WritesPngFileWithNonZeroLength()
    {
        var (batch, _, _) = BatchWithClasses();
        var path = Path.Combine(_root, "png", "card.png");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.Generic,
            Format = FillInTemplateFormat.Png,
            OutputPath = path,
            Columns = 3,
        };

        await vm.GenerateAsync();

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal("已生成 1 份模板。", vm.StatusText);
    }

    [Fact]
    public async Task Generate_WritesPdfFileWithNonZeroLength()
    {
        var (batch, _, _) = BatchWithClasses();
        var path = Path.Combine(_root, "pdf", "card.pdf");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.Generic,
            Format = FillInTemplateFormat.Pdf,
            OutputPath = path,
            Columns = 3,
        };

        await vm.GenerateAsync();

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal("已生成 1 份模板。", vm.StatusText);
    }

    [Fact]
    public async Task Generate_AllClasses_WritesOneFilePerClass()
    {
        var (batch, _, _) = BatchWithClasses();
        var outDir = Path.Combine(_root, "all");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.AllClasses,
            Format = FillInTemplateFormat.Png,
            OutputPath = outDir,
            Columns = 3,
        };

        await vm.GenerateAsync();

        var files = Directory.GetFiles(outDir, "*.png");
        Assert.Equal(2, files.Length);
        Assert.Contains(Path.Combine(outDir, "填涂卡_八年级1班.png"), files);
        Assert.Contains(Path.Combine(outDir, "填涂卡_八年级2班.png"), files);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 0));
        Assert.Equal("已生成 2 份模板。", vm.StatusText);
    }

    [Fact]
    public async Task Generate_InvalidColumns_WritesNothingAndReportsReason()
    {
        var (batch, _, _) = BatchWithClasses();
        var path = Path.Combine(_root, "never.png");
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.Generic,
            OutputPath = path,
            Columns = 0,
        };

        await vm.GenerateAsync();

        Assert.False(File.Exists(path));
        Assert.Contains("列数", vm.StatusText);
    }

    [Fact]
    public async Task Generate_ExistingTarget_RefusesOverwriteAndKeepsBytes()
    {
        var (batch, _, _) = BatchWithClasses();
        var path = Path.Combine(_root, "existing.png");
        var original = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, original);
        var vm = new FillInTemplateViewModel(batch)
        {
            Scope = FillInTemplateScope.Generic,
            Format = FillInTemplateFormat.Png,
            OutputPath = path,
            Columns = 3,
        };

        await vm.GenerateAsync();

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Contains("已存在", vm.StatusText);
        Assert.Contains("拒绝覆盖", vm.StatusText);
    }
}
