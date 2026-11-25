using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;
using TeachCraft.Shared;

namespace TeachCraft.App.ViewModels;

/// <summary>共享名单库里的一行。只带摘要，不把学生明细拉进界面。</summary>
public sealed record SharedRosterRow(
    string Id,
    string Name,
    int Version,
    int StudentCount,
    string FieldsText,
    string UpdatedText);

/// <summary>
/// 账号页的状态机。所有云端动作都经这里，界面只绑定属性、只抛事件。
/// 未配置时服务全是 null：构造、刷新、列名单都不碰文件也不联网。
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    /// <summary>同意文案的版本。改了 CloudConsentText 的实质内容就 +1，要求重新同意。</summary>
    public const int CurrentConsentVersion = 1;

    public const string NotConfiguredStatus = "账号与共享名单 · 此版本尚未接入 SECTL 账号。";
    public const string SignedOutStatus = "未登录 · 云同步关闭";
    public const string ConsentRequiredStatus = "请先阅读并同意共享名单字段说明，再登录。";
    public const string SettingsSaveFailedWarning = "设置未能保存到本机，本次修改仅在本次运行有效。";

    private readonly CloudRuntime _runtime;
    private readonly DesktopAccountSignInFlow? _signIn;
    private CancellationTokenSource? _opsCts;
    private bool _subscribed;
    private bool _disposed;
    private CloudSyncSettings _snapshot;
    private bool _applyingCloudEnabled;
    private bool _raisingNotifier;

    public AccountViewModel(CloudRuntime runtime, DesktopAccountSignInFlow? signIn = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _signIn = signIn;
        IsConfigured = runtime.IsConfigured;
        _snapshot = runtime.Settings.Load();
        Refresh();
    }

    /// <summary>云端能力是否装配成功。false 时整页只显示「尚未接入」。</summary>
    public bool IsConfigured { get; }

    /// <summary>宿主各应用共用同一份装配结果，避免各自 new 出第二套服务。</summary>
    public CloudRuntime Runtime => _runtime;

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _cloudEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = NotConfiguredStatus;
    [ObservableProperty] private string _accountText = "尚未登录";
    [ObservableProperty] private bool _consentAccepted;
    [ObservableProperty] private bool _showConsentPrompt;
    [ObservableProperty] private IReadOnlyList<SharedRosterRow> _rosters = Array.Empty<SharedRosterRow>();
    [ObservableProperty] private string _settingsSaveWarning = "";

    public bool HasRosters => Rosters.Count > 0;

    public bool HasSettingsSaveWarning => !string.IsNullOrEmpty(SettingsSaveWarning);

    partial void OnSettingsSaveWarningChanged(string value)
        => OnPropertyChanged(nameof(HasSettingsSaveWarning));

    /// <summary>云端名单读 / 删入口：未配置、未登录或用户关掉同步时一律不可用。</summary>
    public bool CanAccessCloud => IsConfigured && IsSignedIn && CloudEnabled;

    partial void OnRostersChanged(IReadOnlyList<SharedRosterRow> value)
        => OnPropertyChanged(nameof(HasRosters));

    partial void OnConsentAcceptedChanged(bool value) => UpdateConsentPrompt();

    private void UpdateConsentPrompt() => ShowConsentPrompt = !ConsentAccepted && IsConfigured;

    private void PersistSettings()
    {
        var saved = _runtime.Settings.TrySave(_snapshot);
        SettingsSaveWarning = saved ? "" : SettingsSaveFailedWarning;
    }

    private void CheckSettingsLoadWarning()
    {
        if (!string.IsNullOrEmpty(_runtime.Settings.LastError))
            SettingsSaveWarning = FileCloudSyncSettingsStore.SettingsLoadFailedReason;
    }

    /// <summary>只读本机偏好与账号状态，不联网、不列名单。</summary>
    public void Refresh()
    {
        _snapshot = _runtime.Settings.Load();
        CheckSettingsLoadWarning();
        SetCloudEnabledSilently(_snapshot.Enabled);
        ConsentAccepted = _snapshot.ConsentVersion >= CurrentConsentVersion;
        UpdateConsentPrompt();

        var account = _runtime.Account?.Current;
        IsSignedIn = account is not null;
        AccountText = account is null ? "尚未登录" : account.DisplayName;
        OnPropertyChanged(nameof(CanAccessCloud));

        // 只订阅一次：账号服务与开关通知器都是长生命周期单例，重复订阅会多次刷新或泄漏。
        if (!_subscribed && !_disposed)
        {
            if (_runtime.Account is { } service)
                service.StateChanged += OnAccountStateChanged;
            _runtime.Notifier.StateChanged += OnAccountStateChanged;
            _subscribed = true;
        }

        // 登录在途时账号服务会回调 Refresh；此时默认状态会盖掉「正在打开浏览器」的提示，
        // 界面又变回「点了没反应」。终态由 BeginSignInAsync 自己写。
        if (!IsBusy)
            UpdateStatusText();
    }

    /// <summary>
    /// 登录前必须已经看过字段说明。这道闸在开浏览器之前——
    /// 否则「同意」就变成事后补手续，等于没有。
    /// </summary>
    public async Task BeginSignInAsync(Window owner)
    {
        if (!IsConfigured || _runtime.Account is null || _signIn is null)
        {
            StatusText = NotConfiguredStatus;
            return;
        }
        if (!ConsentAccepted)
        {
            StatusText = CloudErrorText.ConsentRequiredDetailed;
            return;
        }
        if (IsBusy) return;

        IsBusy = true;
        var cts = BeginOps();
        try
        {
            StatusText = CloudErrorText.SignInWaitingBrowser;
            var outcome = await _signIn.SignInAsync(owner, cts.Token);
            Refresh();
            StatusText = outcome.Message;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = CloudErrorText.SignInCancelled;
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe(ex, "登录");
        }
        finally
        {
            IsBusy = false;
            EndOps(cts);
        }
    }

    public async Task SignOutAsync()
    {
        if (!IsConfigured || _runtime.Account is null)
        {
            StatusText = NotConfiguredStatus;
            return;
        }
        if (IsBusy) return;

        IsBusy = true;
        var cts = BeginOps();
        try
        {
            await _runtime.Account.SignOutAsync(cts.Token);
            var notice = _runtime.Account.LastSignOutNotice;

            // 退出即关同步：否则状态栏会出现「未登录 · 云同步关闭」而开关还亮着的鬼画面。
            // 同时清掉云端文档身份：留着上一人的 id / revision，下一位登录后会拿它去发布。
            _snapshot = _runtime.Settings.Load() with
            {
                Enabled = false,
                LastDocumentId = null,
                LastDocumentRevision = 0,
            };
            PersistSettings();
            Rosters = Array.Empty<SharedRosterRow>();
            Refresh();
            // 退出同样关掉同步：已经打开的模块窗口必须立刻失去云端能力。
            RaiseNotifierSafely();
            StatusText = "已退出登录。" + (notice is null ? "" : " " + notice);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = CloudErrorText.SignInCancelled;
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe(ex, "退出登录");
        }
        finally
        {
            IsBusy = false;
            EndOps(cts);
        }
    }

    /// <summary>
    /// 界面双向绑定写入 CloudEnabled 时走这里：与 SetCloudEnabled 共用同一条落盘 / 状态 / 广播路径。
    /// 程序性赋值（Refresh、走查布景）由 _applyingCloudEnabled 挡住，不会重复保存或广播。
    /// </summary>
    partial void OnCloudEnabledChanged(bool value)
    {
        if (_applyingCloudEnabled) return;
        ApplyCloudEnabled(value);
    }

    private void SetCloudEnabledSilently(bool value)
    {
        _applyingCloudEnabled = true;
        try { CloudEnabled = value; }
        finally { _applyingCloudEnabled = false; }
    }

    /// <summary>开关云同步。关掉时立刻取消在途的名单读取，避免关了还在拉。</summary>
    public void SetCloudEnabled(bool value) => ApplyCloudEnabled(value);

    private void ApplyCloudEnabled(bool value)
    {
        if (!IsConfigured)
        {
            StatusText = NotConfiguredStatus;
            return;
        }
        if (!value) CancelOps();

        // 写回前重读最新偏好：主窗口抽屉与子窗口各自持有 AccountViewModel，防止覆盖另一侧刚保存的 ConsentVersion。
        _snapshot = _runtime.Settings.Load() with { Enabled = value };
        PersistSettings();
        SetCloudEnabledSilently(value);
        OnPropertyChanged(nameof(CanAccessCloud));
        UpdateStatusText();
        RaiseNotifierSafely();
    }

    /// <summary>记录用户已读过字段说明。只写本机偏好，不联网。</summary>
    public void AcceptConsent()
    {
        if (!IsConfigured)
        {
            StatusText = NotConfiguredStatus;
            return;
        }
        _snapshot = _runtime.Settings.Load() with { ConsentVersion = CurrentConsentVersion };
        PersistSettings();
        ConsentAccepted = true;
        UpdateStatusText();
        RaiseNotifierSafely();
    }

    public async Task RefreshRostersAsync()
    {
        if (CloudAccessBlocked()) return;
        if (_runtime.Rosters is null)
        {
            SetEmptyRosters();
            return;
        }
        if (IsBusy) return;

        IsBusy = true;
        var cts = BeginOps();
        try
        {
            await LoadRostersAsync(cts.Token, announce: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 被更新的操作（如退出登录）顶掉时状态由那个操作负责，别用「已取消读取」盖掉它。
            if (ReferenceEquals(_opsCts, cts))
                StatusText = "已取消读取共享名单。";
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe(ex, "读取共享名单");
        }
        finally
        {
            IsBusy = false;
            EndOps(cts);
        }
    }

    public async Task DeleteRosterAsync(string id)
    {
        if (CloudAccessBlocked()) return;
        if (_runtime.Rosters is null || string.IsNullOrWhiteSpace(id)) return;
        if (IsBusy) return;

        IsBusy = true;
        var cts = BeginOps();
        try
        {
            await _runtime.Rosters.DeleteAsync(id, cts.Token);
            await LoadRostersAsync(cts.Token, announce: false);
            StatusText = "已删除这份共享名单。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (ReferenceEquals(_opsCts, cts))
                StatusText = "已取消删除共享名单。";
        }
        catch (Exception ex)
        {
            StatusText = ErrorText.Describe(ex, "删除共享名单");
        }
        finally
        {
            IsBusy = false;
            EndOps(cts);
        }
    }

    /// <summary>走查布景：只改展示状态，不碰任何服务、不发请求。</summary>
    internal void ApplyCaptureState(string state)
    {
        switch (state)
        {
            case "consent":
                ConsentAccepted = false;
                StatusText = CloudErrorText.ConsentRequiredDetailed;
                break;
            case "signedin":
                IsSignedIn = true;
                AccountText = "示例教师";
                SetCloudEnabledSilently(true);
                StatusText = $"已登录：{AccountText} · 云同步已开启（仅手动上传 / 拉取）";
                Rosters = new[]
                {
                    new SharedRosterRow("demo-roster", "八年级1班名单", 3, 42,
                        "姓名、性别、学号、身高", "2026-11-24 09:12"),
                };
                break;
            case "error":
                StatusText = ErrorText.Describe(
                    new CloudAuthException(CloudErrorText.Timeout), "读取共享名单");
                break;
            default:
                IsSignedIn = false;
                AccountText = "尚未登录";
                SetCloudEnabledSilently(false);
                StatusText = IsConfigured ? SignedOutStatus : NotConfiguredStatus;
                break;
        }
        OnPropertyChanged(nameof(HasRosters));
        OnPropertyChanged(nameof(CanAccessCloud));
    }

    private async Task LoadRostersAsync(CancellationToken ct, bool announce)
    {
        if (_runtime.Rosters is null)
        {
            SetEmptyRosters();
            return;
        }

        var list = await _runtime.Rosters.ListAsync(ct);
        Rosters = list.Select(ToRow).ToArray();
        OnPropertyChanged(nameof(HasRosters));
        if (announce)
            StatusText = HasRosters ? $"共享名单：{Rosters.Count} 份" : "还没有共享名单。";
    }

    private void SetEmptyRosters()
    {
        Rosters = Array.Empty<SharedRosterRow>();
        OnPropertyChanged(nameof(HasRosters));
    }

    /// <summary>云端访问闸门：未配置、未登录或用户关掉同步时不碰网络。</summary>
    private bool CloudAccessBlocked()
    {
        if (!IsConfigured)
        {
            StatusText = NotConfiguredStatus;
            return true;
        }
        // 未登录时没有可用凭据，按钮也不该还能点；桥层虽会挡住传输，界面先如实拦下。
        if (!IsSignedIn)
        {
            StatusText = SignedOutStatus;
            return true;
        }
        // 已登录但用户主动关掉同步：这个场景会带着有效凭据发出请求，必须拦。
        if (!CloudEnabled)
        {
            StatusText = CloudErrorText.CloudSyncOff;
            return true;
        }
        return false;
    }

    private static SharedRosterRow ToRow(SharedRosterInfo info) => new(
        info.RosterId,
        info.Name,
        info.Version,
        info.StudentCount,
        DescribeFields(info.Fields),
        info.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

    private static string DescribeFields(RosterFieldPresence fields)
    {
        var labels = new List<string>(CloudConsentText.RequiredFieldNames);
        labels.AddRange(fields.PresentFieldNames.Select(name => name switch
        {
            "height" => "身高",
            "vision" => "视力",
            "score" => "成绩",
            "notes" => "备注",
            "originalGroup" => "原小组",
            _ => name,
        }));
        return string.Join("、", labels);
    }

    private void UpdateStatusText()
    {
        StatusText = !IsConfigured
            ? NotConfiguredStatus
            : !IsSignedIn
                ? SignedOutStatus
                : CloudEnabled
                    ? $"已登录：{AccountText} · 云同步已开启（仅手动上传 / 拉取）"
                    : $"已登录：{AccountText} · 云同步已关闭";
    }

    private void RaiseNotifierSafely()
    {
        _raisingNotifier = true;
        try { _runtime.Notifier.Raise(); }
        finally { _raisingNotifier = false; }
    }

    private void OnAccountStateChanged(object? sender, EventArgs e)
    {
        // 本实例刚刚主动保存并更新完内存状态后发出的广播，不再对自己执行 Refresh()，
        // 避免使用 NoopCloudSyncSettingsStore 时把刚写入的内存状态冲回默认值。
        if (_raisingNotifier && ReferenceEquals(sender, _runtime.Notifier)) return;
        RunOnUi(() =>
        {
            if (_disposed) return;
            // 凭据失效 / 被撤销后服务已登出，但设置里 Enabled 还留着 true：
            // 不在这里关掉，Refresh() 会把「未登录 · 云同步关闭」和亮着的开关一起显示出来。
            // 只落盘、不广播：广播会再次回到这里，形成环。
            if (_runtime.Account is { IsSignedIn: false } && CloudEnabled)
            {
                _snapshot = _runtime.Settings.Load() with { Enabled = false };
                PersistSettings();
                SetCloudEnabledSilently(false);
            }
            Refresh();
            OnPropertyChanged(nameof(CanAccessCloud));
        });
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(action);
            return;
        }
        action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed)
        {
            if (_runtime.Account is { } service)
                service.StateChanged -= OnAccountStateChanged;
            _runtime.Notifier.StateChanged -= OnAccountStateChanged;
            _subscribed = false;
        }
        CancelOps();
    }

    private CancellationTokenSource BeginOps()
    {
        CancelOps();
        _opsCts = new CancellationTokenSource();
        return _opsCts;
    }

    private void CancelOps()
    {
        var cts = _opsCts;
        _opsCts = null;
        // 只取消不释放：在途操作还拿着这个 token，等它自己的 finally 再 Dispose。
        cts?.Cancel();
    }

    private void EndOps(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_opsCts, cts)) _opsCts = null;
        cts.Dispose();
    }
}
