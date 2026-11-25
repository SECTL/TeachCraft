using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TeachCraft.Core.Tools;

namespace TeachCraft.App.ViewModels;

public sealed record CertificateColumnOption(int Index, string DisplayName)
{
    public static CertificateColumnOption NoAward { get; } = new(-1, "（不印奖项）");
    public override string ToString() => DisplayName;
}

public sealed record CertificateDraftSignature(string? TemplatePath, long TemplateRevision, long TemplateContentRevision,
    string? RosterPath, long RosterRevision, string? Worksheet, decimal HeaderRow,
    int? NameColumn, int? AwardColumn, CertificateTextPlacement Placement);

public partial class CertificateBatchDraft : ObservableObject
{
    public event EventHandler? StateChanged;

    [ObservableProperty] private string? _templatePath;
    [ObservableProperty] private string? _rosterPath;
    [ObservableProperty] private string? _selectedWorksheetName;
    [ObservableProperty] private decimal _headerRow = 1;
    [ObservableProperty] private CertificateColumnOption? _selectedNameColumn;
    [ObservableProperty] private CertificateColumnOption? _selectedAwardColumn;
    [ObservableProperty] private double _nameX = 50;
    [ObservableProperty] private double _nameY = 52;
    [ObservableProperty] private double _awardX = 50;
    [ObservableProperty] private double _awardY = 66;
    [ObservableProperty] private double _nameFontScale = 7.5;
    [ObservableProperty] private double _awardFontScale = 4;
    [ObservableProperty] private string _statusText = "先选择奖状底图和 Excel 名单。";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<string> WorksheetNames { get; } = new();
    public ObservableCollection<CertificateColumnOption> NameColumnChoices { get; } = new();
    public ObservableCollection<CertificateColumnOption> AwardColumnChoices { get; } = new();

    private CertificateWorkbookSnapshot? _workbook;
    private CertificateWorksheetPreview? _worksheet;
    private IReadOnlyList<CertificateRecord> _records = Array.Empty<CertificateRecord>();
    private bool _isRosterLoading;
    private long _rosterLoadGeneration;
    private long _templateInputGeneration;
    private long _rosterRevision;
    private string? _templateContentHash;
    private long _templateContentRevision;
    public long TemplateRevision { get; private set; }

    public bool HasTemplate => TemplatePath is not null;
    public bool HasRoster => RosterPath is not null;
    public bool HasBothInputs => HasTemplate && HasRoster;
    public int HeaderRowMaximum => _workbook?.InspectWorkbook().Worksheets
        .FirstOrDefault(sheet => sheet.Name == SelectedWorksheetName)?.RowCount ?? 30;
    public bool IsRosterLoading => _isRosterLoading;
    public bool CanSelectWorksheet => HasRoster && !IsRosterLoading && !IsBusy;
    public bool CanSelectHeaderRow => CanSelectWorksheet && SelectedWorksheetName is not null;
    public bool CanSelectColumns => CanSelectHeaderRow && _worksheet is not null;
    public bool CanPositionName => HasTemplate && !IsBusy;
    public bool CanPositionAward => CanPositionName && CanPrintAward;
    public string MappingHint => IsRosterLoading ? "正在读取名单，请稍候…"
        : !HasTemplate ? "第 1 步：选择一张奖状底图。"
        : !HasRoster ? "第 2 步：选择 Excel 获奖名单。"
        : SelectedWorksheetName is null ? "第 3 步：选择名单所在的工作表。"
        : _worksheet is null ? "请检查表头行，再选择姓名列和奖项列。"
        : SelectedNameColumn is null ? "第 4 步：选择姓名列。"
        : SelectedAwardColumn is null ? "第 5 步：选择奖项列，或选择「不印奖项」。"
        : _records.Count == 0 ? "当前映射没有有效姓名，请检查表头行与姓名列。"
        : $"已就绪 · {_records.Count} 人 / {_records.Count} 页，按名单原顺序生成。";
    public bool CanGenerate => !IsRosterLoading && HasBothInputs && _worksheet is not null
        && SelectedNameColumn is not null && SelectedAwardColumn is not null && _records.Count > 0;
    public bool CanPrintAward => SelectedAwardColumn is not null && SelectedAwardColumn.Index >= 0;
    public IReadOnlyList<CertificateRecord> Records => _records;
    public CertificateRecord? PreviewRecord => _records.FirstOrDefault()
        ?? (HasTemplate ? new CertificateRecord("示例姓名", SelectedAwardColumn?.Index == -1 ? "" : "示例奖项") : null);

