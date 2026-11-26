using Homework.Core.Scanning;

namespace Homework.App.Scanning;

/// <summary>一台可用的扫描仪。Id 来自 WIA DeviceID，Name 是驱动显示名。</summary>
public sealed record ScannerInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>一次扫描的输出：每页一个本机文件，外加保存这些文件的目录。</summary>
public sealed record ScanOutput(IReadOnlyList<string> PagePaths, string WorkingDirectory);

/// <summary>
/// 直接扫描的抽象。Windows 走 WIA；其他平台返回不支持，由收件文件夹兜底。
/// </summary>
public interface IScannerService
{
    bool IsSupported { get; }

    Task<IReadOnlyList<ScannerInfo>> GetScannersAsync(CancellationToken token);

    Task<ScannerCapabilities> GetCapabilitiesAsync(ScannerInfo scanner, CancellationToken token);

    Task<ScanOutput> ScanAsync(
        ScannerInfo scanner,
        ScanSettings settings,
        IProgress<int>? progress,
        CancellationToken token);

    /// <summary>调用 WIA 公共对话框，由驱动自己的界面设置扫描参数并采集。</summary>
    Task<ScanOutput> AcquireWithDriverUiAsync(CancellationToken token);
}

/// <summary>扫描件导入辑册的落点。由 HomeworkWindow 实现，扫描窗口不直接碰批次。</summary>
public interface IScanImportSink
{
    bool HasCurrentBatch { get; }

    Task<ScanImportOutcome> ImportAsync(
        IReadOnlyList<string> paths,
        bool createNewBatch,
        CancellationToken token);
}

public sealed record ScanImportOutcome(
    bool Success,
    int PageCount,
    string BatchTitle,
    string Message)
{
    public static ScanImportOutcome Failed(string message) => new(false, 0, string.Empty, message);

    public static ScanImportOutcome Ok(int pageCount, string batchTitle) =>
        new(true, pageCount, batchTitle, $"已导入 {pageCount} 页到「{batchTitle}」。");
}
