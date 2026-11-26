using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 联网口径的守护测试。
///
/// 2026-11-16 起，「不联网 / 不上传」的绝对承诺被废除：接入集控必然联网。
/// 现在的契约是「两处联网，都由用户明确开启」——检查更新与集控。
/// 这条测试的存在意义是：以后有人随手写一句「无需联网」或「从不上传」，
/// 会立刻被打红；同时保证「集控默认关闭」这条不被悄悄改掉。
/// </summary>
public sealed class LocalFirstCopyTests
{
    [Fact]
    public void App_ContainsNoAbsoluteNoNetworkClaim()
    {
        var offenders = RepoRoot
            .ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Where(f => f.Text.Contains("无需联网", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void App_DoesNotClaimNothingIsEverUploaded()
    {
        // 集控会把班级与运行状态报到平台，且未来可能扩展。
        var surface = RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "SeatGrid.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "ClassBlock.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "GradeWeave.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "MeetRoster.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "Homework.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "GradeStrip.App"), ".axaml", ".cs"));

        var offenders = surface
            .Where(f => f.Text.Contains("不上传", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoShippedSurfaceClaimsNothingIsEverStored()
    {
        // 程序会在 %LOCALAPPDATA%\TeachCraft\ 留存更新状态、集控设置与安装包，
        // 任何「不留存」的绝对表述都成了假话。
        var surfaces = new List<(string Path, string Text)>
        {
            ("AGENTS.md", RepoRoot.Read("AGENTS.md")),
            (".memory/README.md", RepoRoot.Read(Path.Combine(".memory", "README.md"))),
            (".github/workflows/release-draft.yml", RepoRoot.Read(Path.Combine(".github", "workflows", "release-draft.yml"))),
        };
        surfaces.AddRange(RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs"));

        var offenders = surfaces
            .Where(f => f.Text.Contains("不留存", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void App_StatesBothNetworkSurfaces()
    {
        var corpus = RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Select(f => f.Text)
            .ToList();

        // 1.45.0 起是三处：更新、账号与共享名单、集控。三处都要在界面文案里点名。
        Assert.Contains(corpus, text => text.Contains("联网仅限更新、账号与集控", StringComparison.Ordinal));
        Assert.Contains(corpus, text => text.Contains("账号与云同步", StringComparison.Ordinal));

        // 这两句必须真的 shipped 在主窗口上，不能只躺在某个 ViewModel 常量里——
        // 否则「界面说清楚了」就是一句空话。
        var mainWindow = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "Views", "MainWindow.axaml"));
        Assert.Contains("联网仅限更新、账号与集控", mainWindow, StringComparison.Ordinal);
        Assert.Contains("账号与云同步", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void WalkthroughWiringCannotConstructCloudServices()
    {
        // 替换掉旧的假守护（只查源码里有没有 "isWalkthrough" 字样）。真正要保证的是：
        // 走查路径经唯一装配点拿到的必须是 CloudRuntime.Disabled，哪怕本机偏好写着
        // Enabled=true——一次截图也不能连到真实平台、写凭据或落设备身份。
        using var dir = new TempDir();
        new FileCloudSyncSettingsStore(dir.Path)
            .Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        var handler = new FakeSectlApiHandler();

        var runtime = CloudWiring.Create(
            isWalkthrough: true,
            clientId: SectlPlatformDefaults.ClientId,
            redirectUri: SectlPlatformDefaults.RedirectUri,
            appDataRoot: dir.Path,
            handler: handler);

        Assert.False(runtime.IsConfigured);
        Assert.Null(runtime.Account);
        Assert.Null(runtime.SeatDocuments);
        Assert.Null(runtime.Rosters);
        Assert.Equal(0, handler.RequestCount);

        // 装配点必须唯一：App 只能经 CloudWiring.Create 拿服务，不能自己 new 一套绕过闸门。
        var app = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "App.axaml.cs"));
        Assert.Contains("CloudWiring.Create", app, StringComparison.Ordinal);
        foreach (var direct in new[]
        {
            "new SectlAccountService", "new SectlCloudDocumentStore",
            "new SectlSharedRosterSource", "new SectlCloudKvClient",
        })
            Assert.DoesNotContain(direct, app, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacySentenceIsConsistentBetweenPanelAndCatalog()
    {
        // 这两处曾经把同一句话各写了一份，改一处会让面板自己前后矛盾。
        var panel = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "Views", "ProjectPanelView.axaml"));
        var catalog = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "ViewModels", "ToolDescriptionCatalog.cs"));

        Assert.Contains("本机处理", panel);
        Assert.Contains("本机处理", catalog);
    }

    [Fact]
    public void StatusBarDefaultAndResetUseTheSameConstant()
    {
        var vm = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "ViewModels", "MainViewModel.cs"));

        // 复位若直接写字面量，用完面板后旧口径会复活。
        Assert.DoesNotContain("SetStatus(\"本机运行", vm);
        Assert.Contains("SetStatus(DefaultStatusText)", vm);
    }

    [Fact]
    public void RecognitionPath_IntroducesNoNetworkEndpoint()
    {
        // 填涂识别必须完全在本机完成：识别路径里不得出现任何网络端点或客户端类型。
        var surface = RepoRoot.ReadAll(Path.Combine("src", "Homework.Core", "Recognition"), ".cs")
            .Select(f => (f.Path, f.Text))
            .ToList();

        // 只扫 BatchWorkspaceViewModel 的识别段，不把同文件里其它功能算进来。
        var vm = RepoRoot.Read(Path.Combine("src", "Homework.App", "BatchWorkspaceViewModel.cs"));
        var start = vm.IndexOf("填涂识别 ---", StringComparison.Ordinal);
        var end = vm.IndexOf("提交 / 放弃 / 导出 ---", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start,
            "未能定位 BatchWorkspaceViewModel 的识别段，测试需随源码更新。");
        surface.Add(("src/Homework.App/BatchWorkspaceViewModel.cs（识别段）", vm[start..end]));

        string[] forbidden = ["http://", "https://", "HttpClient", "WebRequest", "Socket"];
        var offenders = new List<string>();
        foreach (var (path, text) in surface)
            foreach (var token in forbidden)
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{path} 含网络端点标记「{token}」");

        Assert.True(offenders.Count == 0,
            "填涂识别路径不得引入网络端点（本地优先红线）：" + string.Join("；", offenders));
    }

    [Fact]
    public void Docs_DoNotPromiseNoNetworkAtAll()
    {
        // 「除「检查更新」外全程不联网」这类绝对句式正是要消灭的。
        // 注意：断言的是「不联网。」这个完整句式，而不是「不联网」三个字——
        // 「联网只在……不建立连接」是合法表述。
        Assert.DoesNotContain("全程不联网", RepoRoot.Read("AGENTS.md"));

        var mem01 = RepoRoot.Read(Path.Combine(".memory", "01-项目概览.md"));
        Assert.DoesNotContain("全程不联网", mem01);

        // 但必须说明联网发生在哪两处。
        Assert.Contains("检查更新", mem01);
        Assert.Contains("集控", mem01);
    }

    // ---- 1.45.0 云同步 Core 层结构守护（T9a）------------------------------------
    // 这些断言针对的是「默认关闭 / 最小披露 / 无后台连接 / 不写日志」这些结构性事实，
    // 不是「源码里有没有出现某个词」的假守护。走查参数相关的守卫在 T9b 另行替换。

    [Fact]
    public void CloudSync_DefaultsToOff()
    {
        var d = CloudSyncSettings.Default;
        Assert.False(d.Enabled);
        Assert.False(d.LastRosterHeight);
        Assert.False(d.LastRosterVision);
        Assert.False(d.LastRosterScore);
        Assert.False(d.LastRosterNotes);
        Assert.False(d.LastRosterOriginalGroup);

        // 空目录（没有 settings.json）加载出来也必须是全关，不能凭空开启。
        using var dir = new TempDir();
        Assert.False(new FileCloudSyncSettingsStore(dir.Path).Load().Enabled);
    }

    [Fact]
    public void SeatGrid_AutoSync_IsOptInAndWired()
    {
        // 自动保存座位表 / 约束是新增的联网行为：默认必须关闭，且必须在归位侧真正接线。
        Assert.False(CloudSyncSettings.Default.AutoSyncSeatDocument);

        var seatGrid = RepoRoot.ReadAll(Path.Combine("src", "SeatGrid.App"), ".axaml", ".cs")
            .Select(f => f.Text).ToList();
        Assert.Contains(seatGrid, text => text.Contains("AutoSyncSeatDocument", StringComparison.Ordinal));
        Assert.Contains(seatGrid, text => text.Contains("SetAutoSyncEnabled", StringComparison.Ordinal));

        // 偏好必须读写新键，否则开关重启后丢失，或反过来被旧文件悄悄打开。
        var store = RepoRoot.Read(Path.Combine("src", "TeachCraft.Core", "Cloud", "CloudSyncSettings.cs"));
        Assert.Contains("AutoSyncSeatDocument", store, StringComparison.Ordinal);
    }

    [Fact]
    public void RosterFieldSelection_DefaultsToMinimal()
    {
        var s = RosterFieldSelection.Minimal;
        Assert.False(s.Height);
        Assert.False(s.Vision);
        Assert.False(s.Score);
        Assert.False(s.Notes);
        Assert.False(s.OriginalGroup);

        var p = RosterFieldPresence.None;
        Assert.False(p.Height);
        Assert.False(p.Vision);
        Assert.False(p.Score);
        Assert.False(p.Notes);
        Assert.False(p.OriginalGroup);
    }

    [Fact]
    public void CloudConsentText_IsExplicitAndVoluntary()
    {
        var text = RepoRoot.Read(Path.Combine("src", "TeachCraft.Core", "Cloud", "CloudConsentText.cs"));

        // 必填 + 可选字段必须逐一点名，不能含糊地说「部分学生信息」。
        foreach (var field in new[] { "姓名", "性别", "学号", "身高", "视力", "成绩", "备注", "原小组" })
            Assert.Contains(field, text);

        Assert.Contains("未勾选", text);
        Assert.Contains("不会自动上传", text);
        Assert.Contains("完全由你决定", text);
        Assert.Contains("学校的数据管理规定", text);

        // 文件里实际写的是「默认全部关闭」；接受「默认不包含」作为等价表述。
        Assert.True(
            text.Contains("默认不包含", StringComparison.Ordinal) ||
            text.Contains("默认全部关闭", StringComparison.Ordinal),
            "同意文案必须写明可选字段默认关闭");

        // 集控会联网、会留存设置，这些绝对承诺一律不许复活。
        foreach (var banned in new[] { "不上传", "不留存", "无需联网", "全程本机" })
            Assert.DoesNotContain(banned, text);
    }

    [Fact]
    public void CloudLayer_HasNoBackgroundTimersOrSockets()
    {
        // 云同步只在用户主动操作时发请求；不得自带定时器或常驻 socket 把「默认关闭」架空。
        // CancellationTokenSource.CancelAfter 是单次超时保护，不属于后台定时器，不在禁止之列。
        string[] forbidden = ["Timer", "PeriodicTimer", "ClientWebSocket", "Task.Delay("];
        var offenders = new List<string>();
        foreach (var (path, text) in CloudSourceScan.AllCloudSourceFiles())
            foreach (var token in forbidden)
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{path} 含后台连接/定时器标记「{token}」");

        Assert.True(offenders.Count == 0,
            "云同步层不得自带后台定时器或常驻 socket：" + string.Join("；", offenders));
    }

    [Fact]
    public void CloudSources_DoNotLogSecrets()
    {
        // 凭据与响应体不进日志。先禁掉日志 API 本身，再针对日志行单独查凭据——
        // SectlDtos.ToString() 里出现 HasRefreshToken 是脱敏摘要，不是日志，不能误伤。
        string[] logApis = ["Console.", "Debug.", "Trace.", "ILogger"];
        string[] logCallMarkers = ["WriteLine", "Console.", "Debug.", "Trace.", "ILogger", "Log."];
        string[] secretTokens = ["AccessToken", "RefreshToken"];

        var offenders = new List<string>();
        foreach (var (path, text) in CloudSourceScan.AllCloudSourceFiles())
        {
            foreach (var api in logApis)
                if (text.Contains(api, StringComparison.Ordinal))
                    offenders.Add($"{path} 含日志 API「{api}」");

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!logCallMarkers.Any(m => trimmed.Contains(m, StringComparison.Ordinal)))
                    continue;
                foreach (var secret in secretTokens)
                    if (trimmed.Contains(secret, StringComparison.Ordinal))
                        offenders.Add($"{path} 的日志行含凭据「{secret}」：{trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "云同步层不得写日志，更不得把凭据写进日志：" + string.Join("；", offenders));
    }

    [Fact]
    public void CloudSync_IsDefaultOffInWiring()
    {
        // 默认运行时必须是一条「不存在的通道」：未配置、三个服务全为 null。
        // 界面据此显示未接入，而不是自己 new 一个服务补位把默认关闭架空。
        var disabled = CloudRuntime.Disabled;
        Assert.False(disabled.IsConfigured);
        Assert.Null(disabled.Account);
        Assert.Null(disabled.SeatDocuments);
        Assert.Null(disabled.Rosters);
    }

    [Fact]
    public void ModuleMatrixCopyIsHonest()
    {
        var readme = RepoRoot.Read("README.md");

        // 必须如实写明哪几件应用接了云：归位可发布/拉取，辑册只拉取，其余后续接入。
        Assert.Contains(
            "归位可发布、拉取共享名单；辑册可拉取；分晓等其他应用后续接入。",
            readme, StringComparison.Ordinal);

        // 「所有应用都可以…共享名单」这类全覆盖说法一律不许出现。
        Assert.DoesNotContain("所有应用都可以", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeworkExposesNoCloudUpload()
    {
        var bridge = RepoRoot.Read(Path.Combine("src", "Homework.App", "Cloud", "IHomeworkRosterBridge.cs"));

        // 辑册对共享名单只读：桥上不得出现 Upload / Save / Put 方法。
        foreach (var verb in new[] { "Upload", "Save", "Put" })
            Assert.DoesNotContain(verb, bridge, StringComparison.Ordinal);

        // 更不许绕过桥直接持有云文档 / 共享名单源接口。
        var offenders = RepoRoot.ReadAll(Path.Combine("src", "Homework.App"), ".cs")
            .Where(f => f.Text.Contains("ICloudDocumentStore", StringComparison.Ordinal)
                     || f.Text.Contains("ISharedRosterSource", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void RecognitionPath_StillHasNoCloudCalls()
    {
        // 与 RecognitionPath_IntroducesNoNetworkEndpoint 同一文件范围：
        // Homework.Core/Recognition 全部 .cs + BatchWorkspaceViewModel 的识别段。
        var surface = RepoRoot.ReadAll(Path.Combine("src", "Homework.Core", "Recognition"), ".cs")
            .Select(f => (f.Path, f.Text))
            .ToList();

        var vm = RepoRoot.Read(Path.Combine("src", "Homework.App", "BatchWorkspaceViewModel.cs"));
        var start = vm.IndexOf("填涂识别 ---", StringComparison.Ordinal);
        var end = vm.IndexOf("提交 / 放弃 / 导出 ---", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start,
            "未能定位 BatchWorkspaceViewModel 的识别段，测试需随源码更新。");
        surface.Add(("src/Homework.App/BatchWorkspaceViewModel.cs（识别段）", vm[start..end]));

        string[] forbidden = ["Cloud", "ISharedRosterSource", "HttpClient", "Sectl"];
        var offenders = new List<string>();
        foreach (var (path, text) in surface)
            foreach (var token in forbidden)
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{path} 含云同步标记「{token}」");

        Assert.True(offenders.Count == 0,
            "填涂识别路径不得引入云同步调用：" + string.Join("；", offenders));
    }
}
