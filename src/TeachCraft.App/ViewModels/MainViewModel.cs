using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly BuildIdentity _identity;
    private readonly IUpdateCheckService? _checkService;
    private readonly InstallKind _installKind;

    /// <summary>
    /// 界面版本号取自可自证的构建身份，必须包含预览序号，
    /// 否则 v1.17.0-preview.141 与 -preview.142 在界面上完全同形。
    /// AssemblyVersion 仍是三段，保持不变。
    /// </summary>
    public string VersionText { get; }

    public string VersionLine => $"{VersionText} · 10 TOOLS · 5 APPS";

    public ObservableCollection<ToolItemViewModel> VisibleImageTools { get; } = new();
    public ObservableCollection<ToolItemViewModel> VisibleFileTools { get; } = new();

    /// <summary>
    /// 案头常备卡：状态（展开与否）与检索文本。顺序固定为分晓 / 归位 / 时序 / 织序 / 竞名，
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
    };

    public AppCardViewModel GradeStripCard => _appCards[0];
    public AppCardViewModel SeatGridCard => _appCards[1];
    public AppCardViewModel ClassBlockCard => _appCards[2];
    public AppCardViewModel GradeWeaveCard => _appCards[3];
    public AppCardViewModel MeetRosterCard => _appCards[4];

    public bool IsGradeStripVisible { get; private set; } = true;
    public bool IsSeatGridVisible { get; private set; } = true;
    public bool IsClassBlockVisible { get; private set; } = true;
    public bool IsGradeWeaveVisible { get; private set; } = true;
    public bool IsMeetRosterVisible { get; private set; } = true;
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
        Func<UpdateDownloader>? downloaderFactory = null)
    {
        _identity = identity ?? BuildIdentity.Current(typeof(MainViewModel));
        _checkService = checkService;
        _stateStore = stateStore ?? new UpdateStateStore();
        _downloaderFactory = downloaderFactory ?? (() => new UpdateDownloader(SharedHandlers.Default));

        // 安装形态必须来自真实探测：注册表身份记录 → 路径推断。
        // 写死成 Unknown 会让「自动分配安装包」永远走不到，用户只会看到「暂未提供」。
        var info = installInfo ?? InstallKindDetector.Current(InstallRecordReader.ReadAll());
        _installKind = info.Kind;

        // 探测出的部署模式 / 运行标识补进身份——legacy 构建自带的信息不完整。
        if (info.Mode is not null) _identity = _identity.WithMode(info.Mode);
        if (_identity.Rid is null && info.Rid is not null) _identity = _identity.WithRid(info.Rid);

        VersionText = $"V{_identity.Describe()}";

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
                "将 DOCX 文档转换为 PDF 文件，版式不跑偏。", "file", "档",
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

        ApplyFilter();
    }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ToolItemViewModel? _selectedTool;
    [ObservableProperty] private bool _isToolPanelOpen;
    [ObservableProperty] private bool _isProjectPanelOpen;
    [ObservableProperty] private string _statusText = DefaultStatusText;
    [ObservableProperty] private IBrush _statusBrush = Moss;

    /// <summary>状态栏默认/复位文案。默认文案与复位文案必须是同一个常量，否则用完面板后旧口径会复活。</summary>
    public const string DefaultStatusText = "本机运行 · 文件不上传（仅检查更新会联网）";

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
    [ObservableProperty] private bool _isAllActive = true;
    [ObservableProperty] private bool _isImageActive;
    [ObservableProperty] private bool _isFileActive;
    [ObservableProperty] private bool _isProjectActive;

    public bool IsPanelOpen => IsToolPanelOpen || IsProjectPanelOpen;

    public string ImageCountLabel => VisibleImageTools.Count.ToString("00");
    public string FileCountLabel => VisibleFileTools.Count.ToString("00");

    public int MatchCount => VisibleImageTools.Count + VisibleFileTools.Count + _visibleAppCardCount;

    public string PinnedCountLabel => _visibleAppCardCount.ToString("00");

    public string SearchSummary =>
        string.IsNullOrWhiteSpace(SearchText)
            ? "ALL TOOLS · 10"
            : $"MATCH · {MatchCount}";

    public bool IsEmptyVisible => MatchCount == 0;
    public bool HasImageResults => VisibleImageTools.Count > 0;
    public bool HasFileResults => VisibleFileTools.Count > 0;

    public void OpenTool(ToolItemViewModel tool)
    {
        SelectedTool = tool;
        tool.HasResult = false;
        tool.ResultText = string.Empty;
        IsProjectPanelOpen = false;
        IsProjectActive = false;
        IsToolPanelOpen = true;
        SetStatus("正在查看 " + tool.Name);
        NotifyPanelChanged();
    }

    public void OpenProject()
    {
        IsToolPanelOpen = false;
        IsProjectPanelOpen = true;
        IsProjectActive = true;
        SetStatus("正在查看 分晓 · GradeStrip");
        NotifyPanelChanged();
    }

    public void ClosePanels()
    {
        IsToolPanelOpen = false;
        IsProjectPanelOpen = false;
        IsProjectActive = false;
        SetStatus(DefaultStatusText);
        NotifyPanelChanged();
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
        var service = _checkService ?? UpdateCheckService.CreateDefault();
        var planner = new UpdatePlanner(service, _identity, _installKind);

        var result = await planner.PlanAsync(cancellationToken).ConfigureAwait(true);

        ApplyUpdatePlan(result);

        // 被判为「有新版」但被用户跳过时，ApplyUpdatePlan 不会设 IsUpdateAvailable。
        // 此时必须给一句中性提示，否则会出现「状态栏说有新版本、却没有更新按钮」的自相矛盾。
        if (result.Status == UpdatePlanStatus.UpdateAvailable && !IsUpdateAvailable)
        {
            if (fromUserGesture)
                SetStatus($"新版本 {result.Proposal!.VersionText} 已被你跳过；如需重新提示，请删除配置目录下的 state.json。");
            return;
        }

        if (result.Status == UpdatePlanStatus.Failed && fromUserGesture)
            SetStatus(result.Message ?? "检查更新失败，请稍后再试。", error: true);
        else if (result.Status == UpdatePlanStatus.UpToDate && fromUserGesture)
            SetStatus(result.Message ?? "已是最新版本");
        else if (result.Status == UpdatePlanStatus.UpdateAvailable)
            SetStatus($"发现新版本 {result.Proposal!.VersionText}");
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
        _stateStore.Save(new UpdateState(DateTimeOffset.UtcNow, skipped));
    }

    /// <summary>
    /// 下载并执行更新。返回给界面的一句话结果（成功时的指引或失败原因）。
    ///
    /// 分级：Windows 安装包可以真正装完并重启；其余形态只把下载好的文件位置
    /// 与可执行的替换步骤告诉用户——不自替换正在运行的程序。
    /// </summary>
    public async Task<string> DownloadAndInstallAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = PendingUpdate;
        if (proposal is null) return "没有待安装的更新。";

        try
        {
            SetStatus($"正在下载 {proposal.VersionText}…");

            // handler 由 Core 统一提供（全进程复用），并通过构造注入以便测试替身。
            var downloader = _downloaderFactory();

            // 校验清单必须拿到，否则失败关闭——绝不降级成「只校验长度」。
            // 但要区分「取不到」（网络问题）与「发布没提供」（版本问题），否则会把校园网拦截误报成发布缺陷。
            var checksumResult = await downloader.FetchChecksumsAsync(
                UpdateCheckService.DefaultFeedUrl, proposal.Release.TagName, cancellationToken).ConfigureAwait(true);

            if (!checksumResult.IsSuccess)
            {
                SetStatus(checksumResult.Failure!, error: true);
                return checksumResult.Failure!;
            }

            var checksums = checksumResult.Map;
            if (checksums is null)
            {
                var missing = UpdateErrorText.ForMissingChecksums("下载更新");
                SetStatus(missing, error: true);
                return missing;
            }

            checksums.TryGetValue(proposal.Asset.Name, out var expected);
            if (string.IsNullOrEmpty(expected))
            {
                var missing = UpdateErrorText.ForAssetNotInChecksums("下载更新");
                SetStatus(missing, error: true);
                return missing;
            }

            var download = await downloader.DownloadAsync(
                proposal.Asset, proposal.Release.TagName, expected, cancellationToken, progress).ConfigureAwait(true);

            if (!download.IsSuccess)
            {
                SetStatus(download.Failure!, error: true);
                return download.Failure!;
            }

            var plan = UpdateInstaller.BuildPlan(proposal, download.Path!);

            if (plan.Action == InstallerAction.RunSetup)
            {
                SetStatus("正在安装，安装完成后会自动重新打开…");
                // 交由 App 层执行（那里才有进程与生命周期控制）。
                var handlerDelegate = InstallerRequested;
                if (handlerDelegate is null)
                {
                    var noHandler = "已下载完成，但当前无法自动安装，请手动运行下载的安装包。";
                    SetStatus(noHandler);
                    return noHandler;
                }
                return await handlerDelegate(download.Path!, plan).ConfigureAwait(true);
            }

            var guidance = plan.Guidance ?? "已下载完成。";
            SetStatus(guidance);
            return guidance;
        }
        catch (Exception ex)
        {
            var reason = UpdateErrorText.DescribeForUi(ex, "下载更新");
            SetStatus(reason, error: true);
            return reason;
        }
    }

    /// <summary>需要真正启动安装包时由 App 层订阅，ViewModel 不直接起进程。</summary>
    public event Func<string, InstallerPlan, Task<string>>? InstallerRequested;

    private readonly IUpdateStateStore _stateStore;
    private readonly Func<UpdateDownloader> _downloaderFactory;


    public void SetSection(string section)
    {
        IsAllActive = section == "all";
        IsImageActive = section == "image";
        IsFileActive = section == "file";
        IsProjectActive = false;
    }

    public ToolItemViewModel? FindTool(string id) =>
        _imageTools.Concat(_fileTools).FirstOrDefault(tool => tool.Id == id);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsToolPanelOpenChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));
    partial void OnIsProjectPanelOpenChanged(bool value) => OnPropertyChanged(nameof(IsPanelOpen));

    private void ApplyFilter()
    {
        var query = (SearchText ?? string.Empty).Trim().ToLowerInvariant();

        VisibleImageTools.Clear();
        VisibleFileTools.Clear();

        foreach (var tool in _imageTools)
            if (Matches(tool, query)) VisibleImageTools.Add(tool);

        foreach (var tool in _fileTools)
            if (Matches(tool, query)) VisibleFileTools.Add(tool);

        ApplyCardFilter(query);

        OnPropertyChanged(nameof(ImageCountLabel));
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(IsEmptyVisible));
        OnPropertyChanged(nameof(HasImageResults));
        OnPropertyChanged(nameof(HasFileResults));
    }

    private void ApplyCardFilter(string query)
    {
        IsGradeStripVisible = Matches(GradeStripCard, query);
        IsSeatGridVisible = Matches(SeatGridCard, query);
        IsClassBlockVisible = Matches(ClassBlockCard, query);
        IsGradeWeaveVisible = Matches(GradeWeaveCard, query);
        IsMeetRosterVisible = Matches(MeetRosterCard, query);
        HasPinnedResults = IsGradeStripVisible || IsSeatGridVisible || IsClassBlockVisible ||
                           IsGradeWeaveVisible || IsMeetRosterVisible;
        _visibleAppCardCount = new[]
        {
            IsGradeStripVisible, IsSeatGridVisible, IsClassBlockVisible, IsGradeWeaveVisible, IsMeetRosterVisible,
        }.Count(visible => visible);

        OnPropertyChanged(nameof(IsGradeStripVisible));
        OnPropertyChanged(nameof(IsSeatGridVisible));
        OnPropertyChanged(nameof(IsClassBlockVisible));
        OnPropertyChanged(nameof(IsGradeWeaveVisible));
        OnPropertyChanged(nameof(IsMeetRosterVisible));
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
