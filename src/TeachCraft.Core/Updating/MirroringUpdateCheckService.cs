namespace TeachCraft.Core.Updating;

/// <summary>
/// 带镜像回退的更新检查。
///
/// 三种工作方式：
///   <list type="bullet">
///     <item><see cref="UpdateSource.GitHub"/>（默认）：先问 GitHub；连不上时自动问 SECTL 镜像。</item>
///     <item>用户手动指定「SECTL 镜像」：直接问镜像，不回退（他要的就是镜像）。</item>
///     <item>用户手动指定「GitHub」：直接问 GitHub，不回退（他要的就是 GitHub）。</item>
///   </list>
///
/// 自动回退只在「首选源失败」时发生，且必须把**实际使用的源**报给界面——
/// 否则用户看到「发现新版本」却不知道数据从哪来，出问题时无从排查。
/// </summary>
public sealed class MirroringUpdateCheckService : IUpdateCheckService
{
    private readonly IUpdateCheckService _github;
    private readonly IUpdateCheckService _mirror;
    private readonly UpdateSource _preference;
    private readonly bool _allowFallback;

    public MirroringUpdateCheckService(
        IUpdateCheckService github,
        IUpdateCheckService mirror,
        UpdateSource preference,
        bool allowFallback = true)
    {
        _github = github;
        _mirror = mirror;
        _preference = preference;
        // 手动选了某一源时由调用方传 false：他明确要的就是这个源。
        _allowFallback = allowFallback;
    }

    /// <summary>实际提供数据的源。检查完成后由界面读取并显示。</summary>
    public UpdateSource UsedSource { get; private set; }

    /// <summary>首选源失败的原因。回退成功时仍然保留，供界面在需要时说明。</summary>
    public string? PrimaryFailure { get; private set; }

    /// <summary>是否发生了回退——界面据此提示「GitHub 连不上，已改用镜像」。</summary>
    public bool DidFallBack { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default)
    {
        UsedSource = _preference;
        PrimaryFailure = null;
        DidFallBack = false;

        var primary = _preference == UpdateSource.SectlMirror ? _mirror : _github;
        var result = await primary.CheckAsync(current, cancellationToken).ConfigureAwait(false);

        // 用户主动取消：不该再打另一个源。
        if (result.IsSuccess || !_allowFallback || string.IsNullOrEmpty(result.Failure))
            return result;

        PrimaryFailure = result.Failure;
        UsedSource = _preference == UpdateSource.SectlMirror ? UpdateSource.GitHub : UpdateSource.SectlMirror;
        DidFallBack = true;

        var secondary = UsedSource == UpdateSource.SectlMirror ? _mirror : _github;
        var fallback = await secondary.CheckAsync(current, cancellationToken).ConfigureAwait(false);

        if (fallback.IsSuccess) return fallback;

        // 两个源都失败：优先报首选源的失败原因（用户点的是那个），
        // 但把「另一个源也不通」这个事实说清楚，否则用户会以为只试了一次。
        var other = _preference == UpdateSource.SectlMirror ? UpdateSource.GitHub : UpdateSource.SectlMirror;
        UsedSource = _preference;
        DidFallBack = false;
        return UpdateCheckResult.Fail(
            $"{PrimaryFailure}（已尝试{UpdateSourceText.DisplayName(other)}，同样失败：{fallback.Failure}）");
    }

    /// <summary>用户选择的源（回退后仍指向他原本选的那个）。</summary>
    public UpdateSource Preference => _preference;
}

/// <summary>
/// 从 SECTL 镜像读版本列表。
///
/// 与 GitHub 的差异：镜像的 `/distribution` 一次返回全部项目、全部版本、全部资产，
/// 没有分页也没有单版本查询。数据量约 350 KB，对「偶尔检查一次更新」来说可以接受，
/// 而且换来的是「一次请求拿全」——不会出现版本列表拿到了、资产列表没拿到的不一致状态。
/// </summary>
public sealed class SectlMirrorCheckService : IUpdateCheckService
{
    private readonly HttpClient _client;
    private readonly string _apiBase;
    private readonly string _projectSlug;
    private readonly string _action;

    public SectlMirrorCheckService(
        HttpMessageHandler handler,
        string? apiBase = null,
        string? projectSlug = null,
        string action = "检查更新")
        : this(new HttpClient(handler, disposeHandler: false), apiBase, projectSlug, action)
    {
    }

    private SectlMirrorCheckService(HttpClient client, string? apiBase, string? projectSlug, string action)
    {
        _client = client;
        _apiBase = apiBase ?? SectlMirrorFeed.DefaultApiBase;
        _projectSlug = projectSlug ?? SectlMirrorFeed.DefaultProjectSlug;
        _action = action;
    }

    public async Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = SectlMirrorFeed.BuildDistributionUrl(_apiBase);
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Fail(UpdateErrorText.ForStatus(response.StatusCode, _action, "镜像"));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body) || body.AsSpan().TrimStart()[0] != '{')
                return UpdateCheckResult.Fail(UpdateErrorText.ForNonJsonBody(_action, "镜像"));

            IReadOnlyList<ReleaseEntry> entries;
            try
            {
                entries = SectlMirrorFeed.Parse(body, _projectSlug);
            }
            catch (System.Text.Json.JsonException)
            {
                return UpdateCheckResult.Fail(UpdateErrorText.ForNonJsonBody(_action, "镜像"));
            }

            if (entries.Count == 0)
                return UpdateCheckResult.Fail(UpdateErrorText.MirrorHasNoProject);

            var candidates = UpdateFeed.SelectCandidates(entries, current);
            return UpdateCheckResult.Ok(candidates.FirstOrDefault(), candidates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Fail(string.Empty);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail(UpdateErrorText.DescribeForUi(ex, _action, "镜像"));
        }
    }
}