    public CertificateTextPlacement Placement => new CertificateTextPlacement(
        NameX / 100, NameY / 100, AwardX / 100, AwardY / 100, NameFontScale / 100, AwardFontScale / 100).Clamp();

    public CertificateDraftSignature Signature => new(TemplatePath, TemplateRevision, _templateContentRevision, RosterPath,
        _rosterRevision, SelectedWorksheetName, HeaderRow, SelectedNameColumn?.Index,
        SelectedAwardColumn?.Index, SelectedAwardColumn?.Index == -1
            ? Placement with { AwardX = 0, AwardY = 0, AwardFontScale = 0 } : Placement);

    public IReadOnlyList<string> HeaderRows { get; } = Enumerable.Range(1, 30).Select(row => row.ToString()).ToArray();

    public bool AssignTemplate(string path)
    {
        if (CertificateBatchToolbox.ClassifyInput(path) != CertificateInputRole.TemplateImage)
        {
            StatusText = "奖状底图需为 PNG、JPG、WebP 或 BMP 图片。";
            return false;
        }
        TemplateRevision++;
        _templateContentHash = null;
        _templateContentRevision = 0;
        _templateInputGeneration++;
        TemplatePath = path;
        StatusText = HasRoster ? "请选择工作表、表头行和姓名/奖项列。" : "已选底图，请再选择 Excel 名单。";
        NotifyState();
        return true;
    }

    public void ConfirmTemplatePreviewContent(long revision, string contentHash)
    {
        if (revision != TemplateRevision || TemplatePath is null) return;
        var changed = _templateContentHash is not null && _templateContentHash != contentHash;
        _templateContentHash = contentHash;
        if (!changed) return;
        _templateContentRevision++;
        NotifyState();
    }

    public bool AssignTemplateAndRoster(string templatePath, string rosterPath)
    {
        if (CertificateBatchToolbox.ClassifyInput(templatePath) != CertificateInputRole.TemplateImage
            || CertificateBatchToolbox.ClassifyInput(rosterPath) != CertificateInputRole.RosterWorkbook)
        {
            StatusText = "奖状生成需要图片底图和 XLSX / XLS 名单。";
            NotifyState();
            return false;
        }
        if (!AssignRoster(rosterPath)) return false;
        return AssignTemplate(templatePath);
    }

    public bool AssignRoster(string path)
    {
        var generation = ++_rosterLoadGeneration;
        try
        {
            if (CertificateBatchToolbox.ClassifyInput(path) != CertificateInputRole.RosterWorkbook)
                return SetAssignmentError("获奖名单需为 XLSX 或 XLS 工作簿。");

            ApplyRoster(path, CertificateRosterReader.LoadWorkbook(path));
            return true;
        }
        catch (ToolboxException ex) { return SetAssignmentError(ex.Message); }
        finally { FinishRosterLoad(generation); }
    }

    public Task<(bool Applied, string? Error)> AssignRosterAsync(string path)
    {
        if (CertificateBatchToolbox.ClassifyInput(path) != CertificateInputRole.RosterWorkbook)
        {
            var generation = ++_rosterLoadGeneration;
            const string error = "获奖名单需为 XLSX 或 XLS 工作簿。";
            StatusText = error;
            FinishRosterLoad(generation);
            return Task.FromResult<(bool, string?)>((false, error));
        }
        return AssignInputsAsync(null, path);
    }

    private bool SetAssignmentError(string error)
    {
        StatusText = error;
        return false;
    }

    private void ApplyRoster(string path, CertificateWorkbookSnapshot workbook)
    {
        _rosterRevision++;
        _workbook = workbook;
        WorksheetNames.Clear();
        SelectedWorksheetName = null;
        ResetColumns();
        _records = Array.Empty<CertificateRecord>();
        foreach (var worksheet in workbook.InspectWorkbook().Worksheets) WorksheetNames.Add(worksheet.Name);
        RosterPath = path;
        StatusText = "请选择工作表和表头行；姓名列与奖项列不会自动猜测。";
        NotifyState();
    }

    public IReadOnlyList<string> WorksheetNameList => WorksheetNames;
    public IReadOnlyList<CertificateColumnOption> NameColumnList => NameColumnChoices;
    public IReadOnlyList<CertificateColumnOption> AwardColumnList => AwardColumnChoices;
    public int RowCount => _worksheet?.RowCount ?? 0;

