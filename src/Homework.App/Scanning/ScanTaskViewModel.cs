using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core;
using Homework.Core.Scanning;

namespace Homework.App.Scanning;

public sealed record ScanSourceOption(ScanSource Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ScanPageSizeOption(ScanPageSize Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ResolutionOption(int Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ColorModeOption(ScanColorMode Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record PageLimitOption(ScanPageLimit Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>收件记录里的一行。成功时显示页数与批次，失败时显示原因。</summary>
public sealed class ScanInboxItem
{
    public ScanInboxItem(
        string fileName,
        DateTime receivedAt,
        long sizeBytes,
        int pageCount,
        string batchTitle,
        string status,
        bool isError)
    {
        FileName = fileName;
        ReceivedAt = receivedAt;
        SizeBytes = sizeBytes;
        PageCount = pageCount;
        BatchTitle = batchTitle;
        Status = status;
        IsError = isError;
    }

    public string FileName { get; }

    public DateTime ReceivedAt { get; }

    public long SizeBytes { get; }

    public int PageCount { get; }

    public string BatchTitle { get; }

    public string Status { get; }

    public bool IsError { get; }

    public string TimeText => ReceivedAt.ToString("HH:mm:ss");

    public string SizeText => SizeBytes switch
    {
        <= 0 => string.Empty,
        >= 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024.0:0.0} MB",
        _ => $"{Math.Max(1, SizeBytes / 1024)} KB",
    };

    public string DetailText => IsError
        ? Status
        : $"{(PageCount > 0 ? $"{PageCount} 页 · " : string.Empty)}{BatchTitle}";
}

/// <summary>
/// 扫描任务窗口的视图模型。直接扫描与收件文件夹两条路共用同一个导入落点，
/// 扫描完成后都进入作业整理批次，不再让老师手动找文件。
/// </summary>
public sealed class ScanTaskViewModel : ObservableObject
{
    private readonly IScannerService _scanner;
    private readonly IScanImportSink _sink;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    private bool _isDirectMode = true;
    private bool _isScanning;
    private bool _isDriverUiScanning;
    private bool _isRefreshingScanners;
    private bool _isWatching;
    private bool _isImporting;
    private bool _importToCurrent;
    private bool _hasCurrentBatch;

    private ScannerInfo? _selectedScanner;
    private ScanSourceOption? _selectedSource;
    private ScanPageSizeOption? _selectedPageSize;
    private ResolutionOption? _selectedResolution;
    private ColorModeOption? _selectedColorMode;
    private PageLimitOption? _selectedPageLimit;

    private string _inboxFolder;
    private string _statusText = "扫描件只写到这台电脑；不会上传。";
    private string _scanStatus;
    private string _inboxStatus = "未开始接收。";
    private ScannerCapabilities? _capabilities;
    private string _capabilityHint = string.Empty;
    private bool _isLoadingCapabilities;
    private CancellationTokenSource? _capabilityCts;

    public ScanTaskViewModel(
        IScannerService scanner,
        IScanImportSink sink,
        ScanTaskPreferences preferences)
    {
        _scanner = scanner;
        _sink = sink;

        SourceOptions =
        [
            new(ScanSource.Flatbed, ScanSettingsText.SourceLabel(ScanSource.Flatbed)),
            new(ScanSource.FeederSingle, ScanSettingsText.SourceLabel(ScanSource.FeederSingle)),
            new(ScanSource.FeederDuplex, ScanSettingsText.SourceLabel(ScanSource.FeederDuplex)),
        ];
        PageSizeOptions =
        [
            new(ScanPageSize.A4, ScanSettingsText.PageSizeLabel(ScanPageSize.A4)),
            new(ScanPageSize.A3, ScanSettingsText.PageSizeLabel(ScanPageSize.A3)),
            new(ScanPageSize.B5, ScanSettingsText.PageSizeLabel(ScanPageSize.B5)),
            new(ScanPageSize.EightK, ScanSettingsText.PageSizeLabel(ScanPageSize.EightK)),
            new(ScanPageSize.SixteenK, ScanSettingsText.PageSizeLabel(ScanPageSize.SixteenK)),
            new(ScanPageSize.Auto, ScanSettingsText.PageSizeLabel(ScanPageSize.Auto)),
        ];
        ResolutionOptions =
        [
            new(200, "200 dpi"),
            new(300, "300 dpi"),
            new(600, "600 dpi"),
        ];
        ColorModeOptions =
        [
            new(ScanColorMode.BlackWhite, ScanSettingsText.ColorModeLabel(ScanColorMode.BlackWhite)),
            new(ScanColorMode.Grayscale, ScanSettingsText.ColorModeLabel(ScanColorMode.Grayscale)),
            new(ScanColorMode.Color, ScanSettingsText.ColorModeLabel(ScanColorMode.Color)),
        ];
        PageLimitOptions =
        [
            new(ScanPageLimit.All, ScanSettingsText.PageLimitLabel(ScanPageLimit.All)),
            new(ScanPageLimit.FirstPage, ScanSettingsText.PageLimitLabel(ScanPageLimit.FirstPage)),
        ];

        var normalized = preferences.Normalized();
        _inboxFolder = normalized.InboxFolder;
        _selectedSource = SourceOptions.FirstOrDefault(option => option.Value == normalized.Source) ?? SourceOptions[1];
        _selectedPageSize = PageSizeOptions.FirstOrDefault(option => option.Value == normalized.PageSize) ?? PageSizeOptions[0];
        _selectedResolution = ResolutionOptions.FirstOrDefault(option => option.Value == normalized.ResolutionDpi) ?? ResolutionOptions[1];
        _selectedColorMode = ColorModeOptions.FirstOrDefault(option => option.Value == normalized.ColorMode) ?? ColorModeOptions[0];
        _selectedPageLimit = PageLimitOptions.FirstOrDefault(option => option.Value == normalized.PageLimit) ?? PageLimitOptions[0];

        _hasCurrentBatch = sink.HasCurrentBatch;
        _importToCurrent = normalized.ImportToCurrent && _hasCurrentBatch;
        _scanStatus = IsWindows
            ? "选择扫描仪，设置参数后点「开始扫描」。"
            : "当前平台不支持直接扫描，请用「收件文件夹」接收扫描件。";
        _capabilityHint = IsWindows ? "选择扫描仪后读取驱动能力。" : string.Empty;
    }

    // ----------------------------- 选项 -----------------------------

    public ObservableCollection<ScannerInfo> Scanners { get; } = [];

    public ObservableCollection<ScanSourceOption> SourceOptions { get; }

    public ObservableCollection<ScanPageSizeOption> PageSizeOptions { get; }

    public ObservableCollection<ResolutionOption> ResolutionOptions { get; }

    public ObservableCollection<ColorModeOption> ColorModeOptions { get; }

    public ObservableCollection<PageLimitOption> PageLimitOptions { get; }

    public ObservableCollection<ScanInboxItem> ReceivedFiles { get; } = [];

    public ScannerInfo? SelectedScanner
    {
        get => _selectedScanner;
        set
        {
            if (SetProperty(ref _selectedScanner, value))
            {
                OnPropertyChanged(nameof(ScannerSummary));
                OnPropertyChanged(nameof(CanScan));
                _ = LoadCapabilitiesAsync(value);
            }
        }
    }

    public ScanSourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            if (value?.Value == ScanSource.Flatbed && SelectedPageLimit?.Value == ScanPageLimit.All)
                SelectedPageLimit = PageLimitOptions[1];
            OnPropertyChanged(nameof(CanChoosePageLimit));
        }
    }

    public ScanPageSizeOption? SelectedPageSize
    {
        get => _selectedPageSize;
        set => SetProperty(ref _selectedPageSize, value);
    }

    public ResolutionOption? SelectedResolution
    {
        get => _selectedResolution;
        set => SetProperty(ref _selectedResolution, value);
    }

    public ColorModeOption? SelectedColorMode
    {
        get => _selectedColorMode;
        set => SetProperty(ref _selectedColorMode, value);
    }

    public PageLimitOption? SelectedPageLimit
    {
        get => _selectedPageLimit;
        set => SetProperty(ref _selectedPageLimit, value);
    }

    // ----------------------------- 模式与状态 -----------------------------

    public bool IsWindows => _scanner.IsSupported;

    public bool IsDirectMode
    {
        get => _isDirectMode;
        set
        {
            if (!SetProperty(ref _isDirectMode, value)) return;
            OnPropertyChanged(nameof(IsInboxMode));
        }
    }

    public bool IsInboxMode => !IsDirectMode;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanRefreshScanners));
            OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    public bool IsDriverUiScanning
    {
        get => _isDriverUiScanning;
        private set
        {
            if (!SetProperty(ref _isDriverUiScanning, value)) return;
            OnPropertyChanged(nameof(CanDriverUiScan));
            OnPropertyChanged(nameof(DriverUiScanButtonText));
        }
    }

    public bool CanDriverUiScan => IsWindows && !IsScanning && !IsDriverUiScanning && !IsLoadingCapabilities;

    public string DriverUiScanButtonText => IsDriverUiScanning ? "扫描中…" : "用驱动界面扫描";

    public bool IsRefreshingScanners
    {
        get => _isRefreshingScanners;
        private set
        {
            if (!SetProperty(ref _isRefreshingScanners, value)) return;
            OnPropertyChanged(nameof(CanRefreshScanners));
        }
    }

    public bool IsLoadingCapabilities
    {
        get => _isLoadingCapabilities;
        private set
        {
            if (!SetProperty(ref _isLoadingCapabilities, value)) return;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanRefreshScanners));
        }
    }

    public ScannerCapabilities? Capabilities
    {
        get => _capabilities;
        private set
        {
            if (!SetProperty(ref _capabilities, value)) return;
            OnPropertyChanged(nameof(PageLimitHint));
        }
    }

    public string CapabilityHint
    {
        get => _capabilityHint;
        private set => SetProperty(ref _capabilityHint, value);
    }

    public string PageLimitHint
    {
        get
        {
            if (Capabilities is null) return "稿台玻璃只出单页；输稿器可选全部页面。";
            var hasFlatbed = Capabilities.Sources.Contains(ScanSource.Flatbed);
            var hasFeeder = Capabilities.Sources.Any(source => source != ScanSource.Flatbed);
            return (hasFlatbed, hasFeeder) switch
            {
                (true, true) => "稿台玻璃只出单页；输稿器可选全部页面。",
                (true, false) => "稿台玻璃只出单页。",
                _ => "输稿器可选全部页面。",
            };
        }
    }

    public bool IsWatching
    {
        get => _isWatching;
        private set
        {
            if (!SetProperty(ref _isWatching, value)) return;
            OnPropertyChanged(nameof(WatchButtonText));
        }
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set => SetProperty(ref _isImporting, value);
    }

    public bool ImportToCurrent
    {
        get => _importToCurrent;
        set => SetProperty(ref _importToCurrent, value);
    }

    public bool HasCurrentBatch
    {
        get => _hasCurrentBatch;
        private set
        {
            if (!SetProperty(ref _hasCurrentBatch, value)) return;
            if (!value) ImportToCurrent = false;
        }
    }

    public bool CanScan => IsWindows && !IsScanning && !IsDriverUiScanning && !IsLoadingCapabilities && SelectedScanner is not null;

    public bool CanRefreshScanners => IsWindows && !IsScanning && !IsDriverUiScanning && !IsRefreshingScanners && !IsLoadingCapabilities;

    public bool CanChoosePageLimit => SelectedSource?.Value != ScanSource.Flatbed;

    public string ScannerSummary => SelectedScanner?.Name ?? "未选择扫描仪";

    public string ScanButtonText => IsScanning ? "正在扫描…" : "开始扫描";

    public string WatchButtonText => IsWatching ? "停止接收" : "开始接收";

    public bool HasReceivedFiles => ReceivedFiles.Count > 0;

    public bool IsEmptyInbox => !HasReceivedFiles;

    public string ReceivedCountText => $"{ReceivedFiles.Count} 个文件";

    public string InboxFolder
    {
        get => _inboxFolder;
        set
        {
            if (!SetProperty(ref _inboxFolder, value)) return;
            OnPropertyChanged(nameof(CanWatch));
        }
    }

    public bool CanWatch => !string.IsNullOrWhiteSpace(InboxFolder);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public string InboxStatus
    {
        get => _inboxStatus;
        private set => SetProperty(ref _inboxStatus, value);
    }

    // ----------------------------- 操作 -----------------------------

    public void SwitchToDirect() => IsDirectMode = true;

    public void SwitchToInbox() => IsDirectMode = false;

    public void RefreshCurrentBatch()
    {
        HasCurrentBatch = _sink.HasCurrentBatch;
        if (HasCurrentBatch && !ImportToCurrent)
            ImportToCurrent = true;
    }

    public async Task RefreshScannersAsync()
    {
        if (IsRefreshingScanners || IsScanning) return;

        IsRefreshingScanners = true;
        ScanStatus = "正在查找扫描仪…";
        try
        {
            var scanners = await _scanner.GetScannersAsync(CancellationToken.None);
            var previousId = SelectedScanner?.Id;
            Scanners.Clear();
            foreach (var scanner in scanners) Scanners.Add(scanner);
            SelectedScanner = Scanners.FirstOrDefault(scanner =>
                                 string.Equals(scanner.Id, previousId, StringComparison.OrdinalIgnoreCase))
                             ?? Scanners.FirstOrDefault();
            ScanStatus = Scanners.Count == 0
                ? "未找到扫描仪。请确认驱动已安装，或改用「收件文件夹」。"
                : $"找到 {Scanners.Count} 台扫描仪。";
            await LoadCapabilitiesAsync(SelectedScanner);
        }
        catch (Exception ex)
        {
            ScanStatus = Describe(ex, "查找扫描仪");
        }
        finally
        {
            IsRefreshingScanners = false;
        }
    }

    public async Task ScanAsync()
    {
        if (!IsWindows)
        {
            ScanStatus = "当前平台不支持直接扫描，请用「收件文件夹」接收扫描件。";
            return;
        }

        if (SelectedScanner is null)
        {
            ScanStatus = "请先选择扫描仪。";
            return;
        }

        if (IsScanning) return;

        if (IsLoadingCapabilities)
        {
            ScanStatus = "正在读取驱动能力，请稍候。";
            return;
        }

        IsScanning = true;
        ScanStatus = "正在扫描…";
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        try
        {
            var settings = BuildSettings();
            var progress = new Progress<int>(pages => ScanStatus = $"已扫描 {pages} 页…");
            var output = await _scanner.ScanAsync(SelectedScanner, settings, progress, cts.Token);
            if (output.PagePaths.Count == 0)
            {
                ScanStatus = "扫描没有返回页面。请检查原稿与扫描仪。";
                return;
            }

            var outcome = await ImportPathsAsync(output.PagePaths, "扫描件", cts.Token);
            if (outcome is null)
            {
                ScanStatus = $"扫描完成，共 {output.PagePaths.Count} 页；导入已取消。";
                return;
            }

            ScanStatus = outcome.Success
                ? $"扫描完成，共 {output.PagePaths.Count} 页；{outcome.Message}"
                : $"扫描完成，但导入失败：{outcome.Message}";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "已取消扫描。";
        }
        catch (Exception ex)
        {
            ScanStatus = Describe(ex, "扫描");
        }
        finally
        {
            _scanCts = null;
            cts.Dispose();
            IsScanning = false;
        }
    }

    public async Task AcquireWithDriverUiAsync()
    {
        if (!IsWindows)
        {
            ScanStatus = "当前平台不支持直接扫描，请改用「收件文件夹」接收扫描件。";
            return;
        }

        if (IsDriverUiScanning || IsScanning || IsLoadingCapabilities) return;

        IsDriverUiScanning = true;
        ScanStatus = "正在打开驱动扫描界面…";
        try
        {
            var output = await _scanner.AcquireWithDriverUiAsync(CancellationToken.None);
            if (output.PagePaths.Count == 0)
            {
                ScanStatus = "已取消驱动界面扫描。";
                return;
            }

            var outcome = await ImportPathsAsync(output.PagePaths, "驱动界面扫描件", CancellationToken.None);
            ScanStatus = outcome is null
                ? $"驱动界面扫描完成，共 {output.PagePaths.Count} 页；导入已取消。"
                : outcome.Success
                    ? $"驱动界面扫描完成，共 {output.PagePaths.Count} 页；{outcome.Message}"
                    : $"驱动界面扫描完成，但导入失败：{outcome.Message}";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "已取消驱动界面扫描。";
        }
        catch (Exception ex)
        {
            ScanStatus = Describe(ex, "驱动界面扫描");
        }
        finally
        {
            IsDriverUiScanning = false;
        }
    }

    public void CancelScan()
    {
        if (_scanCts is null) return;
        _scanCts.Cancel();
        ScanStatus = "正在取消，当前页完成后停止…";
    }

    public void SetWatching(bool watching)
    {
        IsWatching = watching;
        InboxStatus = watching
            ? "正在接收扫描件…"
            : "已停止接收。";
    }

    public void SetInboxFolder(string folder)
    {
        InboxFolder = folder;
        if (!IsWatching) InboxStatus = "未开始接收。";
    }

    public async Task ImportIncomingAsync(string path)
    {
        if (_disposed || !ScanFileClassifier.IsImportable(path)) return;
        var fileName = Path.GetFileName(path);
        await ImportPathsAsync([path], fileName, CancellationToken.None);
    }

    public async Task ImportExistingAsync()
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(InboxFolder) || !Directory.Exists(InboxFolder))
        {
            InboxStatus = "收件文件夹不存在，请先选择文件夹。";
            return;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(InboxFolder)
                .Where(ScanFileClassifier.IsImportable)
                .OrderBy(path => SafeCreationTime(path))
                .ToList();
        }
        catch (Exception ex)
        {
            InboxStatus = Describe(ex, "读取收件文件夹");
            return;
        }

        if (files.Count == 0)
        {
            InboxStatus = "收件文件夹里没有可导入的扫描件。";
            return;
        }

        InboxStatus = $"正在导入 {files.Count} 个已有文件…";
        foreach (var file in files)
            await ImportIncomingAsync(file);

        InboxStatus = $"已处理 {files.Count} 个文件。";
    }

    public void AddIncomingFailure(string path, string reason)
    {
        var fileName = string.IsNullOrWhiteSpace(path) ? "未知文件" : Path.GetFileName(path);
        AddItem(new ScanInboxItem(
            fileName,
            DateTime.Now,
            0,
            0,
            string.Empty,
            reason,
            isError: true));
        InboxStatus = reason;
    }

    public void SetStatus(string message) => StatusText = message;

    public ScanTaskPreferences ToPreferences() => new()
    {
        InboxFolder = InboxFolder,
        Source = SelectedSource?.Value ?? ScanSource.FeederSingle,
        PageSize = SelectedPageSize?.Value ?? ScanPageSize.A4,
        ResolutionDpi = SelectedResolution?.Value ?? 300,
        ColorMode = SelectedColorMode?.Value ?? ScanColorMode.BlackWhite,
        PageLimit = SelectedPageLimit?.Value ?? ScanPageLimit.All,
        ImportToCurrent = ImportToCurrent,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _capabilityCts?.Cancel();
        _capabilityCts?.Dispose();
        _capabilityCts = null;
    }

    // ----------------------------- 内部 -----------------------------

    private async Task LoadCapabilitiesAsync(ScannerInfo? scanner)
    {
        var previous = _capabilityCts;
        previous?.Cancel();
        previous?.Dispose();
        _capabilityCts = null;

        if (scanner is null)
        {
            ApplyCapabilities(ScannerCapabilities.Unknown);
            CapabilityHint = "选择扫描仪后读取驱动能力。";
            IsLoadingCapabilities = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _capabilityCts = cts;
        IsLoadingCapabilities = true;
        CapabilityHint = "正在读取驱动能力…";

        try
        {
            var capabilities = await _scanner.GetCapabilitiesAsync(scanner, cts.Token);
            if (_disposed || cts.IsCancellationRequested || SelectedScanner?.Id != scanner.Id) return;
            ApplyCapabilities(capabilities);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_disposed || cts.IsCancellationRequested) return;
            ApplyCapabilities(ScannerCapabilities.Unknown);
            CapabilityHint = $"读取驱动能力失败，已使用保守默认值：{Describe(ex, "读取驱动能力")}";
        }
        finally
        {
            if (ReferenceEquals(_capabilityCts, cts))
            {
                IsLoadingCapabilities = false;
                _capabilityCts = null;
            }
            cts.Dispose();
        }
    }

    private void ApplyCapabilities(ScannerCapabilities capabilities)
    {
        Capabilities = capabilities;

        var source = SelectedSource?.Value;
        var pageSize = SelectedPageSize?.Value;
        var resolution = SelectedResolution?.Value;
        var colorMode = SelectedColorMode?.Value;

        var effective = capabilities.IsReported ? capabilities : ScannerCapabilities.All;
        var sources = effective.Sources;
        var pageSizes = effective.PageSizes;
        var resolutions = effective.Resolutions;
        var colorModes = effective.ColorModes;

        Rebuild(SourceOptions, sources.Distinct().Select(value => new ScanSourceOption(value, ScanSettingsText.SourceLabel(value))));
        Rebuild(PageSizeOptions, pageSizes.Distinct().Select(value => new ScanPageSizeOption(value, ScanSettingsText.PageSizeLabel(value))));
        Rebuild(ResolutionOptions, resolutions.Distinct().OrderBy(value => value).Select(value => new ResolutionOption(value, $"{value} dpi")));
        Rebuild(ColorModeOptions, colorModes.Distinct().Select(value => new ColorModeOption(value, ScanSettingsText.ColorModeLabel(value))));

        SelectedSource = SourceOptions.FirstOrDefault(option => option.Value == source) ?? SourceOptions.FirstOrDefault();
        SelectedPageSize = PageSizeOptions.FirstOrDefault(option => option.Value == pageSize) ?? PageSizeOptions.FirstOrDefault();
        SelectedResolution = ResolutionOptions.FirstOrDefault(option => option.Value == resolution) ?? ResolutionOptions.FirstOrDefault();
        SelectedColorMode = ColorModeOptions.FirstOrDefault(option => option.Value == colorMode) ?? ColorModeOptions.FirstOrDefault();

        if (!capabilities.IsReported)
        {
            CapabilityHint = "驱动未报告可用参数，已列出全部选项供手动选择；若扫描失败请改用「收件文件夹」。";
            return;
        }

        var feeder = capabilities.Sources.Any(value => value != ScanSource.Flatbed);
        var maxPages = capabilities.MaxPages > 0 && feeder ? $" · 最多 {capabilities.MaxPages} 页" : string.Empty;
        CapabilityHint = $"驱动能力：{capabilities.Summary()}{maxPages}";
    }

    private static void Rebuild<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private ScanSettings BuildSettings() => new()
    {
        Source = SelectedSource?.Value ?? ScanSource.FeederSingle,
        PageSize = SelectedPageSize?.Value ?? ScanPageSize.A4,
        ResolutionDpi = SelectedResolution?.Value ?? 300,
        ColorMode = SelectedColorMode?.Value ?? ScanColorMode.BlackWhite,
        PageLimit = SelectedPageLimit?.Value ?? ScanPageLimit.All,
    };

    private async Task<ScanImportOutcome?> ImportPathsAsync(
        IReadOnlyList<string> paths,
        string label,
        CancellationToken token)
    {
        if (paths.Count == 0) return null;

        await _importGate.WaitAsync(token);
        try
        {
            IsImporting = true;
            var createNewBatch = !ImportToCurrent || !_sink.HasCurrentBatch;
            var outcome = await _sink.ImportAsync(paths, createNewBatch, token);

            var size = SumSize(paths);
            if (outcome.Success)
            {
                AddItem(new ScanInboxItem(
                    label,
                    DateTime.Now,
                    size,
                    outcome.PageCount,
                    outcome.BatchTitle,
                    outcome.Message,
                    isError: false));
                StatusText = outcome.Message;
            }
            else
            {
                AddItem(new ScanInboxItem(
                    label,
                    DateTime.Now,
                    size,
                    0,
                    string.Empty,
                    outcome.Message,
                    isError: true));
                StatusText = outcome.Message;
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            var message = Describe(ex, "导入扫描件");
            AddItem(new ScanInboxItem(
                label,
                DateTime.Now,
                SumSize(paths),
                0,
                string.Empty,
                message,
                isError: true));
            StatusText = message;
            return ScanImportOutcome.Failed(message);
        }
        finally
        {
            IsImporting = false;
            _importGate.Release();
        }
    }

    private void AddItem(ScanInboxItem item)
    {
        ReceivedFiles.Insert(0, item);
        while (ReceivedFiles.Count > 50)
            ReceivedFiles.RemoveAt(ReceivedFiles.Count - 1);

        OnPropertyChanged(nameof(HasReceivedFiles));
        OnPropertyChanged(nameof(IsEmptyInbox));
        OnPropertyChanged(nameof(ReceivedCountText));
    }

    private static long SumSize(IReadOnlyList<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch
            {
                // 文件可能在导入后被移走；大小只是展示信息。
            }
        }
        return total;
    }

    private static DateTime SafeCreationTime(string path)
    {
        try
        {
            return File.GetCreationTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string Describe(Exception ex, string action) =>
        ex is HomeworkException ? ex.Message : $"处理失败：{ex.Message}";
}
