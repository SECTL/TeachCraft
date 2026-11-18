using GradeWeave.App;
using GradeWeave.Core.Models;

namespace GradeWeave.Tests;

/// <summary>导出设置弹窗的选项校验与请求组装（纯 VM，不依赖 Avalonia 运行时）。</summary>
public sealed class ExportOptionsTests
{
    [Fact]
    public void Excel_NoSheetSelected_RejectsWithMessage()
    {
        var vm = NewViewModel();
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Excel);
        vm.IncludeMaster = false;
        vm.IncludeAssignment = false;
        vm.IncludeTeacherPosition = false;
        vm.IncludeClassSheets = false;
        vm.IncludeTeacherSheets = false;
        vm.IncludeConflict = false;

        Assert.False(vm.TryBuild(out var request, out var error));
        Assert.Null(request);
        Assert.Contains("至少勾选", error, StringComparison.Ordinal);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void Excel_ReferenceStyle_CarriesSheetToggles()
    {
        var vm = NewViewModel();
        vm.SelectedStyle = vm.Styles.First(s => s.Style == WeaveExportStyle.Reference);
        vm.IncludeClassSheets = false;
        vm.IncludeConflict = false;

        Assert.True(vm.TryBuild(out var request, out _));
        Assert.NotNull(request);
        Assert.Equal(WeaveExportFormat.Excel, request!.Format);
        Assert.Equal(WeaveExportStyle.Reference, request.Options.Style);
        Assert.True(request.Options.IncludeMasterSheet);
        Assert.True(request.Options.IncludeTeacherPositionSheet);
        Assert.False(request.Options.IncludeClassSheets);
        Assert.False(request.Options.IncludeConflictSheet);
        Assert.Equal("全年级", request.TargetLabel);
    }

    [Fact]
    public void Excel_SoftwareStyle_IsDefault()
    {
        var vm = NewViewModel();

        Assert.True(vm.TryBuild(out var request, out _));
        Assert.Equal(WeaveExportStyle.Software, request!.Options.Style);
        Assert.True(request.Options.IncludeTeacherPositionSheet);
    }

    [Fact]
    public void Png_WithoutTarget_Rejects()
    {
        var vm = new ExportOptionsViewModel(WeaveSamples.CreateDemo(2), null, null, false);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Png);

        Assert.False(vm.TryBuild(out _, out var error));
        // 文案在 1.34.x 改为「请选择要导出的班级或教师」——弹窗里现在能直接选，
        // 不再要求老师「先回主窗口选」。
        Assert.Contains("选择要导出的班级或教师", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Png_WithClass_CarriesClassId()
    {
        var project = WeaveSamples.CreateDemo(2);
        var vm = new ExportOptionsViewModel(project, project.Classes[0], null, false);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Png);

        Assert.True(vm.TryBuild(out var request, out _));
        Assert.Equal(WeaveExportFormat.Png, request!.Format);
        Assert.Equal(project.Classes[0].Id, request.ClassId);
        Assert.Equal(project.Classes[0].Name, request.TargetLabel);
    }

    [Fact]
    public void Pdf_WithTeacher_CarriesTeacherId()
    {
        var project = WeaveSamples.CreateDemo(2);
        var vm = new ExportOptionsViewModel(project, null, project.Teachers[0], teacherView: true);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Pdf);

