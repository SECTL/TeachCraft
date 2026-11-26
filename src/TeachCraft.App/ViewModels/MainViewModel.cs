using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using TeachCraft.App.Cloud;
using TeachCraft.App.ControlCenter;
using TeachCraft.Core.Control;
using TeachCraft.Core.Oobe;
using TeachCraft.Core.Tools;
using TeachCraft.Core.Updating;

namespace TeachCraft.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // 静态画刷会被多个 ViewModel 实例与并行测试线程共享，必须是不可变、
    // 创建即完成的实例；SolidColorBrush 的 Color 在并发读取下可能读到未就绪状态，
    // 曾导致「强调色 == 错误色」的偶发断言失败。改用 ImmutableSolidColorBrush。
    private static readonly IBrush Moss = new ImmutableSolidColorBrush(Color.Parse("#5A7A4B"));
    private static readonly IBrush Seal = new ImmutableSolidColorBrush(Color.Parse("#C6613F"));

    /// <summary>
    /// 「有新版本」用的强调色。刻意不用 AccentDeepBrush ——
    /// 它与 SealBrush 同为 #A34A36，会让「有新版本」看起来像报错。
    /// </summary>
    private static readonly IBrush UpdateAccent = new ImmutableSolidColorBrush(Color.Parse("#D97757"));

