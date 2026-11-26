using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using TeachCraft.Core.Updating;
using TeachCraft.Shared;

namespace TeachCraft.App.Views;

/// <summary>
/// 应用内更新完成后的单次更新日志弹窗。
/// 数据全部来自内置 CHANGELOG.md 或下载阶段留存的 Release 说明，窗口本身不做任何联网请求。
/// </summary>
public partial class UpdateChangelogWindow : Window
{
    public UpdateChangelogWindow()
        : this(new UpdateChangelogDocument(
            SemanticVersion.Parse("1.44.0"),
            SemanticVersion.Parse("1.45.0"),
            "已更新至 v1.45.0",
            "从 v1.44.0 升级完成",
            Array.Empty<ChangelogRelease>(),
            IsFallbackSummary: true))
    {
    }

    public UpdateChangelogWindow(UpdateChangelogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        InitializeComponent();
        DataContext = document;
        Opened += (_, _) => WindowFit.Clamp(this);
    }

    public void CaptureTo(string path)
    {
        UpdateLayout();
        var size = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private void OnAcknowledge(object? sender, RoutedEventArgs e) => Close();
}
