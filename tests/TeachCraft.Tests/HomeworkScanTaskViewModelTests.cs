using Homework.App.Scanning;
using Homework.Core;
using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkScanTaskViewModelTests
{
    [Fact]
    public async Task RefreshScanners_PopulatesAndSelectsFirst()
    {
        var scanner = new FakeScanner
        {
            Scanners = [new ScannerInfo("1", "Canon iR-ADV C5560i")],
        };
        var viewModel = new ScanTaskViewModel(scanner, new FakeSink(), Preferences());

        await viewModel.RefreshScannersAsync();

        Assert.Single(viewModel.Scanners);
        Assert.Equal("Canon iR-ADV C5560i", viewModel.SelectedScanner?.Name);
        Assert.True(viewModel.CanScan);
        Assert.Contains("找到 1 台扫描仪", viewModel.ScanStatus);
        Assert.Contains("稿台玻璃", viewModel.PageLimitHint);
    }

    [Fact]
    public async Task ScanAsync_ImportsPagesAndAddsReceivedItem()
    {
        var scanner = new FakeScanner
        {
            Scanners = [new ScannerInfo("1", "Canon")],
            OnScan = _ => Task.FromResult(new ScanOutput(["scan-1.png", "scan-2.png"], "work")),
        };
        var sink = new FakeSink
        {
            HasCurrentBatch = true,
            Result = ScanImportOutcome.Ok(2, "批次1"),
        };
        var viewModel = new ScanTaskViewModel(scanner, sink, Preferences());
        await viewModel.RefreshScannersAsync();

        await viewModel.ScanAsync();

        Assert.Single(viewModel.ReceivedFiles);
        Assert.Equal(2, viewModel.ReceivedFiles[0].PageCount);
        Assert.False(viewModel.ReceivedFiles[0].IsError);
        Assert.Equal(1, sink.Calls);
        Assert.False(sink.LastCreateNewBatch);
        Assert.Contains("扫描完成", viewModel.ScanStatus);
        Assert.Contains("已导入 2 页", viewModel.StatusText);
    }

    [Fact]
    public async Task AcquireWithDriverUi_ImportsPagesAndAddsReceivedItem()
    {
        var sink = new FakeSink { Result = ScanImportOutcome.Ok(1, "批次1") };
        var viewModel = new ScanTaskViewModel(new FakeScanner(), sink, Preferences());

        await viewModel.AcquireWithDriverUiAsync();

        Assert.Single(viewModel.ReceivedFiles);
        Assert.Equal("驱动界面扫描件", viewModel.ReceivedFiles[0].FileName);
        Assert.Contains("驱动界面扫描完成", viewModel.ScanStatus);
    }

    [Fact]
    public async Task ScanAsync_ScannerFailure_ShowsErrorAndNoItem()
    {
        var scanner = new FakeScanner
        {
            Scanners = [new ScannerInfo("1", "Canon")],
            OnScan = _ => Task.FromException<ScanOutput>(new HomeworkException("扫描仪离线。")),
        };
        var viewModel = new ScanTaskViewModel(scanner, new FakeSink(), Preferences());
        await viewModel.RefreshScannersAsync();

        await viewModel.ScanAsync();

        Assert.Empty(viewModel.ReceivedFiles);
        Assert.Contains("扫描仪离线", viewModel.ScanStatus);
    }

    [Fact]
    public async Task ImportIncoming_ForcesNewBatchWhenCurrentUnchecked()
    {
        var sink = new FakeSink
        {
            HasCurrentBatch = true,
            Result = ScanImportOutcome.Ok(1, "批次2"),
        };
        var viewModel = new ScanTaskViewModel(new FakeScanner(), sink, Preferences())
        {
            ImportToCurrent = false,
        };

        await viewModel.ImportIncomingAsync("scan-001.pdf");

        Assert.True(sink.LastCreateNewBatch);
        Assert.Single(viewModel.ReceivedFiles);
        Assert.Equal("scan-001.pdf", viewModel.ReceivedFiles[0].FileName);
    }

    [Fact]
    public async Task ImportIncoming_IgnoresUnsupportedFile()
    {
        var sink = new FakeSink();
        var viewModel = new ScanTaskViewModel(new FakeScanner(), sink, Preferences());

        await viewModel.ImportIncomingAsync("scan-001.tif");

        Assert.Empty(viewModel.ReceivedFiles);
        Assert.Equal(0, sink.Calls);
    }

    [Fact]
    public void AddIncomingFailure_AddsErrorRow()
    {
        var viewModel = new ScanTaskViewModel(new FakeScanner(), new FakeSink(), Preferences());

        viewModel.AddIncomingFailure("scan-002.jpg", "文件仍在写入。");

        Assert.Single(viewModel.ReceivedFiles);
        Assert.True(viewModel.ReceivedFiles[0].IsError);
        Assert.Contains("文件仍在写入", viewModel.ReceivedFiles[0].DetailText);
    }

    [Fact]
    public void ToPreferences_RoundTripsSelections()
    {
        var viewModel = new ScanTaskViewModel(new FakeScanner(), new FakeSink(), Preferences());
        viewModel.SelectedSource = viewModel.SourceOptions.Single(option => option.Value == ScanSource.FeederDuplex);
        viewModel.SelectedPageSize = viewModel.PageSizeOptions.Single(option => option.Value == ScanPageSize.A3);
        viewModel.SelectedResolution = viewModel.ResolutionOptions.Single(option => option.Value == 600);
        viewModel.SelectedColorMode = viewModel.ColorModeOptions.Single(option => option.Value == ScanColorMode.Color);
        viewModel.SelectedPageLimit = viewModel.PageLimitOptions.Single(option => option.Value == ScanPageLimit.FirstPage);

        var preferences = viewModel.ToPreferences();

        Assert.Equal(ScanSource.FeederDuplex, preferences.Source);
        Assert.Equal(ScanPageSize.A3, preferences.PageSize);
        Assert.Equal(600, preferences.ResolutionDpi);
        Assert.Equal(ScanColorMode.Color, preferences.ColorMode);
        Assert.Equal(ScanPageLimit.FirstPage, preferences.PageLimit);
    }

    [Fact]
    public void Flatbed_ForcesFirstPage()
    {
        var viewModel = new ScanTaskViewModel(new FakeScanner(), new FakeSink(), Preferences());

        viewModel.SelectedSource = viewModel.SourceOptions.Single(option => option.Value == ScanSource.Flatbed);

        Assert.False(viewModel.CanChoosePageLimit);
        Assert.Equal(ScanPageLimit.FirstPage, viewModel.SelectedPageLimit?.Value);
    }

    [Fact]
    public async Task UnsupportedScanner_ShowsPlatformMessage()
    {
        var viewModel = new ScanTaskViewModel(
            new FakeScanner { IsSupported = false }, new FakeSink(), Preferences());

        Assert.False(viewModel.IsWindows);
        await viewModel.ScanAsync();

        Assert.Contains("当前平台不支持直接扫描", viewModel.ScanStatus);
    }

    [Fact]
    public async Task RefreshScanners_FiltersOptionsToReportedCapabilities()
    {
        var scanner = new FakeScanner
        {
            Scanners = [new ScannerInfo("1", "Canon")],
            Capabilities = new ScannerCapabilities(
                [ScanSource.FeederSingle, ScanSource.FeederDuplex],
                [ScanPageSize.A4],
                [300],
                [ScanColorMode.BlackWhite],
                50,
                true),
        };
        var viewModel = new ScanTaskViewModel(scanner, new FakeSink(), Preferences());

        await viewModel.RefreshScannersAsync();

        Assert.Equal(
            [ScanSource.FeederSingle, ScanSource.FeederDuplex],
            viewModel.SourceOptions.Select(option => option.Value));
        Assert.Single(viewModel.PageSizeOptions);
        Assert.Equal(ScanPageSize.A4, viewModel.PageSizeOptions[0].Value);
        Assert.Single(viewModel.ResolutionOptions);
        Assert.Equal(300, viewModel.ResolutionOptions[0].Value);
        Assert.Single(viewModel.ColorModeOptions);
        Assert.Equal(ScanColorMode.BlackWhite, viewModel.ColorModeOptions[0].Value);
        Assert.Contains("驱动能力", viewModel.CapabilityHint);
        Assert.Contains("最多 50 页", viewModel.CapabilityHint);
        Assert.DoesNotContain("稿台玻璃", viewModel.PageLimitHint);
    }

    [Fact]
    public async Task RefreshScanners_UnknownCapabilities_ShowsAllOptionsForManualSelection()
    {
        var scanner = new FakeScanner
        {
            Scanners = [new ScannerInfo("1", "Canon")],
            Capabilities = ScannerCapabilities.Unknown,
        };
        var viewModel = new ScanTaskViewModel(scanner, new FakeSink(), Preferences());

        await viewModel.RefreshScannersAsync();

        Assert.Equal(3, viewModel.SourceOptions.Count);
        Assert.Contains(ScanSource.Flatbed, viewModel.SourceOptions.Select(option => option.Value));
        Assert.Equal(6, viewModel.PageSizeOptions.Count);
        Assert.Equal(3, viewModel.ResolutionOptions.Count);
        Assert.Equal(3, viewModel.ColorModeOptions.Count);
        Assert.Contains("未报告", viewModel.CapabilityHint);
        Assert.Contains("手动选择", viewModel.CapabilityHint);
    }

    private static ScanTaskPreferences Preferences() => new()
    {
        InboxFolder = Path.Combine(Path.GetTempPath(), "hw-scan-vm"),
        Source = ScanSource.FeederSingle,
        PageSize = ScanPageSize.A4,
        ResolutionDpi = 300,
        ColorMode = ScanColorMode.BlackWhite,
        PageLimit = ScanPageLimit.All,
        ImportToCurrent = true,
    };

    private sealed class FakeScanner : IScannerService
    {
        public bool IsSupported { get; set; } = true;

        public IReadOnlyList<ScannerInfo> Scanners { get; set; } = [];

        public ScannerCapabilities Capabilities { get; set; } = ScannerCapabilities.All;

        public Func<ScanSettings, Task<ScanOutput>>? OnScan { get; set; }

        public Task<IReadOnlyList<ScannerInfo>> GetScannersAsync(CancellationToken token) =>
            Task.FromResult(Scanners);

        public Task<ScannerCapabilities> GetCapabilitiesAsync(ScannerInfo scanner, CancellationToken token) =>
            Task.FromResult(Capabilities);

        public Task<ScanOutput> AcquireWithDriverUiAsync(CancellationToken token) =>
            Task.FromResult(new ScanOutput(["driver-ui.png"], string.Empty));

        public Task<ScanOutput> ScanAsync(
            ScannerInfo scanner,
            ScanSettings settings,
            IProgress<int>? progress,
            CancellationToken token) =>
            OnScan?.Invoke(settings) ?? Task.FromResult(new ScanOutput([], string.Empty));
    }

    private sealed class FakeSink : IScanImportSink
    {
        public bool HasCurrentBatch { get; set; } = true;

        public bool LastCreateNewBatch { get; private set; }

        public int Calls { get; private set; }

        public ScanImportOutcome Result { get; set; } = ScanImportOutcome.Ok(1, "批次1");

        public Task<ScanImportOutcome> ImportAsync(
            IReadOnlyList<string> paths,
            bool createNewBatch,
            CancellationToken token)
        {
            Calls++;
            LastCreateNewBatch = createNewBatch;
            return Task.FromResult(Result);
        }
    }
}
