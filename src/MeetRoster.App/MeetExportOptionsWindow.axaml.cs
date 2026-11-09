using Avalonia.Controls;
using Avalonia.Interactivity;
using MeetRoster.Core.Models;
using TeachCraft.Shared;

namespace MeetRoster.App;

public partial class MeetExportOptionsWindow : Window
{
    private readonly MeetExportOptionsViewModel _viewModel;

    public MeetExportOptionsWindow()
        : this(new MeetProject(), null)
    {
    }

    public MeetExportOptionsWindow(MeetProject project, string? currentClass)
    {
        InitializeComponent();
        _viewModel = new MeetExportOptionsViewModel(project, currentClass);
        DataContext = _viewModel;
        Opened += (_, _) =>
        {
            WindowFit.Clamp(this);
            // 打开即画第一版预览，用户不用先动设置才看得到东西。
            _viewModel.RequestPreview();
        };
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.TryBuild(out var request, out var error))
            Close(request);
        else
            _viewModel.Error = error;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>设计走查用：把窗口渲染成 PNG。</summary>
    public void CaptureTo(string path)
    {
        var pixelSize = new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            pixelSize, new Avalonia.Vector(96, 96));
        bitmap.Render(this);
        using var stream = System.IO.File.Create(path);
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
