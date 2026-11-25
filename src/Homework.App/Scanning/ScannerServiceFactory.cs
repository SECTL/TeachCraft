using System.Runtime.Versioning;
using Homework.Core;
using Homework.Core.Scanning;

namespace Homework.App.Scanning;

/// <summary>
/// 按平台选择扫描实现。Windows 用 WIA；其他平台返回一个明确「不支持直接扫描」的实现，
/// 让界面可以照常打开并引导用户使用收件文件夹，而不是在启动时炸掉。
/// </summary>
public static class ScannerServiceFactory
{
    public static IScannerService Create()
    {
        if (!OperatingSystem.IsWindows())
            return new UnsupportedScannerService();

        return CreateWindows();
    }

    [SupportedOSPlatform("windows")]
    private static IScannerService CreateWindows() => new WiaScannerService();
}

public sealed class UnsupportedScannerService : IScannerService
{
    public bool IsSupported => false;

    public Task<IReadOnlyList<ScannerInfo>> GetScannersAsync(CancellationToken token) =>
        Task.FromResult<IReadOnlyList<ScannerInfo>>([]);

    public Task<ScannerCapabilities> GetCapabilitiesAsync(ScannerInfo scanner, CancellationToken token) =>
        Task.FromResult(ScannerCapabilities.Unknown);

    public Task<ScanOutput> AcquireWithDriverUiAsync(CancellationToken token) =>
        Task.FromException<ScanOutput>(
            new HomeworkException("当前平台不支持直接扫描，请改用「收件文件夹」接收扫描件。"));

    public Task<ScanOutput> ScanAsync(
        ScannerInfo scanner,
        ScanSettings settings,
        IProgress<int>? progress,
        CancellationToken token) =>
        Task.FromException<ScanOutput>(
            new HomeworkException("当前平台不支持直接扫描，请改用「收件文件夹」接收扫描件。"));
}