    public bool AssignPaths(IReadOnlyList<string> paths, out string? error)
    {
        if (!TryClassifyPaths(paths, out var template, out var roster, out error)) return false;
        if (roster is not null)
        {
            var generation = ++_rosterLoadGeneration;
            try { ApplyInputs(template, roster, CertificateRosterReader.LoadWorkbook(roster)); }
            catch (ToolboxException ex) { error = ex.Message; return false; }
            finally { FinishRosterLoad(generation); }
        }
        else if (template is not null) AssignTemplate(template);
        return true;
    }

    public Task<(bool Applied, string? Error)> AssignPathsAsync(IReadOnlyList<string> paths)
    {
        if (!TryClassifyPaths(paths, out var template, out var roster, out var error))
            return Task.FromResult<(bool, string?)>((false, error));
        return AssignInputsAsync(template, roster);
    }

    private async Task<(bool Applied, string? Error)> AssignInputsAsync(string? template, string? roster)
    {
        if (roster is null)
        {
            if (template is not null) AssignTemplate(template);
            return (true, null);
        }

        var generation = ++_rosterLoadGeneration;
        var templateGeneration = _templateInputGeneration;
        try
        {
            SetRosterLoading(true);
            if (generation != _rosterLoadGeneration) return (false, null);
            StatusText = "正在读取名单工作簿…";
            CertificateWorkbookSnapshot? workbook = null;
            string? error = null;
            try { workbook = await Task.Run(() => CertificateRosterReader.LoadWorkbook(roster)); }
            catch (ToolboxException ex) { error = ex.Message; }

            if (generation != _rosterLoadGeneration
                || (template is not null && templateGeneration != _templateInputGeneration))
                return (false, null);
            if (error is not null)
            {
                StatusText = error;
                return (false, error);
            }

            ApplyInputs(template, roster, workbook);
            return (true, null);
        }
        finally { FinishRosterLoad(generation); }
    }

    private void FinishRosterLoad(long generation)
    {
        if (generation == _rosterLoadGeneration) SetRosterLoading(false);
    }

    private void SetRosterLoading(bool value)
    {
        if (!SetProperty(ref _isRosterLoading, value, nameof(IsRosterLoading))) return;
        NotifyState();
    }

    private void ApplyInputs(string? template, string? roster, CertificateWorkbookSnapshot? workbook)
    {
        if (roster is not null) ApplyRoster(roster, workbook!);
        if (template is not null) AssignTemplate(template);
    }

    private bool TryClassifyPaths(IReadOnlyList<string> paths, out string? template, out string? roster,
        out string? error)
    {
        error = null;
        template = null;
        roster = null;
        if (paths.Count > 2)
        {
            error = "请只选择一张底图和一份名单。";
            return false;
        }

        foreach (var path in paths)
        {
            switch (CertificateBatchToolbox.ClassifyInput(path))
            {
                case CertificateInputRole.TemplateImage when !HasTemplate && template is null:
                    template = path;
                    break;
                case CertificateInputRole.RosterWorkbook when !HasRoster && roster is null:
                    roster = path;
                    break;
                case CertificateInputRole.TemplateImage:
                    error = "已选择奖状底图；请先清除后再更换。";
                    return false;
                case CertificateInputRole.RosterWorkbook:
                    error = "已选择获奖名单；请先清除后再更换。";
                    return false;
                default:
                    error = $"不支持的文件类型：{Path.GetFileName(path)}";
                    return false;
            }
        }
        return true;
    }

    public void ClearTemplate()
    {
        _templateInputGeneration++;
        TemplatePath = null;
        StatusText = HasRoster ? "已选名单，请再选择奖状底图。" : "先选择奖状底图和 Excel 名单。";
        NotifyState();
    }

    public void ClearRoster()
    {
        var generation = ++_rosterLoadGeneration;
        RosterPath = null;
        _workbook = null;
        WorksheetNames.Clear();
        SelectedWorksheetName = null;
        ResetColumns();
        _records = Array.Empty<CertificateRecord>();
        StatusText = HasTemplate ? "已选底图，请再选择 Excel 名单。" : "先选择奖状底图和 Excel 名单。";
        FinishRosterLoad(generation);
        NotifyState();
    }

