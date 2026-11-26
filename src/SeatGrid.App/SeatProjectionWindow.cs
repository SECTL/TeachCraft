using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SeatGrid.Core.Rendering;

namespace SeatGrid.App;

/// <summary>
/// 全屏投影：把座位表铺满屏幕，供教室大屏展示。
///
/// 直接绑定同一个 <see cref="SeatGridViewModel"/>，不新建文档——所见即主窗口
/// 同一份 <see cref="SeatChartDocument"/>（AGENTS 约束自然满足）。
/// 整张表用 Viewbox 缩放，字号随窗口自适应，不需要额外代码。
/// </summary>
public sealed class SeatProjectionWindow : Window
{
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAF9F5"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#141413"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6F6A5E"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E7E3D8"));

    private readonly SeatGridViewModel _viewModel;
    private readonly TextBlock _subtitle;

    public SeatProjectionWindow()
        : this(new SeatGridViewModel())
    {
    }

    public SeatProjectionWindow(SeatGridViewModel viewModel)
    {
        _viewModel = viewModel;

        Title = "归位 · 投影";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        Background = Paper;
        Cursor = new Cursor(StandardCursorType.None);
        _subtitle = new TextBlock
        {
            Text = viewModel.InfoLine, FontSize = 14, Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Content = BuildContent();
        KeyDown += OnKeyDown;
        PointerMoved += (_, _) => Cursor = new Cursor(StandardCursorType.None);
    }

    public void RefreshChart() => Content = BuildContent();

    private Control BuildContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(40, 28),
        };

        var title = new TextBlock
        {
            Text = _viewModel.Title,
            FontFamily = new FontFamily("Anthropic Serif, SimSun"),
            FontSize = 40,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(title);
        header.Children.Add(_subtitle);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 座位表：Viewbox 包住整张表，字号与格子随屏幕自适应。
        var chart = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Margin = new Thickness(0, 24),
            Child = BuildChart(),
        };
        Grid.SetRow(chart, 1);
        root.Children.Add(chart);

        var hint = new TextBlock
        {
            Text = "Esc 退出投影 · ←/→ 切换视角",
            FontSize = 12,
            Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6,
        };
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        return root;
    }

    /// <summary>
    /// 座位网格：与主窗口同一套分组/行/座位结构，只是去掉拖拽与选中。
    /// 摆放顺序仍由 SeatChartComposer 的三个 Order 决定（ViewModel 的 Groups 已经按它构建）。
    /// </summary>
    private Control BuildChart()
    {
        var groups = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        foreach (var group in _viewModel.Groups)
        {
            var rows = new StackPanel { Spacing = 6 };
            rows.Children.Add(new TextBlock
            {
                Text = group.Label, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = _viewModel.GroupLabelBrush, HorizontalAlignment = HorizontalAlignment.Center,
                IsVisible = group.ShowHints,
            });

            foreach (var row in group.Rows)
            {
                var seats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var seat in row.Seats)
                {
                    seats.Children.Add(new Border
                    {
                        Width = seat.Width,
                        Height = seat.Height,
                        Background = seat.Fill,
                        BorderBrush = seat.Stroke,
                        BorderThickness = seat.BorderThickness,
                        CornerRadius = new CornerRadius(6),
                        Child = new TextBlock
                        {
                            Text = seat.DisplayName,
                            FontSize = seat.FontSize,
                            FontWeight = seat.FontWeight,
                            Foreground = seat.NameBrush,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    });
                }

                rows.Children.Add(seats);
            }

            groups.Children.Add(rows);
        }

        return groups;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Right:
                _viewModel.IsStudentView = !_viewModel.IsStudentView;
                Content = BuildContent();
                e.Handled = true;
                break;
        }
    }
}
