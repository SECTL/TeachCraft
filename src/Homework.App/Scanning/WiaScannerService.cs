using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;
using Homework.Core;
using Homework.Core.Scanning;

namespace Homework.App.Scanning;

/// <summary>
/// Windows 直接扫描。走 WIA 自动化（WIA.DeviceManager），不依赖 TWAIN 的窗口句柄，
/// 因此可以把扫描设置做成软件自己的界面。WIA 调用必须在 STA 线程上跑，这里每次
/// 扫描开一个后台 STA 线程，不阻塞 Avalonia 的界面线程。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WiaScannerService : IScannerService
{
    private const string PngFormat = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const string JpegFormat = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    private const string BmpFormat = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private const string AnyFormat = "{00000000-0000-0000-0000-000000000000}";
    private const int MaxPages = 100;

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<IReadOnlyList<ScannerInfo>> GetScannersAsync(CancellationToken token)
    {
        if (!IsSupported)
            return Task.FromResult<IReadOnlyList<ScannerInfo>>([]);

        return RunStaAsync(() => EnumerateScanners(token), token);
    }

    public Task<ScanOutput> ScanAsync(
        ScannerInfo scanner,
        ScanSettings settings,
        IProgress<int>? progress,
        CancellationToken token)
    {
        if (!IsSupported)
            throw new HomeworkException("当前平台不支持直接扫描，请改用「收件文件夹」接收扫描件。");
        if (scanner is null)
            throw new HomeworkException("请先选择扫描仪。");

        var normalized = settings.NormalizedForDriver();
        return RunStaAsync(() => ScanCore(scanner, normalized, progress, token), token);
    }

    public Task<ScanOutput> AcquireWithDriverUiAsync(CancellationToken token)
    {
        if (!IsSupported)
            throw new HomeworkException("当前平台不支持直接扫描，请改用「收件文件夹」接收扫描件。");

        return RunStaAsync(() => AcquireWithDriverUiCore(token), token);
    }

    private sealed record DriverUiAttempt(string Name, string Extension, Func<dynamic, dynamic?> Invoke);

    private static ScanOutput AcquireWithDriverUiCore(CancellationToken token)
    {
        dynamic? dialog = null;

        try
        {
            var dialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
            if (dialogType is null)
                throw new HomeworkException("未找到 Windows 图像采集对话框，无法使用驱动界面扫描。");

            dialog = Activator.CreateInstance(dialogType);
            if (dialog is null)
                throw new HomeworkException("无法创建 Windows 图像采集对话框。");

            var attempts = new List<DriverUiAttempt>
            {
                new("7 参数 / 扫描仪 / BMP", ".bmp", d => d.ShowAcquireImage(1, 0, 131072, BmpFormat, false, true, false)),
                new("7 参数 / 扫描仪 / 默认格式", ".png", d => d.ShowAcquireImage(1, 0, 131072, AnyFormat, false, true, false)),
                new("7 参数 / 扫描仪 / JPEG", ".jpg", d => d.ShowAcquireImage(1, 0, 131072, JpegFormat, false, true, false)),
                new("7 参数 / 未指定设备 / BMP", ".bmp", d => d.ShowAcquireImage(0, 0, 131072, BmpFormat, false, true, false)),
                new("4 参数 / BMP", ".bmp", d => d.ShowAcquireImage(1, 0, 131072, BmpFormat)),
                new("6 参数 / WIA 1.x", ".bmp", d => d.ShowAcquireImage(1, 0, true, "", BmpFormat, "")),
            };

            var errors = new List<string>();
            foreach (var attempt in attempts)
            {
                token.ThrowIfCancellationRequested();
                dynamic? image = null;
                try
                {
                    image = attempt.Invoke(dialog);
                    if (image is null) return new ScanOutput([], string.Empty);

                    var directory = CreateWorkingDirectory();
                    var path = Path.Combine(directory, $"driver-ui-001{attempt.Extension}");
                    image.SaveFile(path);
                    return new ScanOutput([path], directory);
                }
                catch (COMException ex)
                {
                    var friendly = WiaErrorText.DescribeHResult((uint)ex.ErrorCode);
                    errors.Add($"{attempt.Name}: 0x{(uint)ex.ErrorCode:X8} {friendly ?? ex.Message}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{attempt.Name}: {ex.GetType().Name} {ex.Message}");
                }
                finally
                {
                    Release(image);
                }
            }

            throw new HomeworkException(
                $"驱动界面扫描失败。参数尝试记录：{string.Join(" | ", errors)}");
        }
        catch (HomeworkException)
        {
            throw;
        }
        catch (COMException ex)
        {
            throw new HomeworkException($"驱动界面扫描失败（COM 0x{(uint)ex.ErrorCode:X8}）：{ex.Message}", ex);
        }
        finally
        {
            Release(dialog);
        }
    }

    public Task<ScannerCapabilities> GetCapabilitiesAsync(ScannerInfo scanner, CancellationToken token)
    {
        if (!IsSupported)
            return Task.FromResult(ScannerCapabilities.Unknown);

        return RunStaAsync(() => ReadCapabilities(scanner, token), token);
    }

    // ----------------------------- WIA 枚举 -----------------------------

    private static IReadOnlyList<ScannerInfo> EnumerateScanners(CancellationToken token)
    {
        var scanners = new List<ScannerInfo>();
        dynamic? manager = null;
        try
        {
            var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (managerType is null) return scanners;

            manager = Activator.CreateInstance(managerType);
            if (manager is null) return scanners;

            dynamic infos = manager.DeviceInfos;
            try
            {
                var count = (int)infos.Count;
                for (var index = 1; index <= count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    dynamic info = infos.Item(index);
                    try
                    {
                        if ((int)info.Type != 1) continue; // 1 = 扫描仪
                        var id = ReadProperty(info.Properties, "DeviceID") ?? $"wia-{index}";
                        var name = ReadProperty(info.Properties, "Name") ?? id;
                        scanners.Add(new ScannerInfo(id, name));
                    }
                    finally
                    {
                        Release(info);
                    }
                }
            }
            finally
            {
                Release(infos);
            }
        }
        catch (COMException ex)
        {
            throw new HomeworkException(
                "无法读取扫描仪列表。请确认 Windows 图像采集服务已启动，并已安装扫描仪驱动。", ex);
        }
        finally
        {
            Release(manager);
        }

        return scanners;
    }

    private static ScannerCapabilities ReadCapabilities(ScannerInfo scanner, CancellationToken token)
    {
        var stage = "start";
        dynamic? manager = null;
        dynamic? infos = null;
        dynamic? info = null;
        dynamic? device = null;
        dynamic? items = null;
        dynamic? item = null;
        dynamic? itemProperties = null;
        dynamic? deviceProperties = null;

        try
        {
            var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (managerType is null)
                return ScannerCapabilities.Unknown with { RawDump = "WIA.DeviceManager ProgID 未注册。" };

            manager = Activator.CreateInstance(managerType);
            if (manager is null)
                return ScannerCapabilities.Unknown with { RawDump = "Activator.CreateInstance(WIA.DeviceManager) 返回 null。" };

            stage = "enumerate";
            infos = manager.DeviceInfos;
            {
                var count = (int)infos.Count;
                for (var index = 1; index <= count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    dynamic candidate = infos.Item(index);
                    var keep = false;
                    try
                    {
                        if ((int)candidate.Type != 1) continue;
                        var id = ReadProperty(candidate.Properties, "DeviceID");
                        var name = ReadProperty(candidate.Properties, "Name");
                        if (string.Equals(id, scanner.Id, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, scanner.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            info = candidate;
                            keep = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (!keep) Release(candidate);
                    }
                }
            }
            if (info is null)
                return ScannerCapabilities.Unknown with { RawDump = $"未在 WIA 设备列表中找到 {scanner.Id} / {scanner.Name}。" };

            stage = "connect";
            device = info.Connect();
            items = device.Items;
            item = items.Item(1);
            if (item is null)
                return ScannerCapabilities.Unknown with { RawDump = "设备没有 Items[1]，无法读取扫描属性。" };

            stage = "read-item-properties";
            var snapshot = new Dictionary<int, WiaPropertyInfo>();
            itemProperties = item.Properties;
            ReadPropertySnapshot(itemProperties, WiaScanRequest.DocumentHandlingProperty, snapshot);
            ReadPropertySnapshot(itemProperties, WiaScanRequest.CurIntentProperty, snapshot);
            ReadPropertySnapshot(itemProperties, WiaScanRequest.XResolutionProperty, snapshot);
            ReadPropertySnapshot(itemProperties, WiaScanRequest.YResolutionProperty, snapshot);
            ReadPropertySnapshot(itemProperties, WiaScanRequest.PagesProperty, snapshot);
            ReadPropertySnapshot(itemProperties, WiaScanRequest.PageSizeProperty, snapshot);

            stage = "read-device-properties";
            deviceProperties = device.Properties;
            ReadPropertySnapshot(deviceProperties, WiaScanRequest.DocumentHandlingCapabilitiesProperty, snapshot);

            var capabilities = WiaCapabilityReader.Read(new WiaPropertySnapshot(snapshot));

            var dump = new StringBuilder();
            dump.AppendLine($"scanner: {scanner.Name} ({scanner.Id})");
            dump.AppendLine("parsed: " + capabilities.Summary());
            dump.AppendLine($"reported: {capabilities.IsReported}; maxPages: {capabilities.MaxPages}");
            dump.AppendLine();
            dump.AppendLine($"[items count] {(int)items.Count}");
            dump.AppendLine("[item 1 properties]");
            AppendProperties(dump, itemProperties);
            dump.AppendLine();
            dump.AppendLine("[device properties]");
            AppendProperties(dump, deviceProperties);
            var statusProperty = TryGetProperty(deviceProperties, WiaErrorText.DocumentHandlingStatusProperty);
            if (statusProperty is not null)
            {
                var status = TryInt(statusProperty.Value);
                dump.AppendLine($"document handling status = 0x{status:X}");
                foreach (var message in WiaErrorText.DescribeStatus(status))
                    dump.AppendLine("  " + message);
                Release(statusProperty);
            }
            dump.AppendLine();
            dump.AppendLine("[device info properties]");
            dynamic infoProperties = info.Properties;
            try
            {
                AppendProperties(dump, infoProperties);
            }
            finally
            {
                Release(infoProperties);
            }

            return capabilities with { RawDump = dump.ToString() };
        }
        catch (COMException ex)
        {
            return ScannerCapabilities.Unknown with { RawDump = $"stage={stage}; COMException: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return ScannerCapabilities.Unknown with { RawDump = $"stage={stage}; {ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            Release(itemProperties);
            Release(deviceProperties);
            Release(item);
            Release(items);
            Release(device);
            Release(info);
            Release(infos);
            Release(manager);
        }
    }

    // ----------------------------- 扫描主流程 -----------------------------

    private static ScanOutput ScanCore(
        ScannerInfo scanner,
        ScanSettings settings,
        IProgress<int>? progress,
        CancellationToken token)
    {
        var request = WiaScanRequest.Build(settings);
        var workingDirectory = CreateWorkingDirectory();
        var pages = new List<string>();

        dynamic? manager = null;
        dynamic? info = null;
        dynamic? device = null;
        dynamic? items = null;
        dynamic? item = null;

        try
        {
            var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (managerType is null)
                throw new HomeworkException("未找到 Windows 图像采集服务，无法直接扫描。");

            manager = Activator.CreateInstance(managerType);
            if (manager is null)
                throw new HomeworkException("无法启动 Windows 图像采集服务。");

            dynamic infos = manager.DeviceInfos;
            try
            {
                var count = (int)infos.Count;
                for (var index = 1; index <= count; index++)
                {
                    dynamic candidate = infos.Item(index);
                    var keep = false;
                    try
                    {
                        if ((int)candidate.Type != 1) continue;
                        var id = ReadProperty(candidate.Properties, "DeviceID");
                        var name = ReadProperty(candidate.Properties, "Name");
                        if (string.Equals(id, scanner.Id, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, scanner.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            info = candidate;
                            keep = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (!keep) Release(candidate);
                    }
                }
            }
            finally
            {
                Release(infos);
            }

            if (info is null)
                throw new HomeworkException("没有找到选中的扫描仪，请刷新后重试。");

            device = info.Connect();
            items = device.Items;
            item = items.Item(1);

            ApplyRequest(item, device, request);

            var maxPages = settings.ScanAllPages ? MaxPages : 1;
            for (var pageNumber = 1; pageNumber <= maxPages; pageNumber++)
            {
                token.ThrowIfCancellationRequested();
                CheckDeviceStatus(device);

                try
                {
                    var transfer = TransferPage(item, workingDirectory, pageNumber);
                    dynamic image = transfer.Image;
                    try
                    {
                        image.SaveFile(transfer.Path);
                    }
                    finally
                    {
                        Release(image);
                    }

                    pages.Add(transfer.Path);
                    progress?.Report(pageNumber);
                    CheckDeviceStatus(device);
                }
                catch (COMException ex)
                {
                    var message = WiaErrorText.DescribeHResult((uint)ex.ErrorCode);
                    if (message is not null)
                        throw new HomeworkException(message, ex);
                    if (pages.Count > 0)
                        break; // 输稿器没有下一页了
                    throw;
                }
                catch (Exception) when (pages.Count > 0)
                {
                    break;
                }

                if (!settings.ScanAllPages) break;
            }

            if (pages.Count == 0)
                throw new HomeworkException("扫描没有返回任何页面。请检查原稿是否放好、扫描仪是否在线。");

            return new ScanOutput(pages, workingDirectory);
        }
        catch (HomeworkException)
        {
            throw;
        }
        catch (COMException ex)
        {
            throw new HomeworkException("扫描失败。请确认扫描仪已开机、驱动已安装，并检查原稿。", ex);
        }
        finally
        {
            Release(item);
            Release(items);
            Release(device);
            Release(info);
            Release(manager);
        }
    }

    private sealed record TransferredPage(object Image, string Path);

    private static TransferredPage TransferPage(
        dynamic item,
        string workingDirectory,
        int pageNumber)
    {
        var stem = Path.Combine(workingDirectory, $"page-{pageNumber:D3}");
        try
        {
            return new TransferredPage(item.Transfer(PngFormat), stem + ".png");
        }
        catch (COMException)
        {
            return new TransferredPage(item.Transfer(JpegFormat), stem + ".jpg");
        }
    }

    private static void ApplyRequest(dynamic item, dynamic device, WiaScanRequest request)
    {
        dynamic properties = item.Properties;
        try
        {
            TrySet(properties, WiaScanRequest.CurIntentProperty, request.Intent);
            TrySet(properties, WiaScanRequest.XResolutionProperty, request.XResolution);
            TrySet(properties, WiaScanRequest.YResolutionProperty, request.YResolution);

            if (!TrySet(properties, WiaScanRequest.DocumentHandlingProperty, request.DocumentHandling))
            {
                dynamic deviceProperties = device.Properties;
                try
                {
                    TrySet(deviceProperties, WiaScanRequest.DocumentHandlingProperty, request.DocumentHandling);
                }
                finally
                {
                    Release(deviceProperties);
                }
            }

            TrySet(properties, WiaScanRequest.PagesProperty, request.Pages);
            TrySet(properties, WiaScanRequest.PageSizeProperty, request.PageSize);
            TrySet(properties, WiaScanRequest.XExtentProperty, request.WidthPixels);
            TrySet(properties, WiaScanRequest.YExtentProperty, request.HeightPixels);
        }
        finally
        {
            Release(properties);
        }
    }

    // ----------------------------- COM 辅助 -----------------------------

    private static void CheckDeviceStatus(dynamic device)
    {
        dynamic? properties = null;
        try
        {
            properties = device.Properties;
            var property = TryGetProperty(properties, WiaErrorText.DocumentHandlingStatusProperty);
            if (property is null) return;

            var status = TryInt(property.Value);
            if (!WiaErrorText.IsBlocking(status)) return;

            var messages = WiaErrorText.DescribeStatus(status);
            throw new HomeworkException(
                messages.Count > 0 ? string.Join(" ", messages) : "扫描仪需要人工处理，请查看设备面板。");
        }
        catch (HomeworkException)
        {
            throw;
        }
        catch
        {
        }
        finally
        {
            Release(properties);
        }
    }

    private static string? ReadProperty(dynamic properties, string name)
    {
        dynamic? property = null;
        try
        {
            property = properties.Item(name);
            return Convert.ToString(property.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(property);
        }
    }

    private static void AppendProperties(StringBuilder dump, dynamic properties)
    {
        int count;
        try
        {
            count = (int)properties.Count;
        }
        catch
        {
            return;
        }

        for (var index = 1; index <= count; index++)
        {
            dynamic? property = null;
            try
            {
                property = properties.Item(index);
                var name = Convert.ToString(property.Name, CultureInfo.InvariantCulture) ?? "?";
                var value = Convert.ToString(property.Value, CultureInfo.InvariantCulture) ?? "?";
                var subType = TryInt(property.SubType);
                var min = TryInt(property.SubTypeMin);
                var max = TryInt(property.SubTypeMax);
                var step = TryInt(property.SubTypeStep);
                var values = ReadIntValues(property.SubTypeValues);
                dump.AppendLine(
                    $"{name}: value={value}; subType={subType}; min={min}; max={max}; " +
                    $"step={step}; values=[{string.Join(",", values)}]");
            }
            catch
            {
            }
        }
    }

    private static void ReadPropertySnapshot(
        dynamic properties,
        int id,
        IDictionary<int, WiaPropertyInfo> snapshot)
    {
        dynamic? property = TryGetProperty(properties, id);
        if (property is null) return;

        try
        {
            var kind = (int)property.SubType;
            var min = TryInt(property.SubTypeMin);
            var max = TryInt(property.SubTypeMax);
            var step = TryInt(property.SubTypeStep);
            var values = ReadIntValues(property.SubTypeValues);
            var single = TryInt(property.Value);
            snapshot[id] = new WiaPropertyInfo(
                (WiaPropertyKind)kind, min, max, step, values,
                values.Count == 0 && kind == (int)WiaPropertyKind.Unspecified ? single : null);
        }
        catch
        {
        }
    }

    private static dynamic? TryGetProperty(dynamic properties, int id)
    {
        var name = WiaPropertyNames.For(id);
        object?[] candidates = name is null
            ? [id, id.ToString(CultureInfo.InvariantCulture)]
            : [id, id.ToString(CultureInfo.InvariantCulture), name];

        foreach (var candidate in candidates)
        {
            dynamic? property = null;
            try
            {
                property = properties.Item(candidate);
                if (property is not null) return property;
            }
            catch
            {
            }
        }

        return null;
    }

    private static IReadOnlyList<int> ReadIntValues(dynamic? values)
    {
        var result = new List<int>();
        if (values is null) return result;

        int count;
        try
        {
            count = (int)values.Count;
        }
        catch
        {
            return result;
        }

        for (var index = 1; index <= count; index++)
        {
            try
            {
                result.Add(Convert.ToInt32(values.Item(index), CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        if (result.Count == 0)
        {
            for (var index = 0; index < count; index++)
            {
                try
                {
                    result.Add(Convert.ToInt32(values[index], CultureInfo.InvariantCulture));
                }
                catch
                {
                }
            }
        }

        return result.Distinct().ToList();
    }

    private static int TryInt(dynamic? value)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TrySet(dynamic properties, int id, object value)
    {
        if (TrySetCore(properties, id, value)) return true;
        return TrySetCore(properties, id.ToString(CultureInfo.InvariantCulture), value);
    }

    private static bool TrySetCore(dynamic properties, object index, object value)
    {
        dynamic? property = null;
        try
        {
            property = properties.Item(index);
            property.Value = value;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(property);
        }
    }

    private static void Release(object? value)
    {
        if (value is null) return;
        try
        {
            if (Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch
        {
            // 释放 COM 失败不影响扫描结果。
        }
    }

    private static string CreateWorkingDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Homework", "Scans");
        if (string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            root = Path.Combine(Path.GetTempPath(), "TeachCraft", "Homework", "Scans");

        var directory = Path.Combine(
            root,
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TeachCraft.WiaScan",
        };

        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        return completion.Task;
    }
}
