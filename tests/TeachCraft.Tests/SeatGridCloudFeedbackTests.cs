using System;
using System.Linq;
using SeatGrid.App;
using SeatGrid.App.Cloud;
using SeatGrid.Core;
using SeatGrid.Core.Models;
using SeatGrid.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>
/// 两处「点了没反应」的回归：云端窗口空状态按钮必须给出中文提示，
/// 归位主窗口准备云端座位表失败必须弹模态框说明原因。
/// </summary>
public sealed class SeatGridCloudFeedbackTests
{
    private static void AssertChinese(string message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains(message, c => c is >= '一' and <= '鿿');
    }

    [Fact]
    public void UploadDocument_WithoutDocument_ShowsErrorNotSilentReturn()
    {
        var message = SeatGridCloudWindow.DescribeMissingDocumentMessage();
        AssertChinese(message);
        Assert.Contains("共享名单", message);
        Assert.Contains("上传座位表", message);
        Assert.Contains("页签", message);

        var source = RepoRoot.Read("src/SeatGrid.App/Cloud/SeatGridCloudWindow.cs");
        Assert.DoesNotContain("if (_document is null) return;", source);
        Assert.Contains("ShowError(DescribeMissingDocumentMessage());", source);
        Assert.Contains("_error.IsVisible = true;", source);
    }

    [Fact]
    public void UploadRoster_WithoutStudents_ShowsErrorNotSilentReturn()
    {
        var message = SeatGridCloudWindow.DescribeMissingRosterMessage();
        AssertChinese(message);
        Assert.Contains("名单", message);

        var source = RepoRoot.Read("src/SeatGrid.App/Cloud/SeatGridCloudWindow.cs");
        Assert.DoesNotContain("if (_students is null || _students.Count == 0) return;", source);
        Assert.Contains("ShowError(DescribeMissingRosterMessage());", source);
    }

    [Fact]
    public void BuildCloudDocument_WithoutLinkedRoster_ThrowsChineseMessage()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(new RosterImportResult(
            string.Empty, "合成名单",
            new[] { new Student { Name = "苏晚晴", Gender = Gender.Female, Number = "S0001" } },
            Array.Empty<string>()));

        var ex = Assert.Throws<SeatGridException>(() => vm.BuildCloudDocument("座位表"));
        AssertChinese(ex.Message);
        Assert.Contains("共享名单", ex.Message);
    }

    [Fact]
    public void OnCloudDocument_UsesConfirmDialog()
    {
        var source = RepoRoot.Read("src/SeatGrid.App/SeatGridWindow.axaml.cs");
        var start = source.IndexOf("private async void OnCloudDocument", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 OnCloudDocument 处理器。");
        var end = source.IndexOf("private async Task ApplyPickedRosterAsync", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 OnCloudDocument 处理器的结束位置。");
        var method = source[start..end];

        Assert.Contains("catch (Exception ex)", method);
        Assert.Contains("Report(ex, \"准备云端座位表失败\")", method);
        Assert.Contains("ConfirmDialog.ShowAsync", method);
        Assert.Contains("ErrorText.Describe(ex, \"准备云端座位表\")", method);
        Assert.DoesNotContain("ex.Message", method);
        Assert.Contains("\"知道了\"", method);
    }

    [Fact]
    public void ExamSnapshotBlockedMessage_IsChineseAndActionable()
    {
        var message = SeatGridCloudWindow.ExamSnapshotBlockedMessage;
        AssertChinese(message);
        Assert.Contains("考场快照", message);
        Assert.Contains("返回日常座位", message);
    }

    [Fact]
    public void OnCloudDocument_ExamMode_ShowsInformationalDialogBeforeBuild()
    {
        var source = RepoRoot.Read("src/SeatGrid.App/SeatGridWindow.axaml.cs");
        var start = source.IndexOf("private async void OnCloudDocument", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 OnCloudDocument 处理器。");
        var end = source.IndexOf("private SeatGridCloudDocument? TryBuildCloudDocumentOrNull", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 OnCloudDocument 处理器的结束位置。");
        var method = source[start..end];

        var guard = method.IndexOf("if (ViewModel.IsExamMode)", StringComparison.Ordinal);
        var build = method.IndexOf("ViewModel.BuildCloudDocument", StringComparison.Ordinal);
        Assert.True(guard >= 0, "考场态缺少前置拦截。");
        Assert.True(guard < build, "考场拦截必须发生在构建座位表之前。");
        Assert.Contains("SeatGridCloudWindow.ExamSnapshotBlockedMessage", method);
        Assert.DoesNotContain("ViewModel.IsExamMode ||", method);
    }
}
