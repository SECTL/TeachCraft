namespace TeachCraft.Tests;

/// <summary>
/// Release A 的结构契约：构建身份、发布管线、安装包身份记录、发布策略文案。
/// 这些断言只读仓库文本，不跑网络、不碰真实注册表，因此可以安全地在 CI 里跑。
/// 对应 .omo/plans/app-update.md 的 todo 2 / todo 3 / todo 4 / todo 17。
/// </summary>
public sealed class ReleaseIdentityTests
{
    // ---------- todo 17：版本基线 ----------
    // 版本号断言统一放在 VersionBaselineTests：那里从 Directory.Build.props 读，
    // 不写死版本，晋级时无需改测试。这里只保留身份串相关的契约。

    [Fact]
    public void Version_SourceRevisionIsNotAppendedToInformationalVersion()
    {
        // SDK 默认会把 git sha 追加进位身份串（…+<sha>），那会让严格解析全部退化成「无身份」。
        var props = RepoRoot.Read("Directory.Build.props");
        Assert.Contains("<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>", props);
    }

    [Fact]
    public void Version_IsNotHardcodedInWorkflowOrInstallerScript()
    {
        Assert.DoesNotContain("1.17.0", RepoRoot.Read(".github/workflows/release-draft.yml"));
        Assert.DoesNotContain("1.17.0", RepoRoot.Read("build-installer.ps1"));
    }

    // ---------- todo 2：构建身份注入 ----------

    [Fact]
    public void Ci_InjectsInformationalVersionForWindowsAndLinux()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("InformationalVersion", wf);
        Assert.Contains("RID=", wf);
        Assert.Contains("BUILD_IDENTITY", wf);
    }

    [Fact]
    public void Ci_PublishesChecksumManifestAsReleaseAsset()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("SHA256SUMS", wf);
        // files: 目前只有 *.zip/*.exe/*.dmg，.txt 不加进去就不会被上传，
        // 客户端的「清单缺失即失败关闭」会变成无条件失败。
        Assert.Contains("release-assets/*SHA256SUMS.txt", wf);
    }

    [Fact]
    public void Mac_PlacesTwoDistinctVersionPlaceholders()
    {
        var plist = RepoRoot.Read("bundle/macos/Info.plist");
        Assert.Contains("__BUNDLE_VERSION__", plist);
        Assert.Contains("__SHORT_VERSION__", plist);
        // 两个键必须不同：CFBundleVersion 只允许点分整数，不能塞预览段。
        var bundleIdx = plist.IndexOf("__BUNDLE_VERSION__", StringComparison.Ordinal);
        var shortIdx = plist.IndexOf("__SHORT_VERSION__", StringComparison.Ordinal);
        Assert.True(bundleIdx >= 0 && shortIdx >= 0 && bundleIdx != shortIdx);
    }

    [Fact]
    public void Mac_DoesNotStripPreviewSuffixOrInjectIdentityViaStalePath()
    {
        var sh = RepoRoot.Read("bundle/macos/build-dmg.sh");
        Assert.Contains("__BUNDLE_VERSION__", sh);
        Assert.Contains("__SHORT_VERSION__", sh);
        Assert.Contains("-p:InformationalVersion=", sh);
        // 旧写法把预览后缀整段剥掉，导致同一个 base 版本的两个预览在 bundle 里完全同形。
        Assert.DoesNotContain("PLIST_VER=\"${CLEAN_VER%%-*}\"", sh);
    }

    // ---------- todo 3：安装包身份记录 ----------

    [Fact]
    public void Installer_WritesRegistryIdentityIn64BitView()
    {
        var iss = RepoRoot.Read("installer/TeachCraft.iss");
        Assert.Contains("[Registry]", iss);
        // arm64 的 ArchitecturesInstallIn64BitMode 会把默认视图变成 32 位，
        // 写 HKCU 会让 x64/arm64 的键读不到。
        Assert.Contains("HKCU64", iss);
    }

    [Fact]
    public void Installer_UsesPerSkuSubkeyWithCompileTimeConstants()
    {
        var iss = RepoRoot.Read("installer/TeachCraft.iss");
        Assert.Contains("Install-{#MyAppMode}-{#Arch}", iss);
        Assert.Contains("{#MyRid}", iss);
        // Inno 文档说明 {code:} 在 Subkey 参数里不受支持，因此这里必须是编译期常量。
        var registrySection = iss[iss.IndexOf("[Registry]", StringComparison.Ordinal)..];
        Assert.DoesNotContain("{code:", registrySection);
    }

    [Fact]
    public void Installer_DoesNotHardcodeX64SourceDirTemplate()
    {
        // 回退值写死 win-x64-sc 时，-Rid win-arm64 会打包 x64 的树。
        Assert.DoesNotContain("dist/TeachCraft-win-x64-sc", RepoRoot.Read("installer/TeachCraft.iss"));
        Assert.DoesNotContain("dist\\TeachCraft-win-x64-sc", RepoRoot.Read("installer/TeachCraft.iss"));
    }

    [Fact]
    public void InstallerScript_PassesModeAndRid()
    {
        var script = RepoRoot.Read("build-installer.ps1");
        Assert.Contains("/DMyAppMode=", script);
        Assert.Contains("/DMyRid=", script);
        Assert.Contains("/DMyAppVersion=$Version", script);
    }

    [Fact]
    public void Ci_EnsuresInnoSetupBeforeBuildingInstallers()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("Ensure Inno Setup", wf);
        Assert.Contains("innosetup", wf);
    }

    [Fact]
    public void Startup_CleansStalePartialDownloads()
    {
        // 进程被强杀时 finally 不执行，.part 会一直留到下次点更新才清。
        var app = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "App.axaml.cs"));
        Assert.Contains("CleanupStaleParts", app);
    }

    [Fact]
    public void Ci_RequiresMacPortablePackageInEveryRelease()
    {
        // macOS 便携用户只能靠 -mac.zip 更新；少了它那群人会被判「暂未提供」。
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("-mac.zip", wf);
        Assert.Contains("Verify macOS Portable Package Present", wf);
    }

    // ---------- todo 4：发布策略与文案 ----------

    [Fact]
    public void Release_NoLongerDemotesHistoricalPreviews()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.DoesNotContain("Demote Previous Preview", wf);
        Assert.DoesNotContain("自动转为草稿", wf);
        // prerelease 标记本身必须保留，且只应有一处（精确匹配而不是计数）。
        var lines = wf.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("prerelease: ${{ env.IS_RELEASE != 'true' }}", lines);
    }

    [Fact]
    public void Release_DocsMatchTheNewPolicy()
    {
        Assert.DoesNotContain("自动转为 Draft", RepoRoot.Read("AGENTS.md"));
        var mem05 = RepoRoot.Read(".memory/05-构建发布与版本号.md");
        Assert.DoesNotContain("自动转 Draft", mem05);
        Assert.DoesNotContain("降级旧预览", mem05);
        Assert.DoesNotContain("只使用数字段", mem05);
    }

    [Fact]
    public void Release_BodyStatesCheckUpdateIsTheOnlyNetworkUse()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("仅「检查更新」会联网", wf);
    }
}
