using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TeachCraft.Shared;

namespace SeatGrid.App;

/// <summary>
/// 快速换座：双击座位后输入姓名或拼音首字母（如 ZS 找张三），回车对调。
/// 50 多人的名单里，打字比拖拽快得多。
/// </summary>
public sealed class QuickSwapWindow : Window
{
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAF9F5"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E7E3D8"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#141413"));
    private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#3D3B36"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6F6A5E"));
    private static readonly IBrush Wash = new SolidColorBrush(Color.Parse("#FCFAF4"));

    private readonly SeatGridViewModel _viewModel;
    private readonly SeatCellViewModel _source;
    private readonly TextBox _search;
    private readonly ListBox _results;
    private readonly TextBlock _hint;

    /// <summary>用户点了「对调」并成功执行时为 true。</summary>
    public bool Applied { get; private set; }

    public QuickSwapWindow()
        : this(new SeatGridViewModel(), new SeatCellViewModel(new Core.Models.SeatPosition(0, 0, 0)))
    {
    }

    public QuickSwapWindow(SeatGridViewModel viewModel, SeatCellViewModel source)
    {
        _viewModel = viewModel;
        _source = source;

        Title = "快速换人";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 420;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Paper;
        Opened += (_, _) =>
        {
            WindowFit.Clamp(this);
            _search?.Focus();
        };

        _search = new TextBox
        {
            MinHeight = 34, FontSize = 12,
            PlaceholderText = "输入姓名或拼音首字母，如 ZS",
            Background = Wash, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 6),
        };
        _results = new ListBox
        {
            Height = 168, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        _hint = new TextBlock
        {
            Text = "回车对调 · Esc 取消", FontSize = 10, Foreground = Muted,
        };

        _search.TextChanged += (_, _) => RefreshResults();
        _search.KeyDown += OnSearchKeyDown;
        _results.DoubleTapped += (_, _) => Apply();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Content = BuildContent();
        RefreshResults();
    }

    private Control BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), Spacing = 10 };

        root.Children.Add(new TextBlock
        {
            Text = $"换人 · {_source.PositionLabel}", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ink,
        });
        root.Children.Add(new TextBlock
        {
            Text = _source.IsEmpty ? "这个座位是空的。" : $"当前：{_source.DisplayName}",
            FontSize = 11, Foreground = Muted,
        });
        root.Children.Add(_search);
        root.Children.Add(_results);
        root.Children.Add(_hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
        };
        var cancel = new Button
        {
            Content = "取消", IsCancel = true, FontSize = 11.5, Padding = new Thickness(14, 7),
            Background = Card, Foreground = InkSoft, BorderBrush = Line, BorderThickness = new Thickness(1),
        };
        cancel.Click += (_, _) => Close();
        var apply = new Button
        {
            Content = "对调", IsDefault = true, FontSize = 11.5, Padding = new Thickness(16, 7),
            Background = Ink, Foreground = Paper, BorderThickness = new Thickness(0),
        };
        apply.Click += (_, _) => Apply();
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        root.Children.Add(buttons);

        return root;
    }

    private void RefreshResults()
    {
        var keyword = _search.Text ?? string.Empty;
        var matches = _viewModel.FindStudents(keyword);

        _results.ItemsSource = matches.Select(s =>
            $"{s.Name} · {s.SeatLabel}" + (s.Model.Number.Length > 0 ? $" · {s.Model.Number}" : string.Empty))
            .ToList();

        if (matches.Count > 0) _results.SelectedIndex = 0;

        _hint.Text = keyword.Length == 0
            ? "输入姓名或拼音首字母，如 ZS"
            : matches.Count == 0
                ? $"没有找到「{keyword}」"
                : matches.Count > 1
                    ? $"找到 {matches.Count} 人，可再输入几个字缩小范围"
                    : "回车对调 · Esc 取消";
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Apply();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _results.ItemCount > 0)
        {
            _results.SelectedIndex = Math.Min(_results.ItemCount - 1, _results.SelectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _results.ItemCount > 0)
        {
            _results.SelectedIndex = Math.Max(0, _results.SelectedIndex - 1);
            e.Handled = true;
        }
    }

    private void Apply()
    {
        var keyword = _search.Text ?? string.Empty;
        if (keyword.Trim().Length == 0) return;

        _viewModel.QuickSwapByName(_source, keyword);
        Applied = true;
        Close();
    }
}
