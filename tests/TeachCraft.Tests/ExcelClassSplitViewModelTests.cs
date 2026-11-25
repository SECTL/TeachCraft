using TeachCraft.App.ViewModels;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class ExcelClassSplitViewModelTests
{
    [Theory]
    [InlineData("synthetic.xlsx")]
    [InlineData("synthetic.XLS")]
    public void AcceptsOneWorkbookButCannotRunWithoutInspection(string input)
    {
        var viewModel = new MainViewModel(showOobe: false);
        var tool = Assert.IsType<ToolItemViewModel>(viewModel.FindTool("excel-class-split"));

        Assert.Equal(ToolKind.ExcelClassSplit, tool.Kind);
        Assert.Equal("12", tool.No);
        Assert.Equal("Excel按班拆表", tool.Name);
        Assert.False(tool.IsMultiFile);
        Assert.True(tool.UsesOutputFolder);
        Assert.False(tool.CanRun);

        Assert.True(tool.TrySelectExcelClassSplitInput(new[] { input }));
        Assert.Equal(input, Assert.Single(tool.Files));
        Assert.False(tool.CanRun);
    }

    [Fact]
    public void RejectsInvalidOrMultipleFilesWithoutReplacingSelection()
    {
        var tool = new MainViewModel(showOobe: false).FindTool("excel-class-split")!;
        Assert.True(tool.TrySelectExcelClassSplitInput(new[] { "first.xlsx" }));

        Assert.False(tool.TrySelectExcelClassSplitInput(new[] { "wrong.csv" }));
        Assert.False(tool.TrySelectExcelClassSplitInput(new[] { "second.xlsx", "third.xls" }));
        Assert.Equal("first.xlsx", Assert.Single(tool.Files));

        tool.ReplaceFiles(Array.Empty<string>());
        Assert.False(tool.HasFiles);
        Assert.False(tool.CanRun);
    }
}
