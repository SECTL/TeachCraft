using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Homework.Core;
using Homework.Core.Roster;
using TeachCraft.Shared;

namespace Homework.App;

/// <summary>
/// 名单导入弹窗：Excel 与手动粘贴两个页签。所有解析都走 Core 的纯函数，
/// 窗口只负责选文件、收集列映射、展示预览与把结果交回调用方。
/// </summary>
public sealed partial class RosterImportWindow : Window, INotifyPropertyChanged
{
    private string? _filePath;
    private RosterWorkbookPreview? _workbook;
    private RosterImportResult? _excelResult;
    private RosterPasteResult? _pasteResult;
    private bool _isExcelTab = true;
    private bool _suppressEvents;
    private bool _canConfirm;
    private decimal? _headerRow = 1m;

    public RosterImportWindow()
    {
        InitializeComponent();
        Opened += (_, _) => WindowFit.Clamp(this);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Excel 页确认后的结果；只有本窗口 Close(true) 时才有值。</summary>
    public RosterImportResult? ImportedResult { get; private set; }

    /// <summary>粘贴页确认后的结果；只有本窗口 Close(true) 时才有值。</summary>
    public RosterPasteResult? PasteResult { get; private set; }

    public bool CanConfirm
    {
        get => _canConfirm;
        private set
        {
            if (_canConfirm == value) return;
            _canConfirm = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConfirm)));
        }
    }

    /// <summary>1 基表头行号，供 NumericUpDown 双向绑定。</summary>
    public decimal? HeaderRow
    {
        get => _headerRow;
        set
        {
            if (_headerRow == value) return;
            _headerRow = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderRow)));
            if (!_suppressEvents) RefreshSheet();
        }
    }

    private void OnExcelTab(object? sender, RoutedEventArgs e) => SwitchTab(true);

    private void OnPasteTab(object? sender, RoutedEventArgs e) => SwitchTab(false);

    private void SwitchTab(bool excel)
    {
        _isExcelTab = excel;
        ExcelPanel.IsVisible = excel;
        PastePanel.IsVisible = !excel;
        if (excel)
        {
            if (!ExcelTabButton.Classes.Contains("active")) ExcelTabButton.Classes.Add("active");
            PasteTabButton.Classes.Remove("active");
        }
        else
        {
            if (!PasteTabButton.Classes.Contains("active")) PasteTabButton.Classes.Add("active");
            ExcelTabButton.Classes.Remove("active");
        }
        UpdateConfirmState();
    }

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择学生名单",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("名单文件") { Patterns = ["*.xlsx", "*.xls", "*.csv"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        LoadFile(path);
    }

    /// <summary>走查用：跳过文件选择框，直接加载指定名单并回读下拉框显示文本。</summary>
    public void LoadForCapture(string path) => LoadFile(path);

    public string DescribeSelectionsForCapture() =>
        $"sheet={SheetBox.SelectedItem}; name={NameColumnBox.SelectedItem}; " +
        $"number={NumberColumnBox.SelectedItem}; class={ClassColumnBox.SelectedItem}";

    private void LoadFile(string path)
    {
        try
        {
            var preview = HomeworkRosterImporter.LoadWorkbook(path);
            _filePath = path;
            _workbook = preview;
            FilePathText.Text = path;
            ExcelError.IsVisible = false;

            _suppressEvents = true;
            try
            {
                SheetBox.ItemsSource = preview.Worksheets;
                SheetBox.SelectedIndex = 0;
                var sheet = preview.Worksheets.FirstOrDefault();
                if (sheet is not null)
                {
                    HeaderUpDown.Maximum = Math.Max(1, sheet.RowCount);
                    HeaderRow = 1m;
                }
            }
            finally
            {
                _suppressEvents = false;
            }

            RefreshSheet();
        }
        catch (HomeworkException ex)
        {
            ResetExcelState();
            ShowExcelError(ErrorText.Describe(ex, "读取名单"));
        }
        catch (Exception ex)
        {
            ResetExcelState();
            ShowExcelError(ErrorText.Describe(ex, "读取名单"));
        }
    }

    private void OnSheetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (SheetBox.SelectedItem is RosterWorksheetInfo sheet)
            HeaderUpDown.Maximum = Math.Max(1, sheet.RowCount);
        RefreshSheet();
    }

    private void OnClassColumnToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ClassColumnBox.IsEnabled = HasClassColumnBox.IsChecked == true;
        RefreshSheet();
    }

    private void OnColumnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        RefreshExcelPreview();
    }

    private void OnDefaultClassChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        RefreshExcelPreview();
    }

    private void RefreshSheet()
    {
        if (_filePath is null || _workbook is null || SheetBox.SelectedItem is not RosterWorksheetInfo sheet)
            return;

        var header = (int)(HeaderRow ?? 1m);
        if (header < 1) header = 1;
        if (header > sheet.RowCount) header = sheet.RowCount;

        try
        {
            var inspected = HomeworkRosterImporter.InspectWorksheet(_filePath, sheet.Name, header);
            if (inspected.Columns.Count == 0)
            {
                ResetExcelState();
                ShowExcelError("这张工作表没有可识别的列，请换一张表。");
                return;
            }

            var guess = HomeworkRosterImporter.GuessColumns(
                _filePath, sheet.Name, header, HasClassColumnBox.IsChecked == true);
            var numberItems = WithNone(inspected.Columns);
            var classItems = WithNone(inspected.Columns);

            _suppressEvents = true;
            try
            {
                NameColumnBox.ItemsSource = inspected.Columns;
                NumberColumnBox.ItemsSource = numberItems;
                ClassColumnBox.ItemsSource = classItems;

                NameColumnBox.SelectedItem = inspected.Columns.FirstOrDefault(c => c.Index == guess.NameColumn)
                                             ?? inspected.Columns.FirstOrDefault();
                NumberColumnBox.SelectedItem = numberItems.FirstOrDefault(c => c.Index == guess.NumberColumn)
                                               ?? numberItems.First();
                ClassColumnBox.SelectedItem = classItems.FirstOrDefault(c => c.Index == guess.ClassColumn)
                                              ?? classItems.First();
            }
            finally
            {
                _suppressEvents = false;
            }

            ClassColumnBox.IsEnabled = HasClassColumnBox.IsChecked == true;
            ExcelError.IsVisible = false;
            RefreshExcelPreview();
        }
        catch (HomeworkException ex)
        {
            ResetExcelState();
            ShowExcelError(ErrorText.Describe(ex, "读取工作表"));
        }
        catch (Exception ex)
        {
            ResetExcelState();
            ShowExcelError(ErrorText.Describe(ex, "读取工作表"));
        }
    }

    private void RefreshExcelPreview()
    {
        _excelResult = null;
        ExcelPreviewLines.ItemsSource = null;
        ExcelWarnings.ItemsSource = null;
        ExcelSummary.Text = "选择文件后可预览识别结果";
        UpdateConfirmState();

        if (_filePath is null || SheetBox.SelectedItem is not RosterWorksheetInfo sheet) return;

        var map = CurrentMap();
        if (map.NameColumn < 0)
        {
            ShowExcelError("请选择姓名列。");
            return;
        }

        try
        {
            var result = HomeworkRosterImporter.Read(
                _filePath, sheet.Name, map, ExcelDefaultClassBox.Text ?? string.Empty);
            _excelResult = result;

            ExcelSummary.Text = $"已识别 {result.Students.Count} 名学生 · {result.Classes.Count} 个班级"
                                + (result.SkippedRows > 0 ? $"（跳过 {result.SkippedRows} 行）" : string.Empty);

            var classNames = result.Classes.ToDictionary(c => c.Id, c => c.Name);
            ExcelPreviewLines.ItemsSource = result.Students
                .Take(5)
                .Select(s => $"{classNames.GetValueOrDefault(s.ClassId, string.Empty)}  {s.Number}  {s.Name}")
                .ToArray();
            ExcelWarnings.ItemsSource = result.Warnings;
            ExcelError.IsVisible = false;
        }
        catch (HomeworkException ex)
        {
            ShowExcelError(ErrorText.Describe(ex, "读取名单"));
        }
        catch (Exception ex)
        {
            ShowExcelError(ErrorText.Describe(ex, "读取名单"));
        }

        UpdateConfirmState();
    }

    private RosterColumnMap CurrentMap()
    {
        var header = (int)(HeaderRow ?? 1m);
        var name = (NameColumnBox.SelectedItem as RosterColumnChoice)?.Index ?? -1;
        var number = (NumberColumnBox.SelectedItem as RosterColumnChoice)?.Index ?? -1;
        var classColumn = HasClassColumnBox.IsChecked == true
            ? (ClassColumnBox.SelectedItem as RosterColumnChoice)?.Index ?? -1
            : -1;
        return new RosterColumnMap(header, name, number, classColumn);
    }

    private void OnParsePaste(object? sender, RoutedEventArgs e)
    {
        _pasteResult = null;
        PasteErrors.ItemsSource = null;
        PasteSummary.Text = "粘贴后点「解析」查看结果";
        PasteError.IsVisible = false;

        try
        {
            var result = RosterPasteParser.Parse(
                PasteBox.Text ?? string.Empty, PasteDefaultClassBox.Text ?? string.Empty);
            _pasteResult = result;
            PasteSummary.Text = $"已识别 {result.Students.Count} 名学生";
            PasteErrors.ItemsSource = result.Errors;
        }
        catch (HomeworkException ex)
        {
            PasteError.Text = ErrorText.Describe(ex, "解析名单");
            PasteError.IsVisible = true;
        }
        catch (Exception ex)
        {
            PasteError.Text = ErrorText.Describe(ex, "解析名单");
            PasteError.IsVisible = true;
        }

        UpdateConfirmState();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_isExcelTab)
        {
            if (_excelResult is null || _excelResult.Students.Count == 0) return;
            ImportedResult = _excelResult;
        }
        else
        {
            if (_pasteResult is null || _pasteResult.Students.Count == 0) return;
            PasteResult = _pasteResult;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void UpdateConfirmState()
    {
        CanConfirm = _isExcelTab
            ? (_excelResult?.Students.Count ?? 0) > 0
            : (_pasteResult?.Students.Count ?? 0) > 0;
    }

    private void ResetExcelState()
    {
        _excelResult = null;
        ExcelPreviewLines.ItemsSource = null;
        ExcelWarnings.ItemsSource = null;
        ExcelSummary.Text = "选择文件后可预览识别结果";
        UpdateConfirmState();
    }

    private void ShowExcelError(string message)
    {
        ExcelError.Text = message;
        ExcelError.IsVisible = true;
    }

    private static List<RosterColumnChoice> WithNone(IReadOnlyList<RosterColumnChoice> columns)
    {
        var items = new List<RosterColumnChoice> { new(-1, "—", "（不使用）") };
        items.AddRange(columns);
        return items;
    }
}
