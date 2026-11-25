using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SeatGrid.Core.Cloud;
using TeachCraft.Shared;

namespace SeatGrid.App.Cloud;

/// <summary>云端窗口打开时先落在哪一页。</summary>
public enum SeatGridCloudTab
{
    Rosters,
    Documents,
}

/// <summary>
/// 归位的云端存档窗：共享名单与座位表两页，每页都能上传当前内容或从云端拉取。
/// 全部网络动作都走桥接接口、都可取消；关窗即取消在途操作。
/// </summary>
public sealed class SeatGridCloudWindow : Window
{
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FAF9F5"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E7E3D8"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#141413"));
    private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#3D3B36"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6F6A5E"));
    private static readonly IBrush Wash = new SolidColorBrush(Color.Parse("#FCFAF4"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D97757"));
    private static readonly IBrush AccentWash = new SolidColorBrush(Color.Parse("#FBE4D7"));

    private static readonly FuncDataTemplate RowTemplate = new(
        typeof(string), (item, _) => BuildRow((string)item!), false);

    private const double Pad = 16;

    public const string RosterDeletedLinkClearedMessage = "已删除云端名单，本地关联已解除。";
    public const string ExamSnapshotBlockedMessage = "考场快照不能上传云端。请先返回日常座位。";

    private readonly ISeatGridCloudBridge _cloud;
    private readonly SeatGridCloudTab _initialTab;
    private readonly IReadOnlyList<SeatGridRosterStudent>? _students;
    private readonly SeatGridRosterFields _fields;
    private readonly string _suggestedName;
    private SeatGridCloudDocument? _document;
    private readonly Func<SeatGridCloudDocument?>? _buildDocument;
    private string? _existingId;
    private int _expectedRevision;
    private readonly Action<SeatGridRosterEntry>? _onRosterSaved;
    private readonly Action<SeatGridRosterPick>? _onRosterPicked;
    private readonly Action<SeatGridCloudSaveResult>? _onDocumentSaved;
    private readonly Action<SeatGridCloudPull>? _onDocumentPicked;
    private readonly Action<string>? _onDocumentDeleted;
    private readonly string? _linkedRosterId;
    private readonly Action<string>? _onRosterDeleted;

    private Button? _rosterTabButton;
    private Button? _documentTabButton;
    private Control? _rosterTabContent;
    private Control? _documentTabContent;
    private int _selectedTabIndex;
    private readonly ListBox _rosterList;
    private readonly ListBox _documentList;
    private readonly Control _rosterEmpty;
    private readonly Control _documentEmpty;
    private TextBlock? _headerStatus;
    private Button? _headerAccountLink;
    private readonly TextBlock _busy;
    private readonly TextBlock _notice;
    private readonly TextBlock _error;
    private readonly Button _retry;
    private readonly Button _rosterUpload;
    private readonly Button _rosterPull;
    private readonly Button _rosterDelete;
    private readonly Button _documentUpload;
    private readonly Button _documentSaveAsNew;
    private readonly Button _documentPull;
    private readonly Button _documentDelete;

    private IReadOnlyList<SeatGridRosterEntry> _rosters = Array.Empty<SeatGridRosterEntry>();
    private IReadOnlyList<SeatGridCloudEntry> _documents = Array.Empty<SeatGridCloudEntry>();
    private CancellationTokenSource? _opsCts;
    private Func<Task>? _lastOperation;
    private bool _closed;

    private SeatGridCloudWindow(
        ISeatGridCloudBridge cloud,
        SeatGridCloudTab initialTab,
        IReadOnlyList<SeatGridRosterStudent>? students,
        SeatGridRosterFields fields,
        string suggestedName,
        SeatGridCloudDocument? document,
        Func<SeatGridCloudDocument?>? buildDocument,
        string? existingId,
        int expectedRevision,
        Action<SeatGridRosterEntry>? onRosterSaved,
        Action<SeatGridRosterPick>? onRosterPicked,
        Action<SeatGridCloudSaveResult>? onDocumentSaved,
        Action<SeatGridCloudPull>? onDocumentPicked,
        Action<string>? onDocumentDeleted,
        string? linkedRosterId = null,
        Action<string>? onRosterDeleted = null)
    {
        _cloud = cloud;
        _initialTab = initialTab;
        _students = students;
        _fields = fields;
        _suggestedName = suggestedName;
        _document = document;
        _buildDocument = buildDocument;
        _existingId = existingId;
        _expectedRevision = expectedRevision;
        _onRosterSaved = onRosterSaved;
        _onRosterPicked = onRosterPicked;
        _onDocumentSaved = onDocumentSaved;
        _onDocumentPicked = onDocumentPicked;
        _onDocumentDeleted = onDocumentDeleted;
        _linkedRosterId = linkedRosterId;
        _onRosterDeleted = onRosterDeleted;

        Title = "云端存档 · 归位";
        Width = 820;
        Height = 620;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Paper;
        Opened += (_, _) => WindowFit.Clamp(this);

        _rosterList = new ListBox
        {
            Classes = { "sg-card-list" },
            MinHeight = 220, Padding = new Thickness(4), ItemTemplate = RowTemplate,
        };
        _documentList = new ListBox
        {
            Classes = { "sg-card-list" },
            MinHeight = 220, Padding = new Thickness(4), ItemTemplate = RowTemplate,
        };
        AutomationProperties.SetName(_rosterList, "共享名单列表");
        AutomationProperties.SetName(_documentList, "座位表列表");

        _rosterEmpty = BuildEmptyState(
            "还没有共享名单", "上传一份名单后，就能在这里管理与拉取。", "上传当前名单…", OnRosterUploadClick);
        _documentEmpty = BuildEmptyState(
            "还没有座位表文档", "上传当前座位表后，就能在这里保存与拉取。", "上传当前座位表…", OnDocumentUploadClick);

        _busy = new TextBlock { Text = "", FontSize = 10.5, Foreground = Muted, IsVisible = false };
        _notice = new TextBlock { Text = "", FontSize = 10.5, Foreground = Ink, IsVisible = false };
        _error = new TextBlock
        {
            Text = "", FontSize = 10.5, Foreground = Accent, TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _retry = ActionButton("重试", primary: false);
        _retry.IsVisible = false;
        AutomationProperties.SetName(_retry, "重试");
        _retry.Click += (_, _) =>
        {
            if (_lastOperation is { } op) _ = op();
        };

        _rosterUpload = ActionButton("上传当前名单…", primary: true);
        _rosterPull = ActionButton("从云端拉取…", primary: false);
        _rosterDelete = ActionButton("删除", primary: false);
        _documentUpload = ActionButton("上传当前座位表…", primary: true);
        _documentSaveAsNew = ActionButton("另存为新副本…", primary: false);
        _documentPull = ActionButton("从云端拉取…", primary: false);
        _documentDelete = ActionButton("删除", primary: false);
        AutomationProperties.SetName(_rosterUpload, "上传当前名单");
        AutomationProperties.SetName(_rosterPull, "从云端拉取");
        AutomationProperties.SetName(_rosterDelete, "删除共享名单");
        AutomationProperties.SetName(_documentUpload, "上传当前座位表");
        AutomationProperties.SetName(_documentSaveAsNew, "另存为新副本");
        AutomationProperties.SetName(_documentPull, "从云端拉取座位表");
        AutomationProperties.SetName(_documentDelete, "删除云端座位表");

        _rosterUpload.Click += OnRosterUploadClick;
        _rosterPull.Click += (_, _) => _ = PullRosterAsync();
        _rosterDelete.Click += (_, _) => _ = DeleteRosterAsync();
        _documentUpload.Click += OnDocumentUploadClick;
        _documentSaveAsNew.Click += (_, _) => _ = UploadDocumentAsync(saveAsNew: true);
        _documentPull.Click += (_, _) => _ = PullDocumentAsync();
        _documentDelete.Click += (_, _) => _ = DeleteDocumentAsync();

        _rosterList.SelectionChanged += (_, _) => UpdateButtons();
        _documentList.SelectionChanged += (_, _) => UpdateButtons();

        _selectedTabIndex = initialTab == SeatGridCloudTab.Rosters ? 0 : 1;

        Content = BuildRoot();
        _cloud.StateChanged += OnCloudStateChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _cloud.StateChanged -= OnCloudStateChanged;
            _opsCts?.Cancel();
        };

        _ = RefreshAsync();
    }

    /// <summary>打开共享名单页。</summary>
    public static void OpenRosters(
        ISeatGridCloudBridge cloud,
        Window owner,
        IReadOnlyList<SeatGridRosterStudent> students,
        SeatGridRosterFields fields,
        string suggestedName,
        Action<SeatGridRosterEntry> onSaved,
        Action<SeatGridRosterPick> onPicked,
        SeatGridCloudDocument? document = null,
        Func<SeatGridCloudDocument?>? buildDocument = null,
        string? existingDocumentId = null,
        int expectedDocumentRevision = 0,
        Action<SeatGridCloudSaveResult>? onDocumentSaved = null,
        Action<SeatGridCloudPull>? onDocumentPicked = null,
        Action<string>? onDocumentDeleted = null,
        string? linkedRosterId = null,
        Action<string>? onRosterDeleted = null)
        => new SeatGridCloudWindow(
            cloud, SeatGridCloudTab.Rosters, students, fields, suggestedName,
            document, buildDocument, existingDocumentId, expectedDocumentRevision,
            onSaved, onPicked, onDocumentSaved, onDocumentPicked, onDocumentDeleted,
            linkedRosterId, onRosterDeleted).Show(owner);

    /// <summary>走查取证：创建共享名单页窗口但不显示，供截图通道设定尺寸与选中行。</summary>
    public static SeatGridCloudWindow CreateForCapture(ISeatGridCloudBridge cloud)
        => new(cloud, SeatGridCloudTab.Rosters, Array.Empty<SeatGridRosterStudent>(),
            SeatGridRosterFields.None, string.Empty, null, null, null, 0, null, null, null, null, null);

    /// <summary>走查取证：按名称选中一份共享名单。</summary>
    public void SelectRosterForCapture(string name)
    {
        var index = _rosters.ToList().FindIndex(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (index >= 0) _rosterList.SelectedIndex = index;
    }

    /// <summary>打开座位表页。</summary>
    public static void OpenDocuments(
        ISeatGridCloudBridge cloud,
        Window owner,
        SeatGridCloudDocument? document,
        string? existingId,
        int expectedRevision,
        Action<SeatGridCloudSaveResult> onSaved,
        Action<SeatGridCloudPull> onPicked,
        IReadOnlyList<SeatGridRosterStudent>? students = null,
        SeatGridRosterFields? fields = null,
        string? suggestedName = null,
        Action<SeatGridRosterEntry>? onRosterSaved = null,
        Action<SeatGridRosterPick>? onRosterPicked = null,
        Func<SeatGridCloudDocument?>? buildDocument = null,
        Action<string>? onDocumentDeleted = null,
        string? linkedRosterId = null,
        Action<string>? onRosterDeleted = null)
        => new SeatGridCloudWindow(
            cloud, SeatGridCloudTab.Documents, students, fields ?? SeatGridRosterFields.None,
            suggestedName ?? string.Empty, document, buildDocument, existingId, expectedRevision,
            onRosterSaved, onRosterPicked, onSaved, onPicked, onDocumentDeleted,
            linkedRosterId, onRosterDeleted).Show(owner);

    // ------------------------------ 界面 ------------------------------

    private Control BuildRoot()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var header = new Border { Classes = { "dialog-header" } };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var headerPanel = new StackPanel { Spacing = 3 };
        headerPanel.Children.Add(new TextBlock { Classes = { "section-title" }, Text = "云端存档", FontSize = 18 });
        _headerStatus = new TextBlock
        {
            Classes = { "helper" },
            Text = _cloud.StatusText,
        };
        headerPanel.Children.Add(_headerStatus);
        _headerAccountLink = new Button
        {
            Classes = { "link" },
            Content = "去账号设置",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 2),
            IsVisible = !_cloud.IsAvailable,
        };
        _headerAccountLink.Click += (_, _) => _cloud.OpenAccountSettings(this);
        headerPanel.Children.Add(_headerAccountLink);
        Grid.SetColumn(headerPanel, 0);
        headerGrid.Children.Add(headerPanel);
        var eyebrow = new TextBlock
        {
            Classes = { "eyebrow" }, Text = "SECTL CLOUD", VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(eyebrow, 1);
        headerGrid.Children.Add(eyebrow);
        header.Child = headerGrid;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _rosterTabButton = new Button { Classes = { "segment" }, Content = "共享名单" };
        _documentTabButton = new Button { Classes = { "segment" }, Content = "座位表" };
        _rosterTabButton.Click += (_, _) => SelectTab(0);
        _documentTabButton.Click += (_, _) => SelectTab(1);
        var tabGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(_rosterTabButton, 0);
        Grid.SetColumn(_documentTabButton, 1);
        tabGrid.Children.Add(_rosterTabButton);
        tabGrid.Children.Add(_documentTabButton);
        var tabBar = new Border
        {
            Classes = { "segmented" },
            Margin = new Thickness(Pad, 12, Pad, 0),
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = tabGrid,
        };
        Grid.SetRow(tabBar, 0);
        body.Children.Add(tabBar);

        _rosterTabContent = BuildRosterTab();
        _documentTabContent = BuildDocumentTab();
        var tabHost = new Panel { Margin = new Thickness(Pad, 4, Pad, 0) };
        tabHost.Children.Add(_rosterTabContent);
        tabHost.Children.Add(_documentTabContent);
        Grid.SetRow(tabHost, 1);
        body.Children.Add(tabHost);
        SelectTab(_selectedTabIndex);

        var status = new StackPanel { Spacing = 6, Margin = new Thickness(Pad, 4, Pad, 4) };
        status.Children.Add(_busy);
        status.Children.Add(_notice);
        var errorStrip = new Border
        {
            Background = Wash, BorderBrush = Accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = _error,
        };
        errorStrip.Bind(Visual.IsVisibleProperty, new Binding("IsVisible") { Source = _error });
        status.Children.Add(errorStrip);
        status.Children.Add(_retry);
        Grid.SetRow(status, 2);
        body.Children.Add(status);

        var footer = new Border { Classes = { "dialog-footer" } };
        footer.Child = new TextBlock
        {
            Classes = { "helper" },
            Text = "只在点击时联网；共享名单默认只含姓名、性别、学号，其他字段由你勾选。",
        };
        Grid.SetRow(footer, 3);
        body.Children.Add(footer);

        return root;
    }

    private void SelectTab(int index)
    {
        _selectedTabIndex = index == 0 ? 0 : 1;
        if (_rosterTabButton is not null)
            _rosterTabButton.Classes.Set("active", _selectedTabIndex == 0);
        if (_documentTabButton is not null)
            _documentTabButton.Classes.Set("active", _selectedTabIndex == 1);
        if (_rosterTabContent is not null)
            _rosterTabContent.IsVisible = _selectedTabIndex == 0;
        if (_documentTabContent is not null)
            _documentTabContent.IsVisible = _selectedTabIndex == 1;
    }

    private Control BuildRosterTab() => BuildTab(
        new[] { _rosterUpload, _rosterPull, _rosterDelete }, _rosterList, _rosterEmpty,
        "选中一份名单后点「从云端拉取」即可替换当前名单；上传会覆盖所选名单。");

    private Control BuildDocumentTab() => BuildTab(
        new[] { _documentUpload, _documentSaveAsNew, _documentPull, _documentDelete }, _documentList, _documentEmpty,
        "座位表关联一份共享名单，并内嵌一份名单快照作为兜底；拉取时会按序号 + 姓名重排座位与约束。");

    private static Control BuildTab(
        IEnumerable<Button> actionButtons, ListBox list, Control empty, string hintText)
    {
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 8),
        };
        foreach (var button in actionButtons)
            actions.Children.Add(button);
        Grid.SetRow(actions, 0);
        panel.Children.Add(actions);

        var listFrame = new Border { Classes = { "workspace-panel" }, Padding = new Thickness(8, 8, 8, 4) };
        var innerPanel = new Panel();
        innerPanel.Children.Add(list);
        innerPanel.Children.Add(empty);
        listFrame.Child = innerPanel;
        Grid.SetRow(listFrame, 1);
        panel.Children.Add(listFrame);

        var hint = Hint(hintText);
        hint.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(hint, 2);
        panel.Children.Add(hint);
        return panel;
    }

