using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SeatGrid.Core.Exam;
using TeachCraft.Shared;

namespace SeatGrid.App;

/// <summary>桌贴导出的选项。</summary>
public sealed record DeskStickerRequest(DeskStickerOptions Options, string ClassName, string ExamRoom);

/// <summary>
/// 桌贴设置窗：班级、考场、每页张数、是否印考号，右侧实时预览第一页。
/// 结构与竞名的导出设置窗同构——左设置、右预览、200ms 防抖、关窗取消在途渲染。
/// </summary>
public sealed class DeskStickerOptionsWindow : Window
{
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAF9F5"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E7E3D8"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#141413"));
    private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#3D3B36"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6F6A5E"));
    private static readonly IBrush Wash = new SolidColorBrush(Color.Parse("#FCFAF4"));

    private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(200);

    private readonly string _title;
    private readonly TextBox _className;
    private readonly TextBox _examRoom;
    private readonly ComboBox _layout;
    private readonly CheckBox _showNumber;
    private readonly Image _preview;
    private readonly TextBlock _previewHint;

    private CancellationTokenSource? _previewCts;
    private bool _closed;

    public DeskStickerOptionsWindow()
        : this("考场桌贴")
    {
    }

    public DeskStickerOptionsWindow(string title)
    {
        _title = title;

        Title = "导出考场桌贴";
        Width = 940;
        Height = 720;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Paper;
        Opened += (_, _) => WindowFit.Clamp(this);

        _className = new TextBox { Classes = { "field" }, Text = title, MinHeight = 32, FontSize = 11.5 };
        _examRoom = new TextBox { Classes = { "field" }, Text = "第 1 考场", MinHeight = 32, FontSize = 11.5 };
        _layout = new ComboBox
        {
            ItemsSource = new[] { "3 × 4（12 张/页）", "2 × 4（8 张/页）", "3 × 5（15 张/页）" },
            SelectedIndex = 0,
            MinHeight = 32,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _showNumber = new CheckBox { Content = "印考号", IsChecked = true, FontSize = 11.5 };
        _preview = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        _previewHint = new TextBlock
        {
            Text = "正在生成预览…", FontSize = 10.5, Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        Content = BuildContent();
        Closed += (_, _) => CancelPreview();
        _ = RefreshPreviewAsync();
    }

    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var header = new Border
        {
            BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 14),
        };
        var headerPanel = new StackPanel { Spacing = 3 };
        headerPanel.Children.Add(new TextBlock
        {
            Text = "导出考场桌贴", FontFamily = new FontFamily("Anthropic Serif, SimSun"),
            FontSize = 19, Foreground = Ink,
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = "A4 每页排若干张，含姓名、座号、考号与班级，四周有裁切线。",
            FontSize = 10.5, Foreground = Muted,
        });
        header.Child = headerPanel;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("360,*"), ColumnSpacing = 14, Margin = new Thickness(20, 14),
        };

        var left = new StackPanel { Spacing = 13 };
        left.Children.Add(Field("班级", _className));
        left.Children.Add(Field("考场", _examRoom));
        left.Children.Add(Field("每页张数", _layout));
        left.Children.Add(_showNumber);
        left.Children.Add(new TextBlock
        {
            Text = "桌贴按座号顺序排列；导出文件名不含学生姓名。",
            FontSize = 10, LineHeight = 16, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var previewFrame = new Border
        {
            Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
        };
        var previewPanel = new Panel();
        previewPanel.Children.Add(_preview);
        previewPanel.Children.Add(_previewHint);
        previewFrame.Child = previewPanel;
        Grid.SetColumn(previewFrame, 1);
        body.Children.Add(previewFrame);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Border
        {
            BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12),
        };
        var footerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
        };
        var cancel = new Button
        {
            Content = "取消", IsCancel = true, FontSize = 11.5, Padding = new Thickness(14, 7),
            Background = Card, Foreground = InkSoft, BorderBrush = Line, BorderThickness = new Thickness(1),
        };
        cancel.Click += (_, _) => Close(null);
        var confirm = new Button
        {
            Content = "选择位置并导出", IsDefault = true, FontSize = 11.5, Padding = new Thickness(16, 7),
            Background = Ink, Foreground = Paper, BorderThickness = new Thickness(0),
        };
        confirm.Click += (_, _) => Close(new DeskStickerRequest(BuildOptions(), _className.Text ?? string.Empty,
            _examRoom.Text ?? string.Empty));
        footerPanel.Children.Add(cancel);
        footerPanel.Children.Add(confirm);
        footer.Child = footerPanel;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _className.TextChanged += (_, _) => RequestPreview();
        _examRoom.TextChanged += (_, _) => RequestPreview();
        _layout.SelectionChanged += (_, _) => RequestPreview();
        _showNumber.IsCheckedChanged += (_, _) => RequestPreview();

        return root;
    }

    private static Control Field(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = Muted });
        panel.Children.Add(input);
        return panel;
    }

    private DeskStickerOptions BuildOptions()
    {
        var (columns, rows) = _layout.SelectedIndex switch
        {
            1 => (2, 4),
            2 => (3, 5),
            _ => (3, 4),
        };

        return new DeskStickerOptions
        {
            Columns = columns,
            Rows = rows,
            ClassName = _className.Text?.Trim() ?? string.Empty,
            ExamRoom = _examRoom.Text?.Trim() ?? string.Empty,
            ShowNumber = _showNumber.IsChecked == true,
        };
    }

    private void RequestPreview()
    {
        if (_closed) return;
        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        var previous = Interlocked.Exchange(ref _previewCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var token = _previewCts!.Token;

        try
        {
            await Task.Delay(PreviewDebounce, token);

            var options = BuildOptions();
            var stickers = new DeskStickerDocument(_title, new[]
            {
                new DeskSticker("示例同学", "2026001", 1, options.ClassName, options.ExamRoom),
                new DeskSticker("示例同学二", "2026002", 2, options.ClassName, options.ExamRoom),
                new DeskSticker("示例同学三", "2026003", 3, options.ClassName, options.ExamRoom),
            }, "本机生成，文件未上传");

            var png = await Task.Run(() => DeskStickerRenderer.Render(stickers, options, 0, 1.1f), token);
            if (token.IsCancellationRequested || _closed) return;

            using var stream = new MemoryStream(png);
            var bitmap = new Bitmap(stream);
            (_preview.Source as IDisposable)?.Dispose();
            _preview.Source = bitmap;
            _previewHint.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _closed) return;
            _previewHint.Text = "预览生成失败：" + ex.Message;
            _previewHint.IsVisible = true;
        }
    }

    private void CancelPreview()
    {
        _closed = true;
        var cts = Interlocked.Exchange(ref _previewCts, null);
        cts?.Cancel();
        cts?.Dispose();
        (_preview.Source as IDisposable)?.Dispose();
        _preview.Source = null;
    }
}
