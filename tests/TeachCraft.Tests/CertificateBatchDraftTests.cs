using ClosedXML.Excel;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class CertificateBatchDraftTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));

    public CertificateBatchDraftTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Draft_RequiresExplicitSheetAndNameAndAwardChoice()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();
        Assert.False(draft.CanGenerate);

        draft.AssignTemplate(template);
        draft.AssignRoster(roster);
        Assert.Null(draft.SelectedWorksheetName);
        Assert.False(draft.CanGenerate);

        draft.SelectedWorksheetName = "Awards";
        Assert.Null(draft.SelectedNameColumn);
        Assert.Null(draft.SelectedAwardColumn);
        Assert.False(draft.CanGenerate);
    }

    [Fact]
    public void Draft_MapsChosenColumnsAndRetainsDuplicateNames()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();
        draft.AssignTemplateAndRoster(template, roster);
        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[2];

        var request = draft.ToRequest(Path.Combine(_root, "out.pdf"));

        Assert.True(draft.CanGenerate);
        Assert.Equal(2, draft.Records.Count);
        Assert.Equal(1, request.NameColumn);
        Assert.Equal(2, request.AwardColumn);
    }

    [Fact]
    public void Draft_AllowsExplicitNoAwardChoice()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();
        draft.AssignTemplateAndRoster(template, roster);
        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[^1];

        Assert.True(draft.CanGenerate);
        Assert.All(draft.Records, record => Assert.Equal(string.Empty, record.Award));
    }

    [Fact]
    public void Draft_ClassifiesTemplateAndRosterByRoleRatherThanOrder()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();

        Assert.True(draft.AssignPaths(new[] { roster, template }, out var error));
        Assert.Null(error);
        Assert.Equal(template, draft.TemplatePath);
        Assert.Equal(roster, draft.RosterPath);
    }

    [Fact]
    public async Task Draft_MapsLoadedSnapshotAfterWorkbookIsDeleted()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();
        var (applied, error) = await draft.AssignPathsAsync(new[] { roster, template });
        Assert.True(applied, error);
        Assert.Equal(new[] { "Cover", "Awards" }, draft.WorksheetNames.ToArray());
        Assert.Null(draft.SelectedWorksheetName);

        File.Delete(roster);
        draft.SelectedWorksheetName = "Cover";
        Assert.Single(draft.NameColumnChoices);
        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        Assert.Null(draft.SelectedNameColumn);
        Assert.Null(draft.SelectedAwardColumn);
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[2];

        Assert.True(draft.CanGenerate);
        Assert.Equal(new[] { "合成甲", "合成甲" }, draft.Records.Select(record => record.Name));
        Assert.Equal(new[] { "优秀", "进步" }, draft.Records.Select(record => record.Award));
        draft.SelectedAwardColumn = CertificateColumnOption.NoAward;
        Assert.All(draft.Records, record => Assert.Empty(record.Award));
    }

    [Fact]
    public async Task Draft_RejectsUnreadableDropWithoutAssigningEitherInput()
    {
        var (template, _) = Inputs();
        var draft = new CertificateBatchDraft();

        var (applied, error) = await draft.AssignPathsAsync(new[] { template, Path.Combine(_root, "missing.xlsx") });

        Assert.False(applied);
        Assert.NotNull(error);
        Assert.Equal(error, draft.StatusText);
        Assert.False(draft.HasTemplate);
        Assert.False(draft.HasRoster);
        Assert.Empty(draft.WorksheetNames);
    }

    [Fact]
    public async Task Draft_BlocksExportDuringRosterReplacementAndRestoresMappingOnFailure()
    {
        var (template, roster) = Inputs();
        var tool = new ToolItemViewModel(ToolKind.CertificateBatch, "14", "certificate-batch",
            "奖状生成", "", "", "", "", "");
        var draft = tool.Certificate!;
        Assert.True(draft.AssignTemplateAndRoster(template, roster));
        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[2];
        Assert.True(draft.CanGenerate);
        Assert.True(tool.CanRun);

        var output = Path.Combine(_root, "out.pdf");
        var originalRequest = draft.ToRequest(output);
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);
        var originalRecords = draft.Records;
        var sawLoadingStatus = false;
        var canRunChanges = new List<bool>();
        tool.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tool.CanRun)) canRunChanges.Add(tool.CanRun);
        };
        draft.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(draft.StatusText) || draft.StatusText != "正在读取名单工作簿…") return;
            sawLoadingStatus = true;
            Assert.True(draft.IsRosterLoading);
            Assert.False(draft.CanGenerate);
            Assert.False(draft.CanSelectWorksheet);
            Assert.False(draft.CanSelectHeaderRow);
            Assert.False(draft.CanSelectColumns);
            Assert.Contains("读取", draft.MappingHint);
            Assert.False(tool.CanRun);
            Assert.Throws<ToolboxException>(() => draft.ToRequest(output));
        };

        var (applied, error) = await draft.AssignRosterAsync(Path.Combine(_root, "missing.xlsx"));

        Assert.True(sawLoadingStatus);
        Assert.False(applied);
        Assert.NotNull(error);
        Assert.False(draft.IsRosterLoading);
        Assert.True(draft.CanGenerate);
        Assert.True(tool.CanRun);
        Assert.False(canRunChanges[0]);
        Assert.True(canRunChanges[^1]);
        Assert.All(canRunChanges.SkipLast(1), canRun => Assert.False(canRun));
        Assert.Equal(originalRequest, draft.ToRequest(output));
        Assert.Same(originalRecords, draft.Records);
        Assert.False(tool.IsCertificateOutputStale);
        Assert.Equal(output, tool.LastOutputPath);

        var replacement = Path.Combine(_root, "replacement.xlsx");
        File.Copy(roster, replacement);
        (applied, error) = await draft.AssignRosterAsync(replacement);

        Assert.True(applied, error);
        Assert.False(draft.IsRosterLoading);
        Assert.Equal(replacement, draft.RosterPath);
        Assert.Null(draft.SelectedWorksheetName);
        Assert.Null(draft.SelectedNameColumn);
        Assert.Null(draft.SelectedAwardColumn);
        Assert.Empty(draft.Records);
        Assert.False(draft.CanGenerate);
    }

    [Fact]
    public async Task Draft_ClearRosterDiscardsPendingDropAndItsTemplate()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();
        var initialStatus = draft.StatusText;
        var cleared = false;
        draft.PropertyChanged += (_, args) =>
        {
            if (cleared || args.PropertyName != nameof(draft.StatusText)) return;
            cleared = true;
            draft.ClearRoster();
        };

        var (applied, error) = await draft.AssignPathsAsync(new[] { roster, template });

        Assert.True(cleared);
        Assert.False(applied);
        Assert.Null(error);
        Assert.Equal(initialStatus, draft.StatusText);
        Assert.False(draft.HasRoster);
        Assert.False(draft.HasTemplate);
        Assert.False(draft.IsRosterLoading);
        Assert.Empty(draft.WorksheetNames);
    }

    [Fact]
    public void Mapping_GatesFollowWorksheetAndHeaderDependencies()
    {
        var (template, roster) = Inputs();
        var draft = new CertificateBatchDraft();
        Assert.False(draft.CanSelectWorksheet);
        Assert.False(draft.CanSelectHeaderRow);
        Assert.False(draft.CanSelectColumns);
        Assert.Contains("底图", draft.MappingHint);

        draft.AssignTemplate(template);
        Assert.Contains("名单", draft.MappingHint);
        draft.AssignRoster(roster);
        Assert.True(draft.CanSelectWorksheet);
        Assert.False(draft.CanSelectHeaderRow);
        Assert.False(draft.CanSelectColumns);
        Assert.Contains("工作表", draft.MappingHint);

        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        Assert.True(draft.CanSelectHeaderRow);
        Assert.True(draft.CanSelectColumns);
        Assert.Contains("姓名列", draft.MappingHint);
        draft.SelectedNameColumn = draft.NameColumnList[1];
        Assert.Contains("奖项列", draft.MappingHint);
        draft.SelectedAwardColumn = CertificateColumnOption.NoAward;
        Assert.Contains("2 人 / 2 页", draft.MappingHint);

        draft.SelectedWorksheetName = "Cover";
        Assert.Null(draft.SelectedNameColumn);
        Assert.Null(draft.SelectedAwardColumn);
        Assert.False(draft.CanGenerate);
        draft.ClearRoster();
        Assert.False(draft.CanSelectWorksheet);
        Assert.False(draft.CanSelectColumns);
    }

    [Fact]
    public void Placement_NoAwardDisablesAwardButKeepsNameAndValues()
    {
        var tool = MappedTool();
        var draft = tool.Certificate!;
        Assert.True(draft.CanPositionName);
        Assert.True(draft.CanPositionAward);
        draft.AwardX = 42;
        draft.SelectedAwardColumn = CertificateColumnOption.NoAward;
        Assert.True(draft.CanPositionName);
        Assert.False(draft.CanPositionAward);
        Assert.False(draft.CanPrintAward);
        Assert.Equal(42, draft.AwardX);
        Assert.All(draft.Records, record => Assert.Empty(record.Award));

        draft.SelectedAwardColumn = draft.AwardColumnList[2];
        Assert.True(draft.CanPositionAward);
        Assert.Equal(42, draft.AwardX);
        tool.IsBusy = true;
        Assert.False(draft.CanPositionName);
        Assert.False(draft.CanPositionAward);
        Assert.False(draft.CanSelectWorksheet);
        Assert.False(draft.CanSelectColumns);
        tool.IsBusy = false;
        Assert.True(draft.CanPositionAward);
    }

    [Fact]
    public void Preview_FailureAndSuccessfulRetryDoNotChangeMappingStatus()
    {
        var tool = MappedTool();
        var workflowStatus = tool.Certificate!.StatusText;
        tool.BeginCertificatePreview();
        Assert.True(tool.IsCertificatePreviewBusy);
        Assert.False(tool.ShowCertificatePreviewEmpty);
        tool.FailCertificatePreview("无法读取底图");
        Assert.True(tool.HasCertificatePreviewError);
        Assert.False(tool.IsCertificatePreviewBusy);
        Assert.False(tool.ShowCertificatePreviewEmpty);
        Assert.Contains("无法读取底图", tool.CertificatePreviewStatus);

        tool.BeginCertificatePreview();
        Assert.False(tool.HasCertificatePreviewError);
        tool.FinishCertificatePreview();
        Assert.False(tool.IsCertificatePreviewBusy);
        Assert.Null(tool.CertificatePreviewError);
        Assert.Equal(workflowStatus, tool.Certificate.StatusText);
        Assert.True(tool.CanRun);
    }

    [Fact]
    public void Output_StatusAndPreviewChangesKeepCurrentResultAndPath()
    {
        var tool = MappedTool();
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);
        tool.Certificate!.StatusText = "提示已更新";
        tool.IsBusy = true;
        tool.IsBusy = false;
        tool.BeginCertificatePreview();
        tool.FailCertificatePreview("synthetic failure");
        tool.FinishCertificatePreview();

        Assert.Equal(output, tool.LastOutputPath);
        Assert.True(tool.HasOutput);
        Assert.False(tool.IsCertificateOutputStale);
        Assert.Contains("一致", tool.CertificateOutputFeedback);
    }

    [Fact]
    public void Output_PlacementChangesMarkPreviousResultAndRevertingRestoresCurrent()
    {
        var tool = MappedTool();
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.LastOutputPath = output;
        var draft = tool.Certificate!;
        var originalX = draft.NameX;
        draft.NameX += 1;
        Assert.True(tool.IsCertificateOutputStale);
        Assert.Equal(output, tool.LastOutputPath);
        Assert.Contains("上次", tool.CertificateOutputFeedback);
        draft.NameX = originalX;
        Assert.False(tool.IsCertificateOutputStale);

        draft.SelectedAwardColumn = CertificateColumnOption.NoAward;
        Assert.True(tool.IsCertificateOutputStale);
        draft.SelectedAwardColumn = draft.AwardColumnList[2];
        Assert.False(tool.IsCertificateOutputStale);
        draft.ClearTemplate();
        Assert.True(tool.IsCertificateOutputStale);
        Assert.Equal(output, tool.LastOutputPath);
    }

    [Fact]
    public void Output_NoAwardIgnoresUnusedAwardPlacement()
    {
        var tool = MappedTool();
        tool.Certificate!.SelectedAwardColumn = CertificateColumnOption.NoAward;
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);
        tool.Certificate.AwardX += 1;
        tool.Certificate.AwardY += 1;
        tool.Certificate.AwardFontScale += 1;
        Assert.False(tool.IsCertificateOutputStale);
        tool.Certificate.NameFontScale += 1;
        Assert.True(tool.IsCertificateOutputStale);
    }

    [Fact]
    public void Output_MappingAndHeaderChangesRemainStaleUntilSameMappingIsRestored()
    {
        var tool = MappedTool();
        var draft = tool.Certificate!;
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);
        draft.SelectedNameColumn = draft.NameColumnList[0];
        Assert.True(tool.IsCertificateOutputStale);
        draft.SelectedNameColumn = draft.NameColumnList[1];
        Assert.False(tool.IsCertificateOutputStale);
        draft.HeaderRow = 2;
        Assert.True(tool.IsCertificateOutputStale);
        draft.HeaderRow = 3;
        Assert.True(tool.IsCertificateOutputStale);
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[2];
        Assert.False(tool.IsCertificateOutputStale);
        Assert.Equal(output, tool.LastOutputPath);
    }

    [Fact]
    public void Output_UsesRunSnapshotAndCanRecordAnotherSuccessAtSamePath()
    {
        var tool = MappedTool();
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.Certificate!.NameY += 1;
        tool.RecordCertificateOutput(output);
        Assert.True(tool.IsCertificateOutputStale);
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);
        Assert.False(tool.IsCertificateOutputStale);
    }

    [Fact]
    public void Output_ReloadingSameRosterPathMarksChangedInputSnapshot()
    {
        var tool = MappedTool();
        var draft = tool.Certificate!;
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);
        var roster = draft.RosterPath!;
        using (var workbook = new XLWorkbook(roster))
        {
            workbook.Worksheet("Awards").Cell(4, 3).Value = "合成新奖项";
            workbook.Save();
        }
        draft.AssignRoster(roster);
        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[2];
        Assert.True(tool.IsCertificateOutputStale);
        Assert.Equal(output, tool.LastOutputPath);
        Assert.Equal("合成新奖项", draft.Records[0].Award);
    }

    [Fact]
    public void Output_PreviewContentCheckIgnoresSameBytesAndRejectsSupersededResult()
    {
        var tool = MappedTool();
        var draft = tool.Certificate!;
        var revision = draft.TemplateRevision;
        draft.ConfirmTemplatePreviewContent(revision, "synthetic-original-content");
        var output = Path.Combine(_root, "last.pdf");
        tool.ToRequest(output);
        tool.RecordCertificateOutput(output);

        draft.ConfirmTemplatePreviewContent(revision, "synthetic-original-content");
        Assert.False(tool.IsCertificateOutputStale);
        draft.ConfirmTemplatePreviewContent(revision - 1, "superseded-content");
        Assert.False(tool.IsCertificateOutputStale);
        draft.ConfirmTemplatePreviewContent(revision, "synthetic-replaced-content");
        Assert.True(tool.IsCertificateOutputStale);
        Assert.Equal(output, tool.LastOutputPath);
    }

    private ToolItemViewModel MappedTool()
    {
        var (template, roster) = Inputs();
        var tool = new ToolItemViewModel(ToolKind.CertificateBatch, "14", "certificate-batch",
            "奖状生成", "", "", "", "", "");
        var draft = tool.Certificate!;
        Assert.True(draft.AssignTemplateAndRoster(template, roster));
        draft.SelectedWorksheetName = "Awards";
        draft.HeaderRow = 3;
        draft.SelectedNameColumn = draft.NameColumnList[1];
        draft.SelectedAwardColumn = draft.AwardColumnList[2];
        return tool;
    }

    private (string Template, string Roster) Inputs()
    {
        var template = Path.Combine(_root, "certificate.png");
        using (var bitmap = new SkiaSharp.SKBitmap(400, 300))
        using (var stream = File.Create(template)) bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
        var roster = Path.Combine(_root, "roster.xlsx");
        using var workbook = new XLWorkbook();
        var cover = workbook.AddWorksheet("Cover");
        cover.Cell(1, 1).Value = "Note";
        var sheet = workbook.AddWorksheet("Awards");
        sheet.Cell(3, 1).Value = "ID";
        sheet.Cell(3, 2).Value = "姓名";
        sheet.Cell(3, 3).Value = "奖项";
        sheet.Cell(4, 1).Value = "001";
        sheet.Cell(4, 2).Value = "合成甲";
        sheet.Cell(4, 3).Value = "优秀";
        sheet.Cell(5, 1).Value = "002";
        sheet.Cell(5, 2).Value = "合成甲";
        sheet.Cell(5, 3).Value = "进步";
        workbook.SaveAs(roster);
        return (template, roster);
    }
}