    private static Border Hint(string text) => new()
    {
        Background = Wash, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
        Child = new TextBlock
        {
            Text = text, FontSize = 10, Foreground = Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 16,
        },
    };

    private static Button ActionButton(string text, bool primary) => new()
    {
        Classes = { primary ? "primary" : "ghost", "top" },
        Content = text,
        Padding = new Thickness(13, 6),
    };

    private static Control BuildEmptyState(
        string title, string explanation, string actionText, EventHandler<RoutedEventArgs> onAction)
    {
        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 340,
        };
        var glyph = new Border
        {
            Width = 46, Height = 46, BorderBrush = Accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Classes = { "eyebrow" },
                Text = "+", FontSize = 22, Foreground = Accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        stack.Children.Add(glyph);
        stack.Children.Add(new TextBlock
        {
            Classes = { "section-title" },
            Text = title, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Classes = { "helper" },
            Text = explanation,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        });
        var action = ActionButton(actionText, primary: true);
        action.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(action, actionText);
        action.Click += onAction;
        stack.Children.Add(action);
        return stack;
    }

    private static Control BuildRow(string text)
    {
        var parts = text.Split('\n', 2);
        var head = parts[0];
        var fields = parts.Length > 1 ? parts[1] : string.Empty;
        var split = head.IndexOf("    ", StringComparison.Ordinal);
        var name = split >= 0 ? head[..split] : head;
        var detail = split >= 0 ? head[(split + 4)..] : string.Empty;
        var meta = fields.Length > 0 ? $"{detail}  ·  {fields}" : detail;

        var row = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9), Margin = new Thickness(0, 0, 0, 4),
        };
        row.Bind(Border.BackgroundProperty, SelectedBinding(RowBrushConverter.Background));
        row.Bind(Border.BorderBrushProperty, SelectedBinding(RowBrushConverter.Border));
        row.Child = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ink },
                new TextBlock
                {
                    Classes = { "eyebrow" },
                    Text = meta, FontSize = 9.5, LetterSpacing = 0.2, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        return row;
    }

    private static Binding SelectedBinding(IValueConverter converter) => new("IsSelected")
    {
        RelativeSource = new RelativeSource
        {
            Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBoxItem), AncestorLevel = 1,
        },
        Converter = converter,
    };

    private void OnRosterUploadClick(object? sender, RoutedEventArgs e) => _ = UploadRosterAsync();

    private void OnDocumentUploadClick(object? sender, RoutedEventArgs e) => _ = UploadDocumentAsync();

    /// <summary>缺少可上传座位表时的提示（纯函数，便于测试与复用）。</summary>
    public static string DescribeMissingDocumentMessage()
        => "请先到「共享名单」页发布或拉取一份名单，再上传座位表。可点上方「共享名单」页签切换。";

    /// <summary>缺少可上传名单时的提示（纯函数，便于测试与复用）。</summary>
    public static string DescribeMissingRosterMessage()
        => "当前没有可上传的名单。请先在归位主窗口导入名单，再回到这里上传。";

    /// <summary>
    /// 上传名单的预填判定（纯函数）。覆盖目标永远是列表选中行：选中行就是当前关联名单时沿用关联侧字段与名称，
    /// 选中了别的名单时用那一行的字段与名称；没有选中行（新建）时用记住的上次字段。
    /// </summary>
    public static (SeatGridRosterFields Fields, string Name) ResolveUploadPrefill(
        SeatGridRosterEntry? selected,
        string? linkedRosterId,
        SeatGridRosterFields linkedFields,
        SeatGridRosterFields lastFields,
        string suggestedName)
    {
        if (selected is null) return (lastFields, suggestedName);
        return string.Equals(selected.RosterId, linkedRosterId, StringComparison.Ordinal)
            ? (linkedFields, suggestedName)
            : (selected.Fields, selected.Name);
    }

    /// <summary>删除的名单是否正是当前关联名单（纯函数，供窗口与宿主共用同一判定）。</summary>
    public static bool ShouldClearRosterLink(string deletedRosterId, string? linkedRosterId)
        => !string.IsNullOrEmpty(linkedRosterId)
           && string.Equals(deletedRosterId, linkedRosterId, StringComparison.Ordinal);

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private void ShowNotice(string message)
    {
        _notice.Text = message;
        _notice.IsVisible = true;
    }

    private sealed class RowBrushConverter : IValueConverter
    {
        public static readonly RowBrushConverter Background = new(true);
        public static readonly RowBrushConverter Border = new(false);
        private readonly bool _isBackground;
        private RowBrushConverter(bool isBackground) => _isBackground = isBackground;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var selected = value is true;
            return _isBackground ? (selected ? AccentWash : Card) : (selected ? Accent : Line);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // ------------------------------ 数据 ------------------------------

    private async Task RefreshAsync()
    {
        _lastOperation = RefreshAsync;
        await RunAsync(async ct =>
        {
            var rosters = await _cloud.ListRostersAsync(ct);
            var documents = await _cloud.ListDocumentsAsync(ct);
            _rosters = rosters;
            _documents = documents;
            _rosterList.ItemsSource = rosters.Select(Describe).ToList();
            _documentList.ItemsSource = documents.Select(Describe).ToList();
            _rosterEmpty.IsVisible = rosters.Count == 0;
            _documentEmpty.IsVisible = documents.Count == 0;
            UpdateButtons();
            return true;
        }, "正在读取云端列表…");
    }

    private static string Describe(SeatGridRosterEntry e)
    {
        var fields = new List<string> { "姓名", "性别", "学号" };
        if (e.Fields.Height) fields.Add("身高");
        if (e.Fields.Vision) fields.Add("视力");
        if (e.Fields.Score) fields.Add("成绩");
        if (e.Fields.Notes) fields.Add("备注");
        if (e.Fields.OriginalGroup) fields.Add("原小组");
        return $"{e.Name}    v{e.Version} · {e.StudentCount} 人 · {e.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n{string.Join("、", fields)}";
    }

    private static string Describe(SeatGridCloudEntry e)
        => $"{e.Name}    v{e.Revision} · {e.Bytes / 1024.0:0.0} KB · {e.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    private void UpdateButtons()
    {
        var roster = _rosterList.SelectedIndex >= 0 && _rosterList.SelectedIndex < _rosters.Count
            ? _rosters[_rosterList.SelectedIndex]
            : null;
        var document = _documentList.SelectedIndex >= 0 && _documentList.SelectedIndex < _documents.Count
            ? _documents[_documentList.SelectedIndex]
            : null;

        _rosterUpload.IsEnabled = _students is not null && _students.Count > 0;
        _rosterPull.IsEnabled = roster is not null;
        _rosterDelete.IsEnabled = roster is not null;
        _documentUpload.IsEnabled = _document is not null;
        var hasExistingDoc = !string.IsNullOrEmpty(_existingId);
        _documentSaveAsNew.IsVisible = hasExistingDoc;
        _documentSaveAsNew.IsEnabled = _document is not null && hasExistingDoc;
        _documentPull.IsEnabled = document is not null;
        _documentDelete.IsEnabled = document is not null;
    }

    private async Task UploadRosterAsync()
    {
        if (_students is null || _students.Count == 0)
        {
            ShowError(DescribeMissingRosterMessage());
            return;
        }
        var selected = SelectedRoster();
        var (initialFields, initialName) = ResolveUploadPrefill(
            selected, _linkedRosterId, _fields, _cloud.LastRosterFields, _suggestedName);
        var picked = await RosterFieldSelectionWindow.ShowAsync(this, _cloud, initialName, initialFields);
        if (picked is null) return;

        var (name, fields) = picked.Value;
        await ExecuteUploadRosterAsync(name, fields, selected);
    }

    private async Task ExecuteUploadRosterAsync(
        string name, SeatGridRosterFields fields, SeatGridRosterEntry? existing)
    {
        _lastOperation = () => ExecuteUploadRosterAsync(name, fields, existing);
        var entry = await RunAsync(
            ct => _cloud.UploadRosterAsync(
                name, _students!, fields, existing?.RosterId, existing?.Version ?? 0, ct),
            "正在上传共享名单…");
        if (entry is null) return;

        _cloud.RememberRosterFields(fields);
        _onRosterSaved?.Invoke(entry);
        UpdateButtons();
        Close();
    }

    private async Task PullRosterAsync()
    {
        var selected = SelectedRoster();
        if (selected is null) return;
        await ExecutePullRosterAsync(selected);
    }

    private async Task ExecutePullRosterAsync(SeatGridRosterEntry selected)
    {
        _lastOperation = () => ExecutePullRosterAsync(selected);
        var pick = await RunAsync(ct => _cloud.DownloadRosterAsync(selected.RosterId, ct), "正在拉取共享名单…");
        if (pick is null) return;

        _onRosterPicked?.Invoke(pick);
        Close();
    }

    private async Task DeleteRosterAsync()
    {
        var selected = SelectedRoster();
        if (selected is null) return;
        if (!await ConfirmDialog.ShowAsync(
                this, "删除共享名单？", $"「{selected.Name}」将从云端删除，已拉取到本地的名单不受影响。",
                "删除", "取消", danger: true))
            return;

        await ExecuteDeleteRosterAsync(selected);
    }

    private async Task ExecuteDeleteRosterAsync(SeatGridRosterEntry selected)
    {
        _lastOperation = () => ExecuteDeleteRosterAsync(selected);
        var ok = await RunAsync(async ct =>
        {
            await _cloud.DeleteRosterAsync(selected.RosterId, ct);
            return true;
        }, "正在删除共享名单…");
        if (ok != true) return;
        if (ShouldClearRosterLink(selected.RosterId, _linkedRosterId))
        {
            _onRosterDeleted?.Invoke(selected.RosterId);
            ShowNotice(RosterDeletedLinkClearedMessage);
        }
        await RefreshAsync();
    }

    private async Task UploadDocumentAsync(bool saveAsNew = false)
    {
        var document = _document ?? _buildDocument?.Invoke();
        if (document is null)
        {
            ShowError(DescribeMissingDocumentMessage());
            return;
        }
        _document = document;
        await ExecuteUploadDocumentAsync(document, saveAsNew);
    }

    private async Task ExecuteUploadDocumentAsync(SeatGridCloudDocument document, bool saveAsNew)
    {
        _lastOperation = () => ExecuteUploadDocumentAsync(document, saveAsNew);
        var replacingId = saveAsNew ? null : _existingId;
        var expectedRevision = saveAsNew ? 0 : _expectedRevision;
        var result = await RunAsync(
            ct => _cloud.UploadDocumentAsync(document, replacingId, expectedRevision, ct),
            saveAsNew ? "正在另存座位表副本…" : "正在上传座位表…");
        if (result is null) return;

        _existingId = result.Entry.Id;
        _expectedRevision = result.Entry.Revision;
        _onDocumentSaved?.Invoke(result);
        Close();
    }

    private async Task PullDocumentAsync()
    {
        var selected = SelectedDocument();
        if (selected is null) return;
        await ExecutePullDocumentAsync(selected);
    }

    private async Task ExecutePullDocumentAsync(SeatGridCloudEntry selected)
    {
        _lastOperation = () => ExecutePullDocumentAsync(selected);
        var pull = await RunAsync(ct => _cloud.DownloadDocumentAsync(selected.Id, ct), "正在拉取座位表…");
        if (pull is null) return;

        _onDocumentPicked?.Invoke(pull);
        Close();
    }

    private async Task DeleteDocumentAsync()
    {
        var selected = SelectedDocument();
        if (selected is null) return;
        if (!await ConfirmDialog.ShowAsync(
                this, "删除云端座位表？", $"「{selected.Name}」将从云端删除，本机当前座位表不受影响。",
                "删除", "取消", danger: true))
            return;

        await ExecuteDeleteDocumentAsync(selected);
    }

    private async Task ExecuteDeleteDocumentAsync(SeatGridCloudEntry selected)
    {
        _lastOperation = () => ExecuteDeleteDocumentAsync(selected);
        var ok = await RunAsync(async ct =>
        {
            await _cloud.DeleteDocumentAsync(selected.Id, ct);
            return true;
        }, "正在删除座位表…");
        if (ok != true) return;

        if (string.Equals(selected.Id, _existingId, StringComparison.Ordinal))
        {
            _existingId = null;
            _expectedRevision = 0;
            UpdateButtons();
        }
        _onDocumentDeleted?.Invoke(selected.Id);
        await RefreshAsync();
    }

    private SeatGridRosterEntry? SelectedRoster()
        => _rosterList.SelectedIndex >= 0 && _rosterList.SelectedIndex < _rosters.Count
            ? _rosters[_rosterList.SelectedIndex]
            : null;

    private SeatGridCloudEntry? SelectedDocument()
        => _documentList.SelectedIndex >= 0 && _documentList.SelectedIndex < _documents.Count
            ? _documents[_documentList.SelectedIndex]
            : null;

    private async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T>> action, string busy)
    {
        _opsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _opsCts = cts;
        var ct = cts.Token;

        _busy.Text = busy;
        _busy.IsVisible = true;
        _notice.IsVisible = false;
        _error.IsVisible = false;
        _retry.IsVisible = false;
        SetActionsEnabled(false);

        try
        {
            return await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _error.Text = ErrorText.Describe(ex, busy.TrimStart('正', '在').TrimEnd('…'));
                _error.IsVisible = true;
                _retry.IsVisible = true;
            }
            return default;
        }
        finally
        {
            if (ReferenceEquals(_opsCts, cts)) _opsCts = null;
            cts.Dispose();
            if (!_closed)
            {
                _busy.IsVisible = false;
                SetActionsEnabled(true);
            }
        }
    }

    private void OnCloudStateChanged(object? sender, EventArgs e)
    {
        void RefreshHeader()
        {
            if (_closed) return;
            if (_headerStatus is not null) _headerStatus.Text = _cloud.StatusText;
            if (_headerAccountLink is not null) _headerAccountLink.IsVisible = !_cloud.IsAvailable;
        }

        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshHeader);
            return;
        }
        RefreshHeader();
    }

    private void SetActionsEnabled(bool enabled)
    {
        UpdateButtons();
        if (enabled) return;

        _rosterUpload.IsEnabled = false;
        _rosterPull.IsEnabled = false;
        _rosterDelete.IsEnabled = false;
        _documentUpload.IsEnabled = false;
        _documentSaveAsNew.IsEnabled = false;
        _documentPull.IsEnabled = false;
        _documentDelete.IsEnabled = false;
    }
}
