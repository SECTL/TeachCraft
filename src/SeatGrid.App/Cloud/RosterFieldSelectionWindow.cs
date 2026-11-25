using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SeatGrid.Core.Cloud;
using TeachCraft.Shared;

namespace SeatGrid.App.Cloud;

/// <summary>
/// 发布共享名单前的字段勾选窗。姓名 / 性别 / 学号是必填项，只做展示；
/// 五个可选字段默认全关，由老师逐项勾选。更新已有名单时用原字段预填。
/// </summary>
public sealed class RosterFieldSelectionWindow : Window
{
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAF9F5"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E7E3D8"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#141413"));
    private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#3D3B36"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6F6A5E"));
    private static readonly IBrush Wash = new SolidColorBrush(Color.Parse("#FCFAF4"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D97757"));

    private const double Gap = 12;
    private const double Pad = 20;

    private readonly TextBox _name;
    private readonly CheckBox _height;
    private readonly CheckBox _vision;
    private readonly CheckBox _score;
    private readonly CheckBox _notes;
    private readonly CheckBox _originalGroup;

    private RosterFieldSelectionWindow(
        ISeatGridCloudBridge cloud, string suggestedName, SeatGridRosterFields initial)
    {
        Title = "共享名单 · 选择字段";
        Width = 540;
        Height = 620;
        MinWidth = 480;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Paper;
        Opened += (_, _) => WindowFit.Clamp(this);

        _name = new TextBox
        {
            Classes = { "field" },
            Text = suggestedName,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _height = new CheckBox { Content = "身高", IsChecked = initial.Height, FontSize = 11.5 };
        _vision = new CheckBox { Content = "视力", IsChecked = initial.Vision, FontSize = 11.5 };
        _score = new CheckBox { Content = "成绩", IsChecked = initial.Score, FontSize = 11.5 };
        _notes = new CheckBox { Content = "备注", IsChecked = initial.Notes, FontSize = 11.5 };
        _originalGroup = new CheckBox { Content = "原小组", IsChecked = initial.OriginalGroup, FontSize = 11.5 };

        Content = BuildContent(cloud);
    }

    /// <summary>返回用户确认的名称与字段；取消返回 null。</summary>
    public static Task<(string Name, SeatGridRosterFields Fields)?> ShowAsync(
        Window owner, ISeatGridCloudBridge cloud, string suggestedName, SeatGridRosterFields initial)
    {
        var window = new RosterFieldSelectionWindow(cloud, suggestedName, initial);
        return window.ShowDialog<(string Name, SeatGridRosterFields Fields)?>(owner);
    }

    private Control BuildContent(ISeatGridCloudBridge cloud)
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var header = new Border { Classes = { "dialog-header" } };
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var headerPanel = new StackPanel { Spacing = 3 };
        headerPanel.Children.Add(new TextBlock { Classes = { "section-title" }, Text = "发布共享名单", FontSize = 18 });
        headerPanel.Children.Add(new TextBlock { Classes = { "helper" }, Text = "先选好名单名称与要共享的字段，再上传到云端。" });
        Grid.SetColumn(headerPanel, 0);
        headerRow.Children.Add(headerPanel);
        var eyebrow = new TextBlock { Classes = { "eyebrow" }, Text = "ROSTER" };
        Grid.SetColumn(eyebrow, 1);
        headerRow.Children.Add(eyebrow);
        header.Child = headerRow;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new ScrollViewer
        {
            Padding = new Thickness(Pad, 16),
        };
        var stack = new StackPanel { Spacing = Gap + 4 };

        stack.Children.Add(Section("名单名称", _name));

        var required = new StackPanel { Spacing = 8 };
        required.Children.Add(new TextBlock { Classes = { "subhead" }, Text = "必填字段" });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var label in new[] { "姓名", "性别", "学号" })
            chips.Children.Add(Chip(label));
        required.Children.Add(chips);
        stack.Children.Add(required);

        var optional = new StackPanel { Spacing = 8 };
        optional.Children.Add(new TextBlock { Classes = { "subhead" }, Text = "可选字段（默认全关）" });
        var checks = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var box in new[] { _height, _vision, _score, _notes, _originalGroup })
        {
            box.Margin = new Thickness(0, 0, 18, 6);
            box.MinWidth = 76;
            checks.Children.Add(box);
        }
        optional.Children.Add(checks);
        stack.Children.Add(optional);

        var consent = new Border { Classes = { "chart-card" }, Padding = new Thickness(12) };
        consent.Child = new TextBlock
        {
            Classes = { "helper" },
            Text = cloud.RosterConsentText,
            LineHeight = 17,
        };
        stack.Children.Add(consent);

        body.Content = stack;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Border { Classes = { "dialog-footer" } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
        };
        var cancel = new Button
        {
            Classes = { "ghost" }, Content = "取消", IsCancel = true, FontSize = 11.5, Padding = new Thickness(14, 7),
        };
        cancel.Click += (_, _) => Close(null);
        var confirm = new Button
        {
            Classes = { "primary" }, Content = "确定", IsDefault = true, FontSize = 11.5, Padding = new Thickness(16, 7),
        };
        confirm.Click += (_, _) =>
        {
            var name = _name.Text?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                _name.Focus();
                return;
            }
            Close((name, new SeatGridRosterFields(
                _height.IsChecked == true, _vision.IsChecked == true, _score.IsChecked == true,
                _notes.IsChecked == true, _originalGroup.IsChecked == true)));
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static Control Section(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Classes = { "field-label" }, Text = label });
        panel.Children.Add(input);
        return panel;
    }

    private static Border Chip(string label) => new()
    {
        Classes = { "seat-tag" },
        Padding = new Thickness(10, 4),
        Child = new TextBlock { Text = label, FontSize = 10.5, Foreground = InkSoft },
    };
}