        Assert.True(vm.TryBuild(out var request, out _));
        Assert.Equal(project.Teachers[0].Id, request!.TeacherId);
        Assert.Null(request.ClassId);
    }

    [Fact]
    public void StyleName_EditsReferenceStyleLabel()
    {
        var vm = NewViewModel();
        vm.ReferenceStyleName = "本机自定义样式";

        Assert.Equal("本机自定义样式", vm.Styles[1].Label);
    }

    [Fact]
    public void Seewo_Style_IsOfferedAfterReference()
    {
        var vm = NewViewModel();

        Assert.Equal(3, vm.Styles.Count);
        Assert.Equal(WeaveExportStyle.Seewo, vm.Styles[2].Style);
        Assert.Contains("希沃", vm.Styles[2].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Seewo_Style_IgnoresSheetTogglesAndBuilds()
    {
        var vm = NewViewModel();
        vm.SelectedStyle = vm.Styles.First(s => s.Style == WeaveExportStyle.Seewo);
        vm.IncludeMaster = false;
        vm.IncludeAssignment = false;
        vm.IncludeTeacherPosition = false;
        vm.IncludeClassSheets = false;
        vm.IncludeTeacherSheets = false;
        vm.IncludeConflict = false;

        // 希沃模板只有一张表，分页勾选对它不生效——不该被「至少勾选一页」拦住。
        Assert.True(vm.TryBuild(out var request, out var error));
        Assert.Empty(error);
        Assert.Equal(WeaveExportStyle.Seewo, request!.Options.Style);
        Assert.True(vm.IsSeewoStyle);
        Assert.False(vm.ShowSheetChoices);
        Assert.Equal("全年级", request.TargetLabel);
    }

    [Fact]
    public void Seewo_Style_HidesSheetChoicesButShowsExplanation()
    {
        var vm = NewViewModel();
        Assert.True(vm.ShowSheetChoices);

        vm.SelectedStyle = vm.Styles.First(s => s.Style == WeaveExportStyle.Seewo);

        Assert.False(vm.ShowSheetChoices);
        Assert.Contains("希沃", vm.StyleHint, StringComparison.Ordinal);
        Assert.Contains("原样保留", vm.StyleHint, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingToPdf_HidesExcelStyleChoices()
    {
        var vm = NewViewModel();
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Pdf);

        Assert.False(vm.IsExcel);
        Assert.False(vm.ShowSheetChoices);
        Assert.False(vm.ShowReferenceName);

        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Excel);
        Assert.True(vm.ShowSheetChoices);
    }

    [Fact]
    public void SelectingTeacher_ResetsClassSide_TeacherWins()
    {
        var project = WeaveSamples.CreateDemo(2);
        // 主窗口带着当前班级打开弹窗：构造写入班级初始值（审计 P2-9 的病灶现场）。
        var vm = new ExportOptionsViewModel(project, project.Classes[0], null, teacherView: false);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Png);

        // 用户改选教师：班级侧被重置为「未选择」，教师侧生效。
        // 修复前必红——旧实现班级值仍在，ResolveTarget 按班级优先把教师选择静默压住。
        vm.SelectedTeacherTarget = vm.TeacherTargets.First(o => o.Id == project.Teachers[0].Id);

        Assert.Null(vm.SelectedClassTarget.Id);
        Assert.True(vm.TryBuild(out var request, out _));
        Assert.Equal(project.Teachers[0].Id, request!.TeacherId);
        Assert.Null(request.ClassId);
    }

    [Fact]
    public void SelectingClass_ResetsTeacherSide_ClassWins()
    {
        var project = WeaveSamples.CreateDemo(2);
        var vm = new ExportOptionsViewModel(project, null, project.Teachers[0], teacherView: true);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == WeaveExportFormat.Pdf);

        // 反向同理：用户改选班级时教师侧重置，班级侧生效（修复前必红）。
        vm.SelectedClassTarget = vm.ClassTargets.First(o => o.Id == project.Classes[0].Id);

        Assert.Null(vm.SelectedTeacherTarget.Id);
        Assert.True(vm.TryBuild(out var request, out _));
        Assert.Equal(project.Classes[0].Id, request!.ClassId);
        Assert.Null(request.TeacherId);
    }

    [Fact]
    public void Constructor_InitialValues_DoNotTriggerMutex()
    {
        var project = WeaveSamples.CreateDemo(2);
        var vm = new ExportOptionsViewModel(project, project.Classes[0], project.Teachers[0], teacherView: false);

        // 构造写入不算用户改动：两侧初始值并存，仍由 ResolveTarget 的既有顺序裁决。
        Assert.Equal(project.Classes[0].Id, vm.SelectedClassTarget.Id);
        Assert.Equal(project.Teachers[0].Id, vm.SelectedTeacherTarget.Id);
        Assert.Contains("二选一", vm.TargetHint, StringComparison.Ordinal);
    }

    private static ExportOptionsViewModel NewViewModel()
    {
        var project = WeaveSamples.CreateDemo(4);
        return new ExportOptionsViewModel(project, project.Classes[0], null, teacherView: false);
    }
}
