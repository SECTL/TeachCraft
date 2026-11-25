using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Linq;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Tools;
using SkiaSharp;

namespace TeachCraft.App.Views;

public partial class CertificateBatchPanelView : UserControl
{
    public event EventHandler? TemplatePickRequested;
    public event EventHandler? RosterPickRequested;
    public event EventHandler? RunRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? OpenOutputRequested;

    private ToolItemViewModel? _tool;
    private CancellationTokenSource? _previewCts;
    private (string Path, long Revision, CertificateRecord Record, CertificateTextPlacement Placement)? _lastRequest;
    private CertificatePageMetrics? _metrics;
    private bool _attached;
    private Control? _dragHandle;
    private IPointer? _dragPointer;
    private Point _dragOffset;
    private readonly SemaphoreSlim _previewRenderGate = new(1, 1);

    public CertificateBatchPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookTool();
        DetachedFromVisualTree += (_, _) => { _attached = false; CancelPreview(); };
        AttachedToVisualTree += (_, _) => { _attached = true; RequestPreview(); };
        PreviewSurface.SizeChanged += (_, _) => UpdateHandles();
        PropertyChanged += (_, args) =>
        {
            if (args.Property != IsVisibleProperty) return;
            if (IsVisible) RequestPreview();
            else CancelPreview();
        };
    }

    private void HookTool()
    {
        CancelPreview();
        if (_tool?.Certificate is { } oldDraft) oldDraft.StateChanged -= OnDraftChanged;
        _tool = DataContext as ToolItemViewModel;
        if (_tool?.Certificate is { } draft) draft.StateChanged += OnDraftChanged;
        RequestPreview();
    }

    private void OnDraftChanged(object? sender, EventArgs e)
    {
        UpdateHandles();
        RequestPreview();
    }

    private void RequestPreview(bool force = false)
    {
        if (!_attached || !IsVisible || _tool?.Certificate is not { } draft || draft.TemplatePath is null)
        {
            CancelPreview();
            if (_tool is { } emptyTool) emptyTool.CertificatePreviewImage = null;
            return;
        }

        var record = draft.PreviewRecord;
        if (record is null) { CancelPreview(); return; }
        var placement = draft.Placement;
        var request = (draft.TemplatePath, draft.TemplateRevision, record, placement);
        if (!force && _lastRequest == request) return;
        var templateChanged = _lastRequest?.Path != draft.TemplatePath || _lastRequest?.Revision != draft.TemplateRevision;
        _lastRequest = request;
        if (templateChanged)
        {
            _tool.CertificatePreviewImage = null;
            _tool.CertificatePreviewCaption = string.Empty;
            _metrics = null;
            UpdateHandles();
        }

        var tokenSource = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCts, tokenSource);
        previous?.Cancel();
        _tool.BeginCertificatePreview();
        _ = RenderPreviewAsync(_tool, draft.TemplatePath, draft.TemplateRevision, record, placement, tokenSource);
    }

    private async Task RenderPreviewAsync(ToolItemViewModel tool, string templatePath, long templateRevision, CertificateRecord record,
        CertificateTextPlacement placement, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(180, token).ConfigureAwait(true);
            if (!ReferenceEquals(_previewCts, cancellation)) return;
            await _previewRenderGate.WaitAsync(token);
            (CertificatePreviewPage Preview, CertificatePageMetrics Metrics, string ContentHash) result;
            try
            {
                result = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    using var templateStream = File.OpenRead(templatePath);
                    var contentHash = Convert.ToHexString(SHA256.HashData(templateStream));
                    templateStream.Position = 0;
                    using var codec = SKCodec.Create(templateStream) ?? throw new ToolboxException("无法读取奖状底图。");
                    var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                        or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                    var size = rotated ? new SKSize(codec.Info.Height, codec.Info.Width) : new SKSize(codec.Info.Width, codec.Info.Height);
                    token.ThrowIfCancellationRequested();
                    return (CertificateRenderer.RenderPreview(templatePath, record, placement),
                        CertificatePageGeometry.Measure(size), contentHash);
                }, token).ConfigureAwait(true);
            }
            finally { _previewRenderGate.Release(); }
            if (!IsCurrent(tool, cancellation)) return;
            using var stream = new MemoryStream(result.Preview.Png);
            tool.CertificatePreviewImage = new Bitmap(stream);
            tool.CertificatePreviewCaption = record.Name;
            tool.Certificate?.ConfirmTemplatePreviewContent(templateRevision, result.ContentHash);
            tool.FinishCertificatePreview();
            _metrics = result.Metrics;
            UpdateHandles();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrent(tool, cancellation)) return;
            tool.FailCertificatePreview(ex is ToolboxException ? ex.Message
                : TeachCraft.Shared.ErrorText.Describe(ex, "读取底图"));
            UpdateHandles();
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cancellation)) Interlocked.CompareExchange(ref _previewCts, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelPreview()
    {
        var cts = Interlocked.Exchange(ref _previewCts, null);
        cts?.Cancel();
        _lastRequest = null;
        _metrics = null;
        EndDrag();
        if (_tool is { } tool) tool.ClearCertificatePreview();
        UpdateHandles();
    }

    private bool IsCurrent(ToolItemViewModel tool, CancellationTokenSource cancellation) =>
        _attached && IsVisible && !cancellation.IsCancellationRequested
        && ReferenceEquals(_previewCts, cancellation) && ReferenceEquals(_tool, tool);

    private Rect TemplateBounds()
    {
        if (_metrics is not { } metrics || _tool?.CertificatePreviewImage is not { } image) return default;
        var scale = Math.Min(PreviewSurface.Bounds.Width / image.PixelSize.Width,
            PreviewSurface.Bounds.Height / image.PixelSize.Height);
        var left = (PreviewSurface.Bounds.Width - image.PixelSize.Width * scale) / 2;
        var top = (PreviewSurface.Bounds.Height - image.PixelSize.Height * scale) / 2;
        return new Rect(left + metrics.TemplateRect.Left / metrics.PageWidth * image.PixelSize.Width * scale,
            top + metrics.TemplateRect.Top / metrics.PageHeight * image.PixelSize.Height * scale,
            metrics.TemplateRect.Width / metrics.PageWidth * image.PixelSize.Width * scale,
            metrics.TemplateRect.Height / metrics.PageHeight * image.PixelSize.Height * scale);
    }

    private void UpdateHandles()
    {
        if (NameHandle is null || AwardHandle is null) return;
        var rect = TemplateBounds();
        var draft = _tool?.Certificate;
        NameHandle.IsVisible = rect.Width > 0 && draft is not null;
        AwardHandle.IsVisible = NameHandle.IsVisible && draft?.CanPrintAward == true
            && !string.IsNullOrWhiteSpace(draft.PreviewRecord?.Award);
        if (draft is null) return;
        var record = draft.PreviewRecord;
        FitHandle(NameHandle, record?.Name ?? string.Empty, draft.NameFontScale / 100);
        FitHandle(AwardHandle, record?.Award ?? string.Empty, draft.AwardFontScale / 100);
        PositionHandle(NameHandle, rect, draft.Placement.NameX, draft.Placement.NameY);
        PositionHandle(AwardHandle, rect, draft.Placement.AwardX, draft.Placement.AwardY);
    }

    /// <summary>把定位框缩到刚好包住预览里渲染的文字（A4 点 → 预览位图 → 屏幕像素），中心仍对准锚点。</summary>
    private void FitHandle(Control handle, string text, double fontScale)
    {
        if (_metrics is null || _tool?.CertificatePreviewImage is not { } image
            || string.IsNullOrWhiteSpace(text))
        {
            handle.Width = 20;
            handle.Height = 16;
            return;
        }
        var surface = PreviewSurface.Bounds;
        if (surface.Width <= 0 || surface.Height <= 0) return;
        var imageScale = Math.Min(surface.Width / image.PixelSize.Width, surface.Height / image.PixelSize.Height);
        var pointToScreen = image.PixelSize.Width / _metrics.PageWidth * imageScale;
        var area = _metrics.TemplateRect;
        var fontSize = area.Height * (float)fontScale;
        var rawWidth = Math.Max(1, text.Length) * fontSize;
        var maxWidth = area.Width * 0.88f;
        if (rawWidth > maxWidth) fontSize *= maxWidth / rawWidth;
        handle.Width = Math.Max(18, Math.Min(rawWidth, maxWidth) * pointToScreen + 8);
        handle.Height = Math.Max(14, fontSize * pointToScreen + 6);
    }

    private static void PositionHandle(Control handle, Rect rect, double x, double y)
    {
        Canvas.SetLeft(handle, rect.Left + rect.Width * x - handle.Width / 2);
        Canvas.SetTop(handle, rect.Top + rect.Height * y - handle.Height / 2);
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(handle);
        _dragOffset = new Point(point.X - handle.Width / 2, point.Y - handle.Height / 2);
        _dragHandle = handle;
        _dragPointer = e.Pointer;
        handle.Focus();
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHandleFocused(object? sender, RoutedEventArgs e)
    {
        NameHandle.ZIndex = ReferenceEquals(sender, NameHandle) ? 1 : 0;
        AwardHandle.ZIndex = ReferenceEquals(sender, AwardHandle) ? 1 : 0;
    }

    private void OnFocusName(object? sender, RoutedEventArgs e)
    {
        ShowPositionHandles.IsChecked = true;
        NameHandle.Focus();
    }

    private void OnFocusAward(object? sender, RoutedEventArgs e)
    {
        ShowPositionHandles.IsChecked = true;
        AwardHandle.Focus();
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragHandle is null || _tool?.Certificate is not { } draft) return;
        var rect = TemplateBounds();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var point = e.GetPosition(PreviewSurface);
        SetPosition(draft, _dragHandle, (point.X - _dragOffset.X - rect.Left) / rect.Width * 100,
            (point.Y - _dragOffset.Y - rect.Top) / rect.Height * 100);
        e.Handled = true;
    }

    private static void SetPosition(CertificateBatchDraft draft, Control handle, double x, double y)
    {
        x = Math.Round(Math.Clamp(x, 0, 100), 1);
        y = Math.Round(Math.Clamp(y, 0, 100), 1);
        if (handle.Name == "NameHandle") { draft.NameX = x; draft.NameY = y; }
        else { draft.AwardX = x; draft.AwardY = y; }
    }

    private void OnHandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control handle || _tool?.Certificate is not { } draft) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0.1;
        var delta = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => default };
        if (delta == default) return;
        var name = handle == NameHandle;
        SetPosition(draft, handle, (name ? draft.NameX : draft.AwardX) + delta.X,
            (name ? draft.NameY : draft.AwardY) + delta.Y);
        e.Handled = true;
    }

    private void EndDrag()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        _dragHandle = null;
        pointer?.Capture(null);
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e) => EndDrag();
    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void OnRetryPreview(object? sender, RoutedEventArgs e)
    {
        RequestPreview(force: true);
    }

    internal async Task WaitForPreviewAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (_previewCts is null && _tool?.HasCertificatePreviewError == true)
                throw new InvalidOperationException(_tool.CertificatePreviewStatus);
            if (_previewCts is null && _tool?.HasCertificatePreview == true && !_tool.IsCertificatePreviewBusy) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("奖状预览未完成：" + _tool?.CertificatePreviewStatus);
    }

    internal async Task<string> ProbeInteractionAsync()
    {
        await WaitForPreviewAsync();
        var draft = _tool!.Certificate!;
        var previousPreview = _tool.CertificatePreviewImage;
        var originalNameX = draft.NameX;
        draft.NameX = originalNameX + (originalNameX < 99 ? 0.1 : -0.1);
        if (!_tool.IsCertificatePreviewBusy || !_tool.HasCertificatePreview
            || !ReferenceEquals(previousPreview, _tool.CertificatePreviewImage)
            || _tool.ShowCertificatePreviewEmpty)
            throw new InvalidOperationException("更新预览时未保留旧图并显示更新状态。");
        await WaitForPreviewAsync();
        draft.NameX = originalNameX;
        await WaitForPreviewAsync();
        var rect = TemplateBounds();
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("底图定位区域为空。");
        var originalAwardX = draft.AwardX;
        var originalAwardY = draft.AwardY;
        if (draft.CanPrintAward)
        {
            draft.AwardX = draft.NameX;
            draft.AwardY = draft.NameY;
            OnFocusName(this, new RoutedEventArgs());
            if (!NameHandle.IsFocused || NameHandle.ZIndex <= AwardHandle.ZIndex)
                throw new InvalidOperationException("定位点重叠时无法选择姓名。");
            OnFocusAward(this, new RoutedEventArgs());
            if (!AwardHandle.IsFocused || AwardHandle.ZIndex <= NameHandle.ZIndex)
                throw new InvalidOperationException("定位点重叠时无法选择奖项。");
            draft.AwardX = originalAwardX;
            draft.AwardY = originalAwardY;
        }
        foreach (var handle in new[] { NameHandle, AwardHandle })
        {
            var isName = handle == NameHandle;
            if (!isName && string.IsNullOrWhiteSpace(draft.PreviewRecord?.Award)) continue;
            var x = isName ? draft.NameX : draft.AwardX;
            var y = isName ? draft.NameY : draft.AwardY;
            var center = new Point(rect.Left + rect.Width * x / 100, rect.Top + rect.Height * y / 100);
            var pointer = new Pointer(isName ? 101 : 102, PointerType.Mouse, true);
            PointerPressedEventArgs At(Point position) => new(handle, pointer, PreviewSurface, position, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
            OnHandlePressed(handle, At(center));
            if (_dragHandle != handle) throw new InvalidOperationException("定位点未进入拖动状态。");
            OnHandleMoved(handle, At(new Point(center.X + rect.Width * 0.03, center.Y - rect.Height * 0.02)));
            EndDrag();
            var actualX = isName ? draft.NameX : draft.AwardX;
            var actualY = isName ? draft.NameY : draft.AwardY;
            if (Math.Abs(actualX - (x + 3)) > 0.11 || Math.Abs(actualY - (y - 2)) > 0.11)
                throw new InvalidOperationException("指针处理器与归一化位置不同步。");
            var key = new KeyEventArgs { Key = Key.Right, KeyModifiers = KeyModifiers.Shift };
            OnHandleKeyDown(handle, key);
            if (!key.Handled || Math.Abs((isName ? draft.NameX : draft.AwardX) - (actualX + 1)) > 0.01)
                throw new InvalidOperationException("方向键与数值不同步。");
            var expectedLeft = rect.Left + rect.Width * (isName ? draft.NameX : draft.AwardX) / 100 - handle.Width / 2;
            if (Math.Abs(Canvas.GetLeft(handle) - expectedLeft) > 0.1)
                throw new InvalidOperationException("数值与定位点不同步。");
            await Task.Delay(50);
            var label = isName ? "姓名横向位置百分比" : "奖项横向位置百分比";
            var numeric = this.GetVisualDescendants().OfType<NumericUpDown>().Single(control =>
                Avalonia.Automation.AutomationProperties.GetName(control) == label);
            if (numeric.Value is not { } displayed || Math.Abs((double)displayed - (actualX + 1)) > 0.01)
                throw new InvalidOperationException("拖动后数值控件未更新。");
            numeric.Value = (decimal)(actualX + 2);
            await Task.Delay(50);
            if (Math.Abs((isName ? draft.NameX : draft.AwardX) - (actualX + 2)) > 0.01)
                throw new InvalidOperationException("数值控件未写回位置。");
            var numericLeft = rect.Left + rect.Width * (actualX + 2) / 100 - handle.Width / 2;
            if (Math.Abs(Canvas.GetLeft(handle) - numericLeft) > 0.1)
                throw new InvalidOperationException("数值控件修改后定位点未更新。");
        }
        await WaitForPreviewAsync();
        return "handler-pointer/name-award=pass (award skipped when blank); keyboard=pass; numeric-anchor-sync=pass; updating-retains-old-image=pass";
    }

    internal async Task ProbeFailureRetryAsync(string validTemplate)
    {
        var draft = _tool!.Certificate!;
        var broken = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllTextAsync(broken, "synthetic invalid image");
        try
        {
            draft.AssignTemplate(broken);
            var workflowStatus = draft.StatusText;
            for (var attempt = 0; attempt < 100 && !_tool.HasCertificatePreviewError; attempt++)
                await Task.Delay(100);
            if (!_tool.HasCertificatePreviewError || _tool.HasCertificatePreview)
                throw new InvalidOperationException("无效底图未进入预览失败状态。");
            if (draft.StatusText != workflowStatus)
                throw new InvalidOperationException("预览错误覆盖了字段映射状态。");
            await Task.Delay(400);
            if (_previewCts is not null) throw new InvalidOperationException("预览失败后发生自动重试循环。");
            await Task.Run(() => File.Copy(validTemplate, broken, true));
            OnRetryPreview(this, new RoutedEventArgs());
            await WaitForPreviewAsync();
            if (_tool.HasCertificatePreviewError || _tool.IsCertificatePreviewBusy)
                throw new InvalidOperationException("预览重试成功后仍保留失败状态。");
            draft.AssignTemplate(validTemplate);
            await WaitForPreviewAsync();
        }
        finally { File.Delete(broken); }
    }

    private void OnPickTemplate(object? sender, RoutedEventArgs e) => TemplatePickRequested?.Invoke(this, EventArgs.Empty);
    private void OnPickRoster(object? sender, RoutedEventArgs e) => RosterPickRequested?.Invoke(this, EventArgs.Empty);
    private void OnRun(object? sender, RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancelTool(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnOpenOutput(object? sender, RoutedEventArgs e) => OpenOutputRequested?.Invoke(this, EventArgs.Empty);
    private void OnClearTemplate(object? sender, RoutedEventArgs e) => _tool?.Certificate?.ClearTemplate();
    private void OnClearRoster(object? sender, RoutedEventArgs e) => _tool?.Certificate?.ClearRoster();

    public void ScrollSettingsToEnd() => SettingsScroll.Offset = new Vector(0,
        Math.Max(0, SettingsScroll.Extent.Height - SettingsScroll.Viewport.Height));

    public object CaptureLayoutMetrics()
    {
        Rect BoundsOf(Control control)
        {
            var origin = control.TranslatePoint(default, this) ?? default;
            return new Rect(origin, control.Bounds.Size);
        }

        object Measure(Control control)
        {
            var bounds = BoundsOf(control);
            return new { control.Name, X = bounds.X, Y = bounds.Y, Width = bounds.Width,
                Height = bounds.Height, control.IsVisible, control.IsEffectivelyVisible,
                control.IsEnabled, control.IsEffectivelyEnabled };
        }

        var settings = BoundsOf(SettingsScroll);
        var preview = BoundsOf(PreviewPane);
        var action = BoundsOf(ActionArea);
        var numerics = this.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        return new
        {
            Width = Bounds.Width, Height = Bounds.Height,
            Settings = Measure(SettingsScroll), Preview = Measure(PreviewPane), Action = Measure(ActionArea),
            Scroll = new { ViewportWidth = SettingsScroll.Viewport.Width, ViewportHeight = SettingsScroll.Viewport.Height,
                ExtentWidth = SettingsScroll.Extent.Width, ExtentHeight = SettingsScroll.Extent.Height,
                OffsetY = SettingsScroll.Offset.Y, HorizontalOverflow = SettingsScroll.Extent.Width > SettingsScroll.Viewport.Width + 1 },
            SettingsPreviewOverlap = settings.Intersects(preview),
            SettingsActionOverlap = settings.Intersects(action), PreviewActionOverlap = preview.Intersects(action),
            Controls = new Control[] { WorksheetPicker, NameColumnPicker, AwardColumnPicker,
                NamePlacementGroup, AwardPlacementGroup, MappingHint, PreviewStatus, NameHandle, AwardHandle }
                .Concat(numerics).Select(Measure).ToArray(),
            MinimumNumericWidth = numerics.Length == 0 ? 0 : numerics.Min(control => control.Bounds.Width),
            NumericEditors = numerics.Select(control => new
            {
                Label = Avalonia.Automation.AutomationProperties.GetName(control),
                Bounds = control.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } editor
                    ? Measure(editor) : null
            }).ToArray(),
            PreviewState = _tool?.CertificatePreviewStatus,
            OutputIsStale = _tool?.IsCertificateOutputStale
        };
    }
}
