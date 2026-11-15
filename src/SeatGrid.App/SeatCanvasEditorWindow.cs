using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;
using TeachCraft.Shared;

namespace SeatGrid.App;

/// <summary>
/// 教室画布编辑器：一张迷你网格，点格子切换「座位 / 过道」。
///
/// 做成独立窗口而不是主窗口里的一个模式，是因为主窗口的座位是 Button，
/// 指针事件已被窗口层的 Seat/Row/Column 三态拖拽状态机接管，
/// 再塞第四种「刷子」模式会和 _suppressSeatClick、幽灵飞行的时序互相打架。
/// </summary>
public sealed class SeatCanvasEditorWindow : Window
{
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAF9F5"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E7E3D8"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#141413"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6F6A5E"));
    private static readonly IBrush AisleFill = new SolidColorBrush(Color.Parse("#F4F2EC"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D97757"));

    private readonly SeatChartLayout _layout;
    private SeatChartCanvas _canvas;
    private readonly Dictionary<SeatPosition, Border> _cells = new();

    /// <summary>点「确定」后的画布；取消时为 null。</summary>
    public SeatChartCanvas? Result { get; private set; }

    public SeatCanvasEditorWindow()
        : this(new SeatChartLayout(4, 6, 2), SeatChartCanvas.Empty)
    {
    }

    public SeatCanvasEditorWindow(SeatChartLayout layout, SeatChartCanvas canvas)
    {
        _layout = layout;
        _canvas = canvas.Normalize(layout);

        Title = "编辑教室画布";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Paper;
        Opened += (_, _) => WindowFit.Clamp(this);

        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 14 };

        root.Children.Add(new TextBlock
        {
            Text = "编辑教室画布", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Ink,
        });
        root.Children.Add(new TextBlock
        {
            Text = $"当前布局 {_layout.Groups} 组 × {_layout.Rows} 排 × {_layout.Columns} 列。点格子切换「座位 / 过道」。",
            FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 18,
        });

        root.Children.Add(BuildGrid());

        root.Children.Add(new TextBlock
        {
            Text = "灰色格子不会坐人，也不会出现在自动排座里。走廊通道请在右侧面板调间距。",
            FontSize = 10, LineHeight = 16, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
        };
        buttons.Children.Add(MakeButton("清空画布", Muted, clear: true));
        buttons.Children.Add(MakeButton("取消", Muted, cancel: true));
        buttons.Children.Add(MakeButton("确定", Ink, confirm: true));
        root.Children.Add(buttons);

        return root;
    }

    private Control BuildGrid()
    {
        var groups = SeatChartComposer.GroupOrder(_layout, ViewMode.Teacher);
        var columns = SeatChartComposer.ColumnOrder(_layout, ViewMode.Teacher);
        var rows = SeatChartComposer.RowOrder(_layout, ViewMode.Teacher);

        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            ColumnSpacing = 4,
            RowSpacing = 4,
        };

        for (var i = 0; i < columns.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(48, GridUnitType.Pixel));
        for (var i = 0; i < rows.Length; i++) grid.RowDefinitions.Add(new RowDefinition(40, GridUnitType.Pixel));

        // 每个格子是一组「列 × 排」，按组横向排列：外层再套一个横向 StackPanel。
        var outer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };

        foreach (var group in groups)
        {
            var groupGrid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
            for (var i = 0; i < columns.Length; i++)
                groupGrid.ColumnDefinitions.Add(new ColumnDefinition(48, GridUnitType.Pixel));
            for (var i = 0; i < rows.Length; i++)
                groupGrid.RowDefinitions.Add(new RowDefinition(40, GridUnitType.Pixel));

            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < columns.Length; c++)
                {
                    var position = new SeatPosition(group, rows[r], columns[c]);
                    var cell = BuildCell(position);
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    groupGrid.Children.Add(cell);
                    _cells[position] = cell;
                }
            }

            outer.Children.Add(groupGrid);
        }

        return new Border
        {
            Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14),
            Child = outer,
        };
    }

    private Border BuildCell(SeatPosition position)
    {
        var border = new Border
        {
            Background = _canvas.IsDisabled(position) ? AisleFill : Card,
            BorderBrush = _canvas.IsDisabled(position) ? Brushes.Transparent : Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = _canvas.IsDisabled(position) ? string.Empty : $"{position.Group + 1}·{position.Row + 1}·{position.Column + 1}",
                FontSize = 9,
                Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        border.PointerPressed += (_, _) => Toggle(position);
        return border;
    }

    private void Toggle(SeatPosition position)
    {
        _canvas = _canvas.Toggle(position);
        var cell = _cells[position];
        var aisle = _canvas.IsDisabled(position);
        cell.Background = aisle ? AisleFill : Card;
        cell.BorderBrush = aisle ? Brushes.Transparent : Line;
        if (cell.Child is TextBlock text)
            text.Text = aisle ? string.Empty : $"{position.Group + 1}·{position.Row + 1}·{position.Column + 1}";
    }

    private Button MakeButton(string text, IBrush foreground, bool clear = false, bool cancel = false, bool confirm = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(16, 7),
            FontSize = 11.5,
            IsCancel = cancel,
            IsDefault = confirm,
            Background = confirm ? Ink : Card,
            Foreground = confirm ? Paper : foreground,
            BorderBrush = confirm ? Ink : Line,
            BorderThickness = new Thickness(1),
        };

        if (clear)
        {
            button.Click += (_, _) =>
            {
                _canvas = SeatChartCanvas.Empty;
                foreach (var (position, cell) in _cells)
                {
                    cell.Background = Card;
                    cell.BorderBrush = Line;
                    if (cell.Child is TextBlock text)
                        text.Text = $"{position.Group + 1}·{position.Row + 1}·{position.Column + 1}";
                }
            };
        }
        else if (cancel)
        {
            button.Click += (_, _) => Close();
        }
        else
        {
            button.Click += (_, _) =>
            {
                Result = _canvas.Normalize(_layout);
                Close();
            };
        }

        return button;
    }
}
