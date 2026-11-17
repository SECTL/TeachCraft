using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SeatGrid.Core.Models;

namespace SeatGrid.App;

public partial class SeatCellViewModel : ObservableObject
{
    public SeatCellViewModel(SeatPosition position)
    {
        Position = position;
    }

    public SeatPosition Position { get; }

    public string PositionLabel => $"{Position.Group + 1}组 · {Position.Row + 1}排 · {Position.Column + 1}列";

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isFemale;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isAisle;
    [ObservableProperty] private Thickness _margin;
    [ObservableProperty] private bool _showEmptyFrame;
    [ObservableProperty] private double _width = 80;
    [ObservableProperty] private double _height = 80;
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private FontWeight _fontWeight = FontWeight.Normal;
    [ObservableProperty] private IBrush _fill = Brushes.White;
    [ObservableProperty] private IBrush _stroke = Brushes.Gray;
    [ObservableProperty] private IBrush _nameBrush = Brushes.Black;
    [ObservableProperty] private Thickness _borderThickness = new(1);
    [ObservableProperty] private string _automationName = string.Empty;

    private bool _emptyFrameWhenIdle;
    private bool _isDropTarget;
    private IBrush _strokeBeforeDrop = Brushes.Gray;
    private Thickness _borderBeforeDrop = new(1);

    private static readonly IBrush DropStrokeBrush = new SolidColorBrush(Color.Parse("#D97757"));

    public void Update(
        string displayName,
        bool isEmpty,
        bool isFemale,
        bool isSelected,
        bool isPinned,
        bool isAisle,
        double size,
        double fontSize,
        bool bold,
        IBrush seatBackground,
        IBrush emptyBackground,
        IBrush nameColor,
        IBrush femaleColor,
        IBrush border,
        bool hideEmptySeats)
    {
        DisplayName = displayName;
        IsEmpty = isEmpty;
        IsFemale = isFemale;
        IsSelected = isSelected;
        IsPinned = isPinned;
        IsAisle = isAisle;

        if (isAisle)
        {
            // 过道格：透明、无边框、不画虚线框，读屏念「过道」而不是「空座位」。
            Fill = Brushes.Transparent;
            Stroke = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Width = size;
            Height = size;
            _emptyFrameWhenIdle = false;
            ShowEmptyFrame = false;
            AutomationName = $"过道 {PositionLabel}";
            return;
        }
        Width = size;
        Height = size;
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal;
        FontSize = FitFontSize(displayName, fontSize, FontWeight, size);
        Fill = isEmpty ? emptyBackground : seatBackground;
        Stroke = border;
        NameBrush = isFemale ? femaleColor : nameColor;
        BorderThickness = isEmpty ? new Thickness(0) : new Thickness(1);
        _emptyFrameWhenIdle = isEmpty && !hideEmptySeats;
        ShowEmptyFrame = _emptyFrameWhenIdle && !isSelected;
        _isDropTarget = false;
        AutomationName = isEmpty
            ? $"空座位 {PositionLabel}"
            : $"{PositionLabel} {displayName}" + (isPinned ? "（已锁定）" : string.Empty);
    }

    /// <summary>
    /// 与导出渲染（SeatChartRenderer.DrawSeat）同一套缩名规则：姓名实测宽度超过格子
    /// 可用宽度（边长 - 10px）时按比例缩小字号，下限 8px，保证屏幕与导出图一致不溢出。
    ///
    /// 量宽失败时原样返回字号：<see cref="FormattedText"/> 依赖平台字体后端
    /// （<c>Avalonia.Platform.IFontManagerImpl</c>），无头环境或后端缺失时它会抛
    /// <see cref="InvalidOperationException"/>。那只是「量不出来」，不是「这个座位坏了」——
    /// 让它掀掉整次 <c>RefreshSeatVisuals</c> 会把一次无害的字号微调变成一片空白网格。
    /// 量不出来就用原字号（最坏情况是略大，与不缩字号的老行为一致）。
    /// </summary>
    private static double FitFontSize(string name, double fontSize, FontWeight weight, double cellSize)
    {
        if (string.IsNullOrWhiteSpace(name) || fontSize <= 0) return fontSize;

        var available = cellSize - 10;
        if (available <= 0) return fontSize;

        double measured;
        try
        {
            measured = new FormattedText(
                name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, weight),
                fontSize,
                Brushes.Black).Width;
        }
        catch (Exception)
        {
            return fontSize;
        }

        if (measured <= available || measured <= 0) return fontSize;
        return Math.Max(8, fontSize * available / measured);
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        ShowEmptyFrame = _emptyFrameWhenIdle && !selected;
    }

    /// <summary>拖拽悬停时临时换上强调色边框，松手或移走后由下一次 Update 还原。</summary>
    public void SetDropTarget(bool on)
    {
        if (on == _isDropTarget) return;
        _isDropTarget = on;
        if (on)
        {
            _strokeBeforeDrop = Stroke;
            _borderBeforeDrop = BorderThickness;
            Stroke = DropStrokeBrush;
            BorderThickness = new Thickness(2.2);
        }
        else
        {
            Stroke = _strokeBeforeDrop;
            BorderThickness = _borderBeforeDrop;
        }
    }
}