    public void ReloadColumns()
    {
        ResetColumns();
        _records = Array.Empty<CertificateRecord>();
        if (_workbook is null || SelectedWorksheetName is null || HeaderRow != decimal.Truncate(HeaderRow))
        {
            NotifyState();
            return;
        }
        try
        {
            _worksheet = _workbook.InspectWorksheet(SelectedWorksheetName, decimal.ToInt32(HeaderRow));
            foreach (var column in _worksheet.Columns)
            {
                var header = string.IsNullOrWhiteSpace(column.Header) ? "（空表头）" : column.Header;
                NameColumnChoices.Add(new CertificateColumnOption(column.Index, $"{column.Letter} · {header}"));
                AwardColumnChoices.Add(new CertificateColumnOption(column.Index, $"{column.Letter} · {header}"));
            }
            AwardColumnChoices.Add(CertificateColumnOption.NoAward);
            StatusText = "请明确选择姓名列和奖项列；如不打印奖项，请明确选择“（不印奖项）”。";
        }
        catch (ToolboxException ex) { StatusText = ex.Message; }
        NotifyState();
    }

    public void ReloadRecords()
    {
        _records = Array.Empty<CertificateRecord>();
        if (_workbook is null || SelectedWorksheetName is null || _worksheet is null
            || SelectedNameColumn is null || SelectedAwardColumn is null)
        {
            NotifyState();
            return;
        }
        try
        {
            _records = _workbook.Read(SelectedWorksheetName,
                _worksheet.HeaderRow, SelectedNameColumn.Index, SelectedAwardColumn.Index);
            StatusText = $"已读取 {_records.Count} 条奖状记录；重复姓名会按原表逐行生成。";
        }
        catch (ToolboxException ex) { StatusText = ex.Message; }
        NotifyState();
    }

    public CertificateBatchRequest ToRequest(string output)
    {
        if (!CanGenerate || TemplatePath is null || RosterPath is null || SelectedWorksheetName is null
            || SelectedNameColumn is null || SelectedAwardColumn is null || _worksheet is null)
            throw new ToolboxException(StatusText);
        return new CertificateBatchRequest(TemplatePath, RosterPath, SelectedWorksheetName,
            _worksheet.HeaderRow, SelectedNameColumn.Index, SelectedAwardColumn.Index, Placement, output);
    }

    private void ResetColumns()
    {
        _worksheet = null;
        NameColumnChoices.Clear();
        AwardColumnChoices.Clear();
        SelectedNameColumn = null;
        SelectedAwardColumn = null;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(HasRoster));
        OnPropertyChanged(nameof(HasBothInputs));
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(CanPrintAward));
        OnPropertyChanged(nameof(CanSelectWorksheet));
        OnPropertyChanged(nameof(CanSelectHeaderRow));
        OnPropertyChanged(nameof(CanSelectColumns));
        OnPropertyChanged(nameof(CanPositionName));
        OnPropertyChanged(nameof(CanPositionAward));
        OnPropertyChanged(nameof(MappingHint));
        OnPropertyChanged(nameof(Signature));
        OnPropertyChanged(nameof(Records));
        OnPropertyChanged(nameof(PreviewRecord));
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(WorksheetNameList));
        OnPropertyChanged(nameof(NameColumnList));
        OnPropertyChanged(nameof(AwardColumnList));
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(HeaderRowMaximum));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTemplatePathChanged(string? value) => NotifyState();
    partial void OnRosterPathChanged(string? value) => NotifyState();
    partial void OnSelectedWorksheetNameChanged(string? value) => ReloadColumns();
    partial void OnHeaderRowChanged(decimal value) => ReloadColumns();
    partial void OnSelectedNameColumnChanged(CertificateColumnOption? value) => ReloadRecords();
    partial void OnSelectedAwardColumnChanged(CertificateColumnOption? value) => ReloadRecords();
    partial void OnNameXChanged(double value) => NotifyState();
    partial void OnNameYChanged(double value) => NotifyState();
    partial void OnAwardXChanged(double value) => NotifyState();
    partial void OnAwardYChanged(double value) => NotifyState();
    partial void OnNameFontScaleChanged(double value) => NotifyState();
    partial void OnAwardFontScaleChanged(double value) => NotifyState();
    partial void OnStatusTextChanged(string value) => NotifyState();
    partial void OnIsBusyChanged(bool value) => NotifyState();
}
