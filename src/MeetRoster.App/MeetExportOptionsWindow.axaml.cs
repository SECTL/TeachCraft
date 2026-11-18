using System;
using System.IO;
using System.Linq;
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

        // 关窗时取消在途渲染：否则后台任务完成后会往已销毁的窗口写位图。
        Closed += (_, _) => _viewModel.CancelPendingPreview();
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.TryBuild(out var request, out var error))
            Close(request);
        else
            _viewModel.Error = error;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>「自动」：放弃手改的文件名，按当前导出对象重新生成。</summary>
    private void OnResetFileName(object? sender, RoutedEventArgs e) => _viewModel.ResetFileName();

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

    /// <summary>
    /// 设计走查用：把样式下拉里每一项的可选状态写成文本。
    /// 下拉弹层是独立 popup，截图抓不到；置灰是否真的生效只能这样取证（审计 P2-17）。
    /// </summary>
    public void DumpStyleAvailability(string path)
    {
        var lines = new List<string>();
        StyleBox.IsDropDownOpen = true;
        StyleBox.UpdateLayout();

        // 容器是弹层里的 ComboBoxItem：取它的 IsEnabled，而不是模板里那个 StackPanel。
        var items = StyleBox.GetRealizedContainers().OfType<ComboBoxItem>().ToList();
        foreach (var item in items)
        {
            var option = item.DataContext as MeetExcelStyleOption;
            lines.Add($"{option?.Label ?? "(null)"} " +
                      $"containerIsEnabled={item.IsEnabled} " +
                      $"optionIsAvailable={option?.IsAvailable} " +
                      $"opacity={option?.ItemOpacity} " +
                      $"hint={option?.Hint}");
        }

        if (items.Count == 0) lines.Add("(没有实现化的下拉项)");

        StyleBox.IsDropDownOpen = false;
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
    }
}