private readonly List<ToolItemViewModel> _imageTools;
private readonly List<ToolItemViewModel> _fileTools;
private readonly List<ToolItemViewModel> _teachTools;
    private readonly BuildIdentity _identity;
    private readonly IUpdateCheckService? _checkService;
    private readonly InstallKind _installKind;
    private readonly Func<UpdateSource, IUpdateCheckService> _checkServiceFactory;

    /// <summary>
    /// 用户选定的更新源。GitHub 为默认；选镜像时不再自动回退——
    /// 他明确要的是镜像，替他回退反而违背选择。
    /// </summary>
    private UpdateSource _updateSource;

    /// <summary>
    /// 用户选定的更新通道。没手动选过时跟随安装形态：
    /// 正式版用户看正式版，预览版用户继续看预览——那是他们当初的选择。
    /// </summary>
    private UpdateChannel _updateChannel;

    /// <summary>
    /// 界面版本号取自可自证的构建身份，必须包含预览序号，
    /// 否则 v1.17.0-preview.141 与 -preview.142 在界面上完全同形。
    /// AssemblyVersion 仍是三段，保持不变。
    /// </summary>
    public string VersionText { get; }

    public string VersionLine => $"{VersionText} · {ToolDescriptionCatalog.ToolIds.Count} TOOLS · {ToolDescriptionCatalog.AppKeys.Count} APPS";

    /// <summary>首次引导的遮罩状态。挂在 MainViewModel 上，因为 MainWindow 的绑定都解析到这里。</summary>
    public OobeViewModel Oobe { get; }

    public ObservableCollection<ToolItemViewModel> VisibleImageTools { get; } = new();
    public ObservableCollection<ToolItemViewModel> VisibleFileTools { get; } = new();
    public ObservableCollection<ToolItemViewModel> VisibleTeachTools { get; } = new();

    /// <summary>
    /// 案头常备卡：状态（展开与否）与检索文本。顺序固定为分晓 / 归位 / 时序 / 织序 / 竞名 / 辑册，
    /// 说明文案来自 ToolDescriptionCatalog，避免同一段话在 XAML 与视图模型里各写一份。
    /// 必须是实例字段：卡片带展开状态，做成 static 会让多个视图模型共享同一份，
    /// 测试里第二个实例一创建就继承上一次的展开状态。
    /// </summary>
    private readonly AppCardViewModel[] _appCards;

    private int _visibleAppCardCount;

    private static AppCardViewModel[] CreateAppCards() => new AppCardViewModel[]
    {
        new(ToolDescriptionCatalog.GradeStripKey, "分晓",
            ToolDescriptionCatalog.ForApp(ToolDescriptionCatalog.GradeStripKey)!),
        new(ToolDescriptionCatalog.SeatGridKey, "归位",
            ToolDescriptionCatalog.ForApp(ToolDescriptionCatalog.SeatGridKey)!),
        new(ToolDescriptionCatalog.ClassBlockKey, "时序",
            ToolDescriptionCatalog.ForApp(ToolDescriptionCatalog.ClassBlockKey)!),
        new(ToolDescriptionCatalog.GradeWeaveKey, "织序",
            ToolDescriptionCatalog.ForApp(ToolDescriptionCatalog.GradeWeaveKey)!),
        new(ToolDescriptionCatalog.MeetRosterKey, "竞名",
            ToolDescriptionCatalog.ForApp(ToolDescriptionCatalog.MeetRosterKey)!),
        new(ToolDescriptionCatalog.HomeworkKey, "辑册",
            ToolDescriptionCatalog.ForApp(ToolDescriptionCatalog.HomeworkKey)!, "WorkFolio"),
    };

    public AppCardViewModel GradeStripCard => _appCards[0];
    public AppCardViewModel SeatGridCard => _appCards[1];
    public AppCardViewModel ClassBlockCard => _appCards[2];
    public AppCardViewModel GradeWeaveCard => _appCards[3];
    public AppCardViewModel MeetRosterCard => _appCards[4];
    public AppCardViewModel HomeworkCard => _appCards[5];

    public bool IsGradeStripVisible { get; private set; } = true;
    public bool IsSeatGridVisible { get; private set; } = true;
    public bool IsClassBlockVisible { get; private set; } = true;
    public bool IsGradeWeaveVisible { get; private set; } = true;
    public bool IsMeetRosterVisible { get; private set; } = true;
    public bool IsHomeworkVisible { get; private set; } = true;
    public bool HasPinnedResults { get; private set; } = true;

    public MainViewModel()
        : this(null, null)
    {
    }

    /// <summary>
    /// 可注入构造。两个参数都可省略，因此现有 9 处 <c>new MainViewModel()</c> 调用无需改动。
    /// identity 为 null 时只从本程序集读身份——绝不能从测试程序集读，
    /// 测试程序集没有版本信息，会退化成 0.0.0-preview.0。
    /// </summary>
    public MainViewModel(
        IUpdateCheckService? checkService = null,
        BuildIdentity? identity = null,
        InstallInfo? installInfo = null,
        IUpdateStateStore? stateStore = null,
        Func<UpdateDownloader>? downloaderFactory = null,
        Func<UpdateSource, IUpdateCheckService>? checkServiceFactory = null,
        IOobeStateStore? oobeStateStore = null,
        bool? showOobe = null,
        CloudRuntime? cloudRuntime = null,
        IPendingChangelogStore? pendingChangelogStore = null,
        ControlRuntime? controlRuntime = null)
    {
        _identity = identity ?? BuildIdentity.Current(typeof(MainViewModel));
        _checkService = checkService;
        _stateStore = stateStore ?? new UpdateStateStore();
        _pendingChangelogStore = pendingChangelogStore ?? new NoopPendingChangelogStore();
        _downloaderFactory = downloaderFactory ?? (() => new UpdateDownloader(SharedHandlers.Default));
        _checkServiceFactory = checkServiceFactory ?? CreateDefaultCheckService;

        // 安装形态必须来自真实探测：注册表身份记录 → 路径推断。
        // 写死成 Unknown 会让「自动分配安装包」永远走不到，用户只会看到「暂未提供」。
        var info = installInfo ?? InstallKindDetector.Current(InstallRecordReader.ReadAll());
        _installKind = info.Kind;

        // 探测出的部署模式 / 运行标识补进身份——legacy 构建自带的信息不完整。
        if (info.Mode is not null) _identity = _identity.WithMode(info.Mode);
        if (_identity.Rid is null && info.Rid is not null) _identity = _identity.WithRid(info.Rid);

        VersionText = $"V{_identity.Describe()}";

        // 引导状态：默认走真实存储；测试与走查通过参数注入替身，
        // 否则每个用例都会去读写开发者本机的 %LOCALAPPDATA%。
        Oobe = new OobeViewModel(oobeStateStore ?? new OobeStateStore());

        // 账号页默认拿到 Disabled：空存储、无服务，构造期不碰账号目录也不联网。
        Cloud = cloudRuntime ?? CloudRuntime.Disabled;
        // 只有装配出真实账号服务时才给账号页接登录编排；Disabled 的 Account 为 null，
        // 这里保持 signIn 为 null，构造期不建浏览器、不绑回环端口、不发请求。
        // 三个部件都只存引用，new 本身没有任何 IO——走查路径因此仍然安全。
        var signIn = Cloud.Account is { } account
            ? new DesktopAccountSignInFlow(account, new ShellBrowserLauncher(), new LoopbackOAuthReceiver())
            : null;
        Account = new AccountViewModel(Cloud, signIn);

        Control = controlRuntime ?? ControlRuntime.Disabled;
        ControlCenter = new ControlViewModel(Control, Cloud);
        if (Cloud.Account is { } acct)
            acct.StateChanged += (_, _) => ControlCenter.Refresh();

        // showOobe：null = 按状态自行判断（默认）；true = 强制打开；false = 强制不打开。
        // 判定本身是只读的——不管打开与否都算一遍，这样 --oobe-metrics 能如实反映「该不该弹」。
        var decision = OobePlanner.Decide(oobeStateStore?.Load() ?? OobeState.Empty);
        var shouldShow = showOobe ?? decision.Show;
        if (showOobe == false) shouldShow = false;


        if (shouldShow) Oobe.OpenAt(decision.StartAt);

        // 上次手动选过的更新源要恢复，否则每次重启都回到默认，用户的选择形同虚设。
        _updateSource = _stateStore.Load().PreferredSource ?? UpdateSource.GitHub;
        _updateChannel = _stateStore.Load().PreferredChannel
                         ?? UpdateChannelText.DefaultFor(_identity.Version);

        _appCards = CreateAppCards();
        _visibleAppCardCount = _appCards.Length;

        _imageTools = new List<ToolItemViewModel>
        {
            new(ToolKind.ImageConvert, "01", "image-convert", "图片格式转换",
                "PNG、JPG、WebP 格式之间自由互转。", "image", "图",
                "PNG / JPG / WebP", "转换后的 PNG / JPG / WebP 文件", new[] { "PNG", "JPG", "WebP" }),
            new(ToolKind.ImageResize, "02", "image-resize", "图片尺寸调整",
                "自定义像素、分辨率或等比例缩放。", "image", "图",
                "任意图片", "指定宽高或缩放比例"),
            new(ToolKind.ImageCompress, "03", "image-compress", "图片压缩",
                "调节画质与体积，减小图片占用空间。", "image", "图",
                "PNG / JPG / WebP", "压缩后的同格式图片"),
            new(ToolKind.ImageWatermark, "04", "image-watermark", "图片加水印",
                "添加自定义文字水印，保护版式版权。", "image", "图",
                "任意图片", "带文字水印的图片"),
        };

        _fileTools = new List<ToolItemViewModel>
        {
            new(ToolKind.PdfToWord, "05", "pdf-word", "PDF 转 Word",
                "提取可编辑文字，或保留页面版式转换为 DOCX。", "file", "档",
                "PDF 文档", "DOCX 文档"),
            new(ToolKind.WordToPdf, "06", "word-pdf", "Word 转 PDF",
                "保留标题、段落与表格，排成 A4 PDF（图片暂不参与）。", "file", "档",
                "DOCX 文档", "PDF 文件"),
            new(ToolKind.ImagesToPdf, "07", "images-pdf", "图片合成 PDF",
                "多张图片或照片按顺序合并生成一份 PDF。", "file", "档",
                "多张图片", "单份 PDF 文档"),
            new(ToolKind.PdfToImages, "08", "pdf-images", "PDF 转图片",
                "逐页导出高清 PNG 图片并打包下载。", "file", "档",
                "PDF 文档", "PNG 图片文件夹"),
            new(ToolKind.PdfMerge, "09", "pdf-merge", "PDF 合并",
                "多份 PDF 文档按顺序拼接合为一份。", "file", "档",
                "多份 PDF", "合并后的 PDF"),
            new(ToolKind.PdfSplit, "10", "pdf-split", "PDF 拆分提取",
                "选取指定页码范围，生成新的 PDF 文件。", "file", "档",
                "PDF 文档", "指定页范围的新 PDF"),
        };

        _teachTools = new List<ToolItemViewModel>
        {
            new(ToolKind.RosterCompare, "11", "roster-compare", "名单核对",
                "核对应交与已交名单，列出未交、重复和名单外姓名。", "teach", "教",
                "两份 TXT / XLSX / XLS 名单", "UTF-8 文本核对报告"),
            new(ToolKind.ExcelClassSplit, "12", "excel-class-split", "Excel按班拆表",
                "将一份 Excel 总表按班级分别导出。", "teach", "教",
                "一份 XLSX / XLS 工作簿", "每班一份 XLSX 文件"),
            new(ToolKind.ExamPaperLayout, "13", "exam-paper-layout", "试卷排版",
                "把 PDF 试卷排成 2 合 1 或小册子版式。", "teach", "教",
                "PDF 试卷", "排版后的 PDF"),
            new(ToolKind.CertificateBatch, "14", "certificate-batch", "奖状生成",
                "用奖状底图和获奖名单批量生成 PDF，一人一页。", "teach", "教",
                "奖状底图 + XLSX / XLS 名单", "批量奖状 PDF"),
        };

        ApplyFilter();
    }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ToolItemViewModel? _selectedTool;
    [ObservableProperty] private bool _isToolPanelOpen;
    [ObservableProperty] private bool _isProjectPanelOpen;

    /// <summary>更新面板是否展开。与工具／项目面板互斥，共用同一个抽屉。</summary>
    [ObservableProperty] private bool _isUpdatePanelOpen;

    /// <summary>账号面板是否展开。与其余面板互斥，共用同一个抽屉。</summary>
    [ObservableProperty] private bool _isAccountPanelOpen;

    /// <summary>集控面板是否展开。与其余面板互斥，共用同一个抽屉。</summary>
    [ObservableProperty] private bool _isControlPanelOpen;

    /// <summary>本进程唯一的云端装配结果。宿主窗口据此给各应用构造只读桥。</summary>
    public CloudRuntime Cloud { get; }

    /// <summary>账号页状态机。由构造时注入的 CloudRuntime 决定是真实能力还是未接入。</summary>
    public AccountViewModel Account { get; }

    /// <summary>本进程唯一的集控装配结果。</summary>
    public ControlRuntime Control { get; }

    /// <summary>集控中心面板状态机。</summary>
    public ControlViewModel ControlCenter { get; }

    /// <summary>更新面板里显示的当前构建身份（含预览序号与部署形态）。</summary>
    [ObservableProperty] private string _installDescription = "";

    /// <summary>更新面板的主状态行：未检查 / 检查中 / 已最新 / 有新版 / 失败原因。</summary>
    [ObservableProperty] private string _updateStateText = "还没有检查过";

    /// <summary>检查/下载进行中——用于禁用按钮、显示进度。</summary>
    [ObservableProperty] private bool _isUpdateBusy;

    /// <summary>下载进度百分比；仅在下载阶段有意义。</summary>
    [ObservableProperty] private int _updateProgress;

    /// <summary>
    /// 下载详细情况：「12.4 MB / 58.2 MB · 1.4 MB/s · 剩余约 32 秒」。
    /// 与百分比分开：百分比驱动进度条，这一行回答「还要等多久」。
    /// </summary>
    [ObservableProperty] private string _updateProgressDetail = string.Empty;

    /// <summary>进度条下方那行速度文本是否该出现（没在下载就藏起来，不留空行）。</summary>
    public bool HasUpdateSpeed => !string.IsNullOrEmpty(UpdateProgressDetail);

    partial void OnUpdateProgressDetailChanged(string value) => OnPropertyChanged(nameof(HasUpdateSpeed));

    /// <summary>上次检查时间，取自本机状态文件。</summary>
    [ObservableProperty] private string _lastCheckedText = "从未检查";

    /// <summary>当前选定的更新源；界面上的两个单选按钮绑它。</summary>
    [ObservableProperty] private UpdateSource _selectedSource = UpdateSource.GitHub;

    /// <summary>上次检查实际用了哪个源（回退后与所选不同），用于「已改用镜像」提示。</summary>
    [ObservableProperty] private string _sourceNoteText = "";

    /// <summary>是否显示「GitHub 连不上，已自动改用镜像」这类提示。</summary>
    [ObservableProperty] private bool _hasSourceNote;

    /// <summary>GitHub 单选按钮状态（Avalonia 单选组绑 bool 比绑枚举直接）。</summary>
    public bool IsGitHubSource
    {
        get => SelectedSource == UpdateSource.GitHub;
        set { if (value) ChangeSource(UpdateSource.GitHub); }
    }

    /// <summary>SECTL 镜像单选按钮状态。</summary>
    public bool IsMirrorSource
    {
        get => SelectedSource == UpdateSource.SectlMirror;
        set { if (value) ChangeSource(UpdateSource.SectlMirror); }
    }

    /// <summary>当前选定的更新通道；界面上的两个单选按钮绑它。</summary>
    [ObservableProperty] private UpdateChannel _selectedChannel = UpdateChannel.Stable;

    /// <summary>稳定通道单选按钮状态。</summary>
    public bool IsStableChannel
    {
        get => SelectedChannel == UpdateChannel.Stable;
        set { if (value) ChangeChannel(UpdateChannel.Stable); }
    }

    /// <summary>预览通道单选按钮状态。</summary>
    public bool IsPreviewChannel
    {
        get => SelectedChannel == UpdateChannel.Preview;
        set { if (value) ChangeChannel(UpdateChannel.Preview); }
    }
    [ObservableProperty] private string _statusText = DefaultStatusText;
    [ObservableProperty] private IBrush _statusBrush = Moss;

    /// <summary>状态栏默认/复位文案。默认文案与复位文案必须是同一个常量，否则用完面板后旧口径会复活。</summary>
    public const string DefaultStatusText = "本机处理 · 联网仅限更新、账号与集控";

    /// <summary>是否有可用新版本。与 StatusText 分开，提示条是独立的一块视觉。</summary>
    [ObservableProperty] private bool _isUpdateAvailable;

    /// <summary>「新版本 1.17.0-preview.142」；没有新版时为空串。</summary>
    [ObservableProperty] private string _updateBadgeText = "";

    /// <summary>
    /// 更新提示的解释文案（挂在徽章的工具提示上）。
    ///
    /// 两件事必须对用户说清楚，否则就是在粉饰：
    ///   1. 旧预览版只能升到下一个正式版——它们的版本号本来就比正式版低，
    ///      「预览直接跳到下一个预览」要等正式版发布之后才成立。
    ///   2. 校验清单与安装包来自同一次未签名发布，它只能证明「下载没坏」，
    ///      不能证明「发布者就是他」。本项目没有签名信任根。
    /// </summary>
    /// 必须是 [ObservableProperty]：工具提示绑定了它，
    /// 普通自动属性不发出 PropertyChanged，界面会永远显示绑定那一刻的空串。
    [ObservableProperty] private string _updateExplanation = "";

    /// <summary>提示条使用的强调色（刻意不等于报错色）。</summary>
    public IBrush WarningBrush => UpdateAccent;

    /// <summary>当前可下载的更新提案；没有时为 null。</summary>
    public UpdateProposal? PendingUpdate { get; private set; }

    /// <summary>最近一次点「跳过这版」记下的版本号；从未跳过时为 null（审计 P3-35 的反馈话术用）。</summary>
    public string? LastSkippedVersion { get; private set; }
    [ObservableProperty] private bool _isAllActive = true;
    [ObservableProperty] private bool _isImageActive;
    [ObservableProperty] private bool _isFileActive;
    [ObservableProperty] private bool _isTeachActive;
    [ObservableProperty] private bool _isProjectActive;

    public bool IsPanelOpen => IsToolPanelOpen || IsProjectPanelOpen || IsUpdatePanelOpen || IsAccountPanelOpen || IsControlPanelOpen;
    public double PanelWidth { get; private set; } = 452;
    private double _windowWidth = 1200;
    public bool IsExamPaperLayoutPanelOpen => IsToolPanelOpen && SelectedTool?.Kind == ToolKind.ExamPaperLayout;
    public bool IsCertificateBatchPanelOpen => IsToolPanelOpen && SelectedTool?.Kind == ToolKind.CertificateBatch;
    public bool IsStandardToolPanelOpen => !IsExamPaperLayoutPanelOpen && !IsCertificateBatchPanelOpen;

    public string AllCountLabel => (_imageTools.Count + _fileTools.Count + _teachTools.Count).ToString("00");
    public string ImageCountLabel => VisibleImageTools.Count.ToString("00");
    public string FileCountLabel => VisibleFileTools.Count.ToString("00");
    public string TeachCountLabel => VisibleTeachTools.Count.ToString("00");
    public string ToolboxCountLabel => $"{_imageTools.Count + _fileTools.Count + _teachTools.Count} 件工具 · {_appCards.Length} 件常备";

    public int MatchCount => VisibleImageTools.Count + VisibleFileTools.Count + VisibleTeachTools.Count + _visibleAppCardCount;

    public string PinnedCountLabel => _visibleAppCardCount.ToString("00");

    public string SearchSummary =>
        string.IsNullOrWhiteSpace(SearchText)
            ? $"ALL TOOLS · {ToolDescriptionCatalog.ToolIds.Count}"
            : $"MATCH · {MatchCount}";

    public bool IsEmptyVisible => MatchCount == 0;
    public bool HasImageResults => VisibleImageTools.Count > 0;
    public bool HasFileResults => VisibleFileTools.Count > 0;
    public bool HasTeachResults => VisibleTeachTools.Count > 0;

    public void OpenTool(ToolItemViewModel tool)
    {
        SelectedTool = tool;
        UpdatePanelWidth(_windowWidth);
        if (!tool.IsCertificateBatch)
        {
            tool.HasResult = false;
            tool.ResultText = string.Empty;
        }
        IsProjectPanelOpen = false;
        IsUpdatePanelOpen = false;
        IsAccountPanelOpen = false;
        IsControlPanelOpen = false;
        IsProjectActive = false;
        IsToolPanelOpen = true;
        OnPropertyChanged(nameof(IsExamPaperLayoutPanelOpen));
        OnPropertyChanged(nameof(IsCertificateBatchPanelOpen));
        OnPropertyChanged(nameof(IsStandardToolPanelOpen));
        SetStatus("正在查看 " + tool.Name);
        NotifyPanelChanged();
    }

    public void UpdatePanelWidth(double windowWidth)
    {
        _windowWidth = windowWidth;
        var width = SelectedTool?.Kind is ToolKind.ExamPaperLayout or ToolKind.CertificateBatch
            ? Math.Max(452, windowWidth - 232)
            : 452;
        if (Math.Abs(PanelWidth - width) < 0.5) return;
        PanelWidth = width;
        OnPropertyChanged(nameof(PanelWidth));
    }

    public void OpenProject()
    {
        IsToolPanelOpen = false;
        IsUpdatePanelOpen = false;
        IsAccountPanelOpen = false;
        IsControlPanelOpen = false;
        IsProjectPanelOpen = true;
        IsProjectActive = true;
        SetStatus("正在查看 分晓 · GradeStrip");
        NotifyPanelChanged();
    }

    public void ClosePanels()
    {
        IsToolPanelOpen = false;
        IsProjectPanelOpen = false;
        IsUpdatePanelOpen = false;
        IsAccountPanelOpen = false;
        IsControlPanelOpen = false;
        IsProjectActive = false;
        SetStatus(DefaultStatusText);
        NotifyPanelChanged();
    }

    public static bool ShouldKeepCertificatePanelOpen(bool isToolPanelOpen, bool isCertificateBatch, bool isBusy) =>
        isToolPanelOpen && isCertificateBatch && isBusy;

    public bool CanDismissPanel()
    {
        if (ShouldKeepCertificatePanelOpen(IsToolPanelOpen,
                SelectedTool?.IsCertificateBatch == true, SelectedTool?.IsBusy == true))
        {
            SetStatus("奖状正在生成，请等待完成；如需停止，请先点「取消」，待处理结束后再关闭面板。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 打开更新页。只负责把当前已知信息摆出来，不自动联网——
    /// 联网必须由用户在页面里点「检查更新」触发（A3 的默认决定）。
    /// </summary>
    public void OpenUpdatePanel()
    {
        IsToolPanelOpen = false;
        IsProjectPanelOpen = false;
        IsAccountPanelOpen = false;
        IsControlPanelOpen = false;
        IsProjectActive = false;
        IsUpdatePanelOpen = true;

        InstallDescription = DescribeInstall();
        LastCheckedText = DescribeLastChecked();

        // 只有真的从没查过才说「还没有检查过」。
        // 否则同一张卡上会出现「还没有检查过」与「上次检查：今天 09:12」自相矛盾（P3-4）。
        if (!IsUpdateAvailable && !IsUpdateBusy && LastCheckedText == "从未检查")
            UpdateStateText = "还没有检查过";

        SetStatus("正在查看 检查更新");
        NotifyPanelChanged();
    }

    /// <summary>
    /// 打开账号页。只摆出本机已知状态：不打开浏览器、不登录、不列共享名单。
    /// 那三件事都要用户在页内再点一次。
    /// </summary>
    public void OpenAccountPanel()
    {
        Account.Refresh();
        IsToolPanelOpen = false;
        IsProjectPanelOpen = false;
        IsUpdatePanelOpen = false;
        IsControlPanelOpen = false;
        IsProjectActive = false;
        IsAccountPanelOpen = true;

        SetStatus("正在查看 账号与共享名单");
        NotifyPanelChanged();
    }

    /// <summary>
    /// 打开集控中心页。只摆出本机已知状态，不自动建立连接或触发网络请求。
    /// </summary>
    public void OpenControlPanel()
    {
        ControlCenter.Refresh();
        IsToolPanelOpen = false;
        IsProjectPanelOpen = false;
        IsUpdatePanelOpen = false;
        IsAccountPanelOpen = false;
        IsProjectActive = false;
        IsControlPanelOpen = true;

        SetStatus("正在查看 SECTL 集控中心");
        NotifyPanelChanged();
    }

    /// <summary>当前这台机器装的是哪一支：版本 + 形态 + 架构。</summary>
    private string DescribeInstall()
    {
        var kind = _installKind switch
        {
            InstallKind.InnoInstall => "安装包版本",
            InstallKind.InnoInstallLegacy => "安装包版本（旧版，未记录部署方式）",
            InstallKind.PortableZip => "绿色免安装版",
            InstallKind.MacBundle => "macOS 应用",
            InstallKind.MacBundlePortable => "macOS 便携应用",
            InstallKind.LinuxPortable => "Linux 绿色版",
            _ => "安装方式未能识别",
        };

        var rid = string.IsNullOrEmpty(_identity.Rid) ? "未知架构" : _identity.Rid;
        var mode = _identity.Mode switch
        {
            "sc" => "自带运行时",
            "fdd" => "依赖 .NET 运行时",
            _ => null,
        };

        return mode is null
            ? $"{kind} · {rid}"
            : $"{kind} · {rid} · {mode}";
    }

    private string DescribeLastChecked()
    {
        var last = _stateStore.Load().LastCheckUtc;
        return last is null
            ? "从未检查"
            : $"上次检查：{last.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// 切换更新源。选择立即持久化——用户不该因为重启就丢掉刚做的选择。
    /// </summary>
    public void ChangeSource(UpdateSource source)
    {
        if (_updateSource == source) return;

        _updateSource = source;
        SelectedSource = source;

        // 单选按钮靠这两个通知刷新选中态；漏了就会「点了没反应」。
        OnPropertyChanged(nameof(IsGitHubSource));
        OnPropertyChanged(nameof(IsMirrorSource));

        // 换源之后上一次的结论不再适用，清掉免得误导。
        HasSourceNote = false;
        SourceNoteText = "";
        if (!IsUpdateAvailable) UpdateStateText = "还没有检查过";

        var state = _stateStore.Load();
        // 换源只该改源，通道要原样保留（四参缺第 4 位就是 null，等于悄悄换回默认通道）。
        _stateStore.Save(new UpdateState(
            state.LastCheckUtc, state.SkippedVersions, source, state.PreferredChannel));

        SetStatus($"更新源已切换为 {UpdateSourceText.DisplayName(source)}");
    }

    /// <summary>
    /// 切换更新通道。选择立即持久化——用户不该因为重启就丢掉刚做的选择。
    /// </summary>
    public void ChangeChannel(UpdateChannel channel)
    {
        if (_updateChannel == channel) return;

        _updateChannel = channel;
        SelectedChannel = channel;

        // 单选按钮靠这两个通知刷新选中态；漏了就会「点了没反应」。
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsPreviewChannel));

        // 换通道之后上一次的结论不再适用，清掉免得误导。
        HasSourceNote = false;
        SourceNoteText = "";
        if (!IsUpdateAvailable) UpdateStateText = "还没有检查过";

        var state = _stateStore.Load();
        _stateStore.Save(new UpdateState(state.LastCheckUtc, state.SkippedVersions, state.PreferredSource, channel));

        SetStatus($"更新通道已切换为 {UpdateChannelText.DisplayName(channel)}");
    }

    /// <summary>
    /// 生产环境的检查服务：GitHub 为主，SECTL 镜像兜底。
    /// 两个服务共用进程内唯一的 handler，不额外建 HttpClient。
    /// </summary>
    private static IUpdateCheckService CreateDefaultCheckService(UpdateSource source)
    {
        var github = UpdateCheckService.CreateDefault();
        var mirror = new SectlMirrorCheckService(SharedHandlers.Default);

        // 选了镜像就只用镜像；默认（GitHub）才允许自动回退。
        return new MirroringUpdateCheckService(github, mirror, source, allowFallback: source == UpdateSource.GitHub);
    }

    public void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusBrush = error ? Seal : Moss;
    }

    /// <summary>
    /// 检查更新。
    ///
    /// fromUserGesture 决定失败要不要说出口：
    ///   - true（用户点了按钮）：失败必须给出具体原因；
    ///   - false（启动时静默检查）：失败一律无声，状态栏都不动。
    /// </summary>
    public async Task CheckUpdateAsync(bool fromUserGesture = true, CancellationToken cancellationToken = default)
    {
        // 注入的 checkService 优先（测试与走查走这条路，保证不真联网）；
        // 否则按用户选定的源建一个带镜像回退的服务。
        var mirroring = _checkService is null ? _checkServiceFactory(_updateSource) as MirroringUpdateCheckService : null;
        var service = _checkService ?? mirroring ?? _checkServiceFactory(_updateSource);
        var planner = new UpdatePlanner(service, _identity, _installKind, _updateChannel);

        // 记下「检查过」这件事本身。失败也算检查过，否则用户会以为按钮没反应。
        var state = _stateStore.Load();
        _stateStore.Save(new UpdateState(DateTimeOffset.UtcNow, state.SkippedVersions, state.PreferredSource, state.PreferredChannel));

        var result = await planner.PlanAsync(cancellationToken).ConfigureAwait(true);

        ApplyUpdatePlan(result);

        // 回退提示：实际用的源与用户选的不同时必须说清楚，
        // 否则他以为在查 GitHub，其实数据来自镜像，出问题时无从排查。
        if (mirroring is not null && mirroring.DidFallBack)
        {
            HasSourceNote = true;
            SourceNoteText =
                $"{UpdateSourceText.DisplayName(mirroring.Preference)} 连不上，已改用{UpdateSourceText.DisplayName(mirroring.UsedSource)}。";
        }
        else
        {
            HasSourceNote = false;
            SourceNoteText = "";
        }

        // 更新页的状态行与状态栏分开：状态栏会被其它操作覆盖，
        // 而页面上这一行必须一直留着上次检查的结论。
        UpdateStateText = result.Status switch
        {
            UpdatePlanStatus.UpdateAvailable when IsUpdateAvailable
                => $"发现新版本 {result.Proposal!.VersionText}",
            UpdatePlanStatus.UpdateAvailable => $"新版本 {result.Proposal!.VersionText}（你已选择跳过）",
            UpdatePlanStatus.UpToDate => "已是最新版本",
            UpdatePlanStatus.Failed => result.Message ?? "检查失败",
            _ => UpdateStateText,
        };
        LastCheckedText = DescribeLastChecked();

        // 被判为「有新版」但被用户跳过时，ApplyUpdatePlan 不会设 IsUpdateAvailable。
        // 此时必须给一句中性提示，否则会出现「状态栏说有新版本、却没有更新按钮」的自相矛盾。
        if (result.Status == UpdatePlanStatus.UpdateAvailable && !IsUpdateAvailable)
        {
            if (fromUserGesture)
                SetStatus($"新版本 {result.Proposal!.VersionText} 已被你跳过；如需重新提示，请删除 %LOCALAPPDATA%\\TeachCraft\\Update\\state.json。");
            return;
        }

        if (result.Status == UpdatePlanStatus.Failed && fromUserGesture)
            SetStatus(result.Message ?? "检查更新失败，请稍后再试。", error: true);
        else if (result.Status == UpdatePlanStatus.UpToDate && fromUserGesture)
            SetStatus(result.Message ?? "已是最新版本");
        else if (result.Status == UpdatePlanStatus.UpdateAvailable)
            SetStatus($"发现新版本 {result.Proposal!.VersionText}");
    }

    /// <summary>
    /// 按资产来源取校验清单。
    ///
    /// 判断依据是下载地址而不是「用户选了哪个源」：自动回退之后，
    /// 用户选的仍是 GitHub，但资产实际来自镜像——这时必须按地址走，
    /// 否则会去 GitHub 拉清单来校验镜像下的包，两边的发布未必同时可用。
    /// </summary>
    private static async Task<UpdateDownloader.FetchResult> FetchChecksumsAsync(
        UpdateDownloader downloader,
        UpdateProposal proposal,
        CancellationToken cancellationToken)
    {
        var fromMirror = proposal.Asset.DownloadUrl.Contains("sectl.cn", StringComparison.OrdinalIgnoreCase);

        if (!fromMirror)
            return await downloader.FetchChecksumsAsync(
                UpdateCheckService.DefaultFeedUrl, proposal.Release.TagName, cancellationToken).ConfigureAwait(false);

        // 镜像的版本记录不带 sha256，只能取它托管的 SHA256SUMS 文件。
        var sumsUrl = SectlMirrorFeed.FindChecksumsUrl(proposal.Release.Assets);
        if (sumsUrl is null)
            return UpdateDownloader.FetchResult.Ok(null);

        return await downloader.FetchChecksumsFromUrlAsync(sumsUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把规划结果落到界面状态。纯函数式的赋值，不触发网络。</summary>
    public void ApplyUpdatePlan(UpdatePlanResult result)
    {
        // 用户点过「稍后」的版本不再提示——必须真的读回跳过列表，
        // 否则点一次就把这个功能永久静音了。
        if (result.Status == UpdatePlanStatus.UpdateAvailable && result.Proposal is not null)
        {
            var skipped = _stateStore.Load().SkippedVersions;
            if (skipped.Contains(result.Proposal.VersionText))
            {
                PendingUpdate = null;
                IsUpdateAvailable = false;
                UpdateBadgeText = "";
                UpdateExplanation = "";
                return;
            }
        }

        if (result.Status == UpdatePlanStatus.UpdateAvailable && result.Proposal is not null)
        {
            PendingUpdate = result.Proposal;
            IsUpdateAvailable = true;
            UpdateBadgeText = $"新版本 {result.Proposal.VersionText}";
            UpdateExplanation = BuildUpdateExplanation(_identity.Version);
        }
        else
        {
            PendingUpdate = null;
            IsUpdateAvailable = false;
            UpdateBadgeText = "";
            UpdateExplanation = "";
        }
    }

    /// <summary>
    /// 走查专用：构造「有新版」的界面状态。只改内存中的展示字段，不写状态文件、不发请求。
    /// 没有它，全新进程里徽章恒为空，「提示条正确显示」这件事无法机器取证。
    /// </summary>
    public void ArmPreviewBadge(string version)
    {
        PendingUpdate = null;
        IsUpdateAvailable = true;
        UpdateBadgeText = $"新版本 {version}";
        UpdateExplanation = BuildUpdateExplanation(_identity.Version);
        // 更新页与状态栏徽章必须同步，否则走查时页面还写着「还没有检查过」，
        // 截出来的图不代表任何真实状态。
        UpdateStateText = $"发现新版本 {version}";
    }

    /// <summary>
    /// 组装更新提示的解释文案。分两条：
    /// 预览用户额外说明升级路径，所有人都要看到完整性信任边界。
    /// </summary>
    private static string BuildUpdateExplanation(SemanticVersion current)
    {
        const string trustBoundary =
            "安装包与校验清单来自同一次未签名的发布：校验只能证明下载过程没有损坏，不能证明发布者身份。";

        if (!current.IsPreview)
            return "更新会下载与你当前安装方式匹配的安装包。" + trustBoundary;

        return "你装的是预览版。旧预览版的版本号本来就低于正式版，因此只能升级到下一个正式版；"
             + "「预览直接跳到下一个预览」要等到新正式版发布之后、又出现更新的预览时才成立。"
             + trustBoundary;
    }

    /// <summary>
    /// 启动时的静默检查：默认关闭，由调用方显式开启。
    ///
    /// 与用户手点的区别只有一处——**失败完全不改状态栏**。
    /// 断网的教室不该因为一次后台检查就在状态栏里挂着错误。
    /// </summary>
    public async Task SilentCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CheckUpdateAsync(fromUserGesture: false, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 静默路径连异常都要吞掉：没点按钮就不该看到任何错误。
        }
    }

    /// <summary>用户点「稍后」：清掉提示，并把该版本记进跳过列表。</summary>
    public void DismissUpdate()
    {
        // 必须在清空 PendingUpdate 之前拿到版本号，否则记不下任何东西。
        var skippedVersion = PendingUpdate?.VersionText;
        LastSkippedVersion = skippedVersion;

        PendingUpdate = null;
        IsUpdateAvailable = false;
        UpdateBadgeText = "";
        // 徽章藏起来了，解释文案也必须一起清掉；
        // 否则徽章消失后工具提示仍挂着上一次的说辞，用户会以为还留着更新。
        UpdateExplanation = "";

        if (skippedVersion is null) return;

        var state = _stateStore.Load();
        var skipped = state.SkippedVersions.ToList();
        if (!skipped.Contains(skippedVersion))
            skipped.Add(skippedVersion);
        // 只追加跳过记录，源与通道原样带回去——两参构造会把它们落成 null，
        // 重启后老师选的镜像/预览通道被静默换成 GitHub+默认通道（审计 P2-35）。
        _stateStore.Save(new UpdateState(
            DateTimeOffset.UtcNow, skipped, state.PreferredSource, state.PreferredChannel));
    }

    /// <summary>
    /// 下载并执行更新。返回给界面的一句话结果（成功时的指引或失败原因）。
    ///
    /// 分级：Windows 安装包可以真正装完并重启；其余形态只把下载好的文件位置
    /// 与可执行的替换步骤告诉用户——不自替换正在运行的程序。
    /// </summary>
    public Task<string> DownloadAndInstallAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        // 旧重载保留：状态栏那条只显示百分比的路径不该被迫接一个新类型。
        DownloadAndInstallCoreAsync(
            progress is null ? null : new Progress<DownloadProgress>(p => progress.Report(p.Percent)),
            cancellationToken);

    /// <summary>
    /// 带速度的下载。界面用 <see cref="DownloadProgress"/> 同时驱动进度条与速度行。
    /// </summary>
    public Task<string> DownloadAndInstallAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken = default) =>
        DownloadAndInstallCoreAsync(progress, cancellationToken);

    private async Task<string> DownloadAndInstallCoreAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var proposal = PendingUpdate;
        if (proposal is null) return "没有待安装的更新。";

        try
        {
            IsLastInstallFailure = false;
            SetStatus($"正在下载 {proposal.VersionText}…");

            // handler 由 Core 统一提供（全进程复用），并通过构造注入以便测试替身。
            var downloader = _downloaderFactory();

            // 校验清单必须拿到，否则失败关闭——绝不降级成「只校验长度」。
            // 但要区分「取不到」（网络问题）与「发布没提供」（版本问题），否则会把校园网拦截误报成发布缺陷。
            //
            // 清单跟着资产走：镜像源的包从 stk.sectl.cn 下载，清单也从镜像取——
            // 两边的文件是同一次发布同步过去的，交叉取源会引入不一致风险。
            var checksumResult = await FetchChecksumsAsync(
                downloader, proposal, cancellationToken).ConfigureAwait(true);

            if (!checksumResult.IsSuccess)
            {
                IsLastInstallFailure = true;
                SetStatus(checksumResult.Failure!, error: true);
                return checksumResult.Failure!;
            }

            var checksums = checksumResult.Map;
            if (checksums is null)
            {
                var missing = UpdateErrorText.ForMissingChecksums("下载更新");
                IsLastInstallFailure = true;
                SetStatus(missing, error: true);
                return missing;
            }

            checksums.TryGetValue(proposal.Asset.Name, out var expected);
            if (string.IsNullOrEmpty(expected))
            {
                var missing = UpdateErrorText.ForAssetNotInChecksums("下载更新");
                IsLastInstallFailure = true;
                SetStatus(missing, error: true);
                return missing;
            }

            var download = await downloader.DownloadAsync(
                proposal.Asset, proposal.Release.TagName, expected, cancellationToken, progress).ConfigureAwait(true);

            if (!download.IsSuccess)
            {
                IsLastInstallFailure = true;
                SetStatus(download.Failure!, error: true);
                return download.Failure!;
            }

            var plan = UpdateInstaller.BuildPlan(proposal, download.Path!);

            // RunSetup 与 ReplacePortable 都需要 App 层介入（进程与生命周期控制）；
            // 其余形态只给指引。
            if (plan.Action is InstallerAction.RunSetup or InstallerAction.ReplacePortable)
            {
                SetStatus(plan.Action == InstallerAction.ReplacePortable
                    ? "正在替换为新版本，完成后会自动重新打开…"
                    : "正在安装，安装完成后会自动重新打开…");
                // 交由 App 层执行（那里才有进程与生命周期控制）。
                var handlerDelegate = InstallerRequested;
                if (handlerDelegate is null)
                {
                    var noHandler = plan.Action == InstallerAction.ReplacePortable
                        ? "已下载完成，但当前无法自动替换，请手动解压更新包覆盖旧文件。"
                        : "已下载完成，但当前无法自动安装，请手动运行下载的安装包。";
                    SetStatus(noHandler);
                    return noHandler;
                }
                var handlerMessage = await handlerDelegate(download.Path!, plan).ConfigureAwait(true);
                // 安装前置确认被拒（「暂不安装」）也是一次失败终态：状态栏必须保持红点，
                // 否则绿色会让人误读成安装已在进行（审计 P2-28）。
                // 失败的 App 层话术都以「已取消安装」开头，其余为成功指引。
                IsLastInstallFailure = handlerMessage.StartsWith("已取消安装", StringComparison.Ordinal);
                return handlerMessage;
            }

            // ShowGuidance（macOS / Linux）在下载与校验成功后记录待展示更新日志：
            // 若用户随后手动替换并启动目标版本，新版本首次启动只展示一次；
            // 若未安装而再次打开旧版本，启动判定会识别版本未变并自动清除。
            _pendingChangelogStore.Save(PendingChangelogEntry.FromProposal(_identity.Version, proposal));
            var guidance = plan.Guidance ?? "已下载完成。";
            SetStatus(guidance);
            return guidance;
        }
        catch (Exception ex)
        {
            var reason = UpdateErrorText.DescribeForUi(ex, "下载更新");
            IsLastInstallFailure = true;
            SetStatus(reason, error: true);
            return reason;
        }
    }

    /// <summary>
    /// 最近一次「下载并安装」是否以失败终态收场（网络失败、校验失败、安装前置确认被拒）。
    ///
    /// 状态栏把返回文案重报时必须沿用失败配色（审计 P2-28）：失败在 ViewModel 里已按
    /// error:true 画过红点，调用方无条件 SetStatus 会把它冲成绿色——绿色的「已取消安装」
    /// 读起来像「装好了」。成功终态（含 ShowGuidance 指引）恒为 false。
    /// </summary>
    public bool IsLastInstallFailure { get; private set; }

    /// <summary>需要真正启动安装包时由 App 层订阅，ViewModel 不直接起进程。</summary>
    public event Func<string, InstallerPlan, Task<string>>? InstallerRequested;

    /// <summary>本次启动由应用内升级成功后待展示一次的更新日志；消费后置空。</summary>
    public UpdateChangelogDocument? StartupChangelog { get; private set; }

    internal void SetStartupChangelog(UpdateChangelogDocument? document) => StartupChangelog = document;

    internal UpdateChangelogDocument? ConsumeStartupChangelog()
    {
        var current = StartupChangelog;
        StartupChangelog = null;
        return current;
    }

    internal PendingChangelogEntry? CreatePendingChangelogEntry() =>
        PendingUpdate is { } proposal
            ? PendingChangelogEntry.FromProposal(_identity.Version, proposal)
            : null;

    private readonly IUpdateStateStore _stateStore;
    private readonly IPendingChangelogStore _pendingChangelogStore;
    private readonly Func<UpdateDownloader> _downloaderFactory;


    public void SetSection(string section)
    {
        IsAllActive = section == "all";
        IsImageActive = section == "image";
        IsFileActive = section == "file";
        IsTeachActive = section == "teach";
        IsProjectActive = false;
    }

    public ToolItemViewModel? FindTool(string id) =>
        _imageTools.Concat(_fileTools).Concat(_teachTools).FirstOrDefault(tool => tool.Id == id);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsToolPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPanelOpen));
        OnPropertyChanged(nameof(IsExamPaperLayoutPanelOpen));
        OnPropertyChanged(nameof(IsCertificateBatchPanelOpen));
        OnPropertyChanged(nameof(IsStandardToolPanelOpen));
    }
    partial void OnIsProjectPanelOpenChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));
    partial void OnIsAccountPanelOpenChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));
    partial void OnIsControlPanelOpenChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));

    private void ApplyFilter()
    {
        var query = (SearchText ?? string.Empty).Trim().ToLowerInvariant();

        VisibleImageTools.Clear();
        VisibleFileTools.Clear();
        VisibleTeachTools.Clear();

        foreach (var tool in _imageTools)
            if (Matches(tool, query)) VisibleImageTools.Add(tool);

        foreach (var tool in _fileTools)
            if (Matches(tool, query)) VisibleFileTools.Add(tool);

        foreach (var tool in _teachTools)
            if (Matches(tool, query)) VisibleTeachTools.Add(tool);

        ApplyCardFilter(query);

        OnPropertyChanged(nameof(AllCountLabel));
        OnPropertyChanged(nameof(ImageCountLabel));
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(TeachCountLabel));
        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(IsEmptyVisible));
        OnPropertyChanged(nameof(HasImageResults));
        OnPropertyChanged(nameof(HasFileResults));
        OnPropertyChanged(nameof(HasTeachResults));
    }

    private void ApplyCardFilter(string query)
    {
        IsGradeStripVisible = Matches(GradeStripCard, query);
        IsSeatGridVisible = Matches(SeatGridCard, query);
        IsClassBlockVisible = Matches(ClassBlockCard, query);
        IsGradeWeaveVisible = Matches(GradeWeaveCard, query);
        IsMeetRosterVisible = Matches(MeetRosterCard, query);
        IsHomeworkVisible = Matches(HomeworkCard, query);
        HasPinnedResults = IsGradeStripVisible || IsSeatGridVisible || IsClassBlockVisible ||
                           IsGradeWeaveVisible || IsMeetRosterVisible || IsHomeworkVisible;
        _visibleAppCardCount = new[]
        {
            IsGradeStripVisible, IsSeatGridVisible, IsClassBlockVisible, IsGradeWeaveVisible, IsMeetRosterVisible,
            IsHomeworkVisible,
        }.Count(visible => visible);

        OnPropertyChanged(nameof(IsGradeStripVisible));
        OnPropertyChanged(nameof(IsSeatGridVisible));
        OnPropertyChanged(nameof(IsClassBlockVisible));
        OnPropertyChanged(nameof(IsGradeWeaveVisible));
        OnPropertyChanged(nameof(IsMeetRosterVisible));
        OnPropertyChanged(nameof(IsHomeworkVisible));
        OnPropertyChanged(nameof(HasPinnedResults));
        OnPropertyChanged(nameof(PinnedCountLabel));
    }

    private static bool Matches(string haystack, string query)
    {
        if (query.Length == 0) return true;
        return haystack.ToLowerInvariant().Contains(query, StringComparison.Ordinal);
    }

    private static bool Matches(AppCardViewModel card, string query) => Matches(card.SearchText, query);

    private static bool Matches(ToolItemViewModel tool, string query)
    {
        if (query.Length == 0) return true;

        // 说明文案也进检索面：老师记得「EXIF」「页码」这类词，卡片短描述里未必有。
        var haystack = tool.Detail is { } detail
            ? string.Join(' ', tool.Name, tool.Id, tool.Description, tool.InputText, tool.OutputText,
                detail.Tagline, string.Join(' ', detail.Highlights), detail.InputDetail, detail.OutputDetail,
                detail.ProcessingDetail, string.Join(' ', detail.Features))
            : string.Join(' ', tool.Name, tool.Id, tool.Description, tool.InputText, tool.OutputText);

        return haystack.ToLowerInvariant().Contains(query, StringComparison.Ordinal);
    }

    private void NotifyPanelChanged()
    {
        OnPropertyChanged(nameof(IsPanelOpen));
    }
}
