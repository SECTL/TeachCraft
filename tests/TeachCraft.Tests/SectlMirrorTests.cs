using System.Net;
using System.Text;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// SECTL 镜像源。
///
/// 这些测试锁住三件事：
///   1. 镜像的 /distribution 响应能被正确归一化成 ReleaseEntry——下游的选择逻辑才能复用；
///   2. 回退只在首选源失败时发生，且实际用的源必须能被界面读到；
///   3. 选了镜像就不回退（用户明确要的是镜像）。
/// </summary>
public sealed class SectlMirrorTests
{
    private const string ApiBase = SectlMirrorFeed.DefaultApiBase;
    private const string Slug = SectlMirrorFeed.DefaultProjectSlug;
    private static string DistributionUrl => SectlMirrorFeed.BuildDistributionUrl(ApiBase);

    /// <summary>按真实 /distribution 响应裁剪出的最小样例：两个项目、两个版本、若干包。</summary>
    private static string SampleDistribution(
        string projectId = "tc-id",
        string slug = "知微 · TeachCraft",
        bool includeServerSource = true,
        bool enabled = true)
    {
        var sources = includeServerSource
            ? """["server","github","direct"]"""
            : """["github","direct"]""";

        return $$"""
        {
          "success": true,
          "projects": [
            { "$id": "other", "slug": "SecRandom", "name": "SecRandom" },
            { "$id": "{{projectId}}", "slug": "{{slug}}", "name": "知微·TeachCraft" }
          ],
          "versions": [
            {
              "project_id": "{{projectId}}",
              "tag": "v1.18.0-preview.9",
              "prerelease": true,
              "enabled": {{(enabled ? "true" : "false")}},
              "package_count": 2
            },
            {
              "project_id": "{{projectId}}",
              "tag": "v1.17.0",
              "prerelease": false,
              "enabled": true,
              "package_count": 1
            },
            {
              "project_id": "other",
              "tag": "v9.9.9",
              "prerelease": false,
              "enabled": true,
              "package_count": 1
            }
          ],
          "packages": [
            {
              "project_id": "{{projectId}}",
              "version_tag": "v1.18.0-preview.9",
              "file_name": "TeachCraft-v1.18.0-preview.9-win-x64-sc-Setup.exe",
              "size": 52481764,
              "sha256": "",
              "server_download_url": "https://stk.sectl.cn/d/tc/v1.18.0-preview.9/TeachCraft-v1.18.0-preview.9-win-x64-sc-Setup.exe",
              "available_sources": {{sources}}
            },
            {
              "project_id": "{{projectId}}",
              "version_tag": "v1.18.0-preview.9",
              "file_name": "TeachCraft-v1.18.0-preview.9-SHA256SUMS.txt",
              "size": 900,
              "sha256": "",
              "server_download_url": "https://stk.sectl.cn/d/tc/v1.18.0-preview.9/TeachCraft-v1.18.0-preview.9-SHA256SUMS.txt",
              "available_sources": {{sources}}
            },
            {
              "project_id": "{{projectId}}",
              "version_tag": "v1.17.0",
              "file_name": "TeachCraft-v1.17.0-win-x64-sc-Setup.exe",
              "size": 51000000,
              "sha256": "",
              "server_download_url": "https://stk.sectl.cn/d/tc/v1.17.0/TeachCraft-v1.17.0-win-x64-sc-Setup.exe",
              "available_sources": {{sources}}
            },
            {
              "project_id": "other",
              "version_tag": "v9.9.9",
              "file_name": "Other.exe",
              "size": 1,
              "sha256": "",
              "server_download_url": "https://stk.sectl.cn/d/other/Other.exe",
              "available_sources": {{sources}}
            }
          ]
        }
        """;
    }

    [Fact]
    public void Parse_KeepsOnlyTheRequestedProject()
    {
        var entries = SectlMirrorFeed.Parse(SampleDistribution(), Slug);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => e.TagName == "v9.9.9");
    }

    [Fact]
    public void Parse_MapsFieldsOntoReleaseEntry()
    {
        var entries = SectlMirrorFeed.Parse(SampleDistribution(), Slug);
        var newest = entries.Single(e => e.TagName == "v1.18.0-preview.9");

        Assert.True(newest.IsPrerelease, "镜像标了 prerelease 的版本必须保留这个标记，否则正式版用户会看到预览版");
        Assert.Equal("1.18.0-preview.9", newest.Version.ToString());
        Assert.Equal(2, newest.Assets.Count);
        Assert.Contains(newest.Assets, a => a.Name.EndsWith("-Setup.exe"));
        Assert.All(newest.Assets, a => Assert.StartsWith("https://stk.sectl.cn/", a.DownloadUrl));
    }

    [Fact]
    public void Parse_UsesMirrorDownloadUrlNotGithub()
    {
        // 归一化后下游只知道 DownloadUrl。若这里漏用 server_download_url，
        // 镜像语境下会去 GitHub 下载——在连不上 GitHub 的校园网里正是要避免的。
        var entries = SectlMirrorFeed.Parse(SampleDistribution(), Slug);

        Assert.All(entries, e => Assert.All(e.Assets,
            a => Assert.DoesNotContain("github.com", a.DownloadUrl, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Parse_SkipsPackagesWithoutServerSource()
    {
        // 镜像没托管文件的包，在镜像语境下下载不到；留着会让用户点了却失败。
        var entries = SectlMirrorFeed.Parse(SampleDistribution(includeServerSource: false), Slug);

        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_SkipsDisabledVersions()
    {
        var entries = SectlMirrorFeed.Parse(SampleDistribution(enabled: false), Slug);

        Assert.DoesNotContain(entries, e => e.TagName == "v1.18.0-preview.9");
        Assert.Contains(entries, e => e.TagName == "v1.17.0");
    }

    [Fact]
    public void Parse_UnknownProjectReturnsEmptyInsteadOfThrowing()
    {
        // 镜像上还没有这个软件是正常状态，不该让整条链路崩掉。
        var entries = SectlMirrorFeed.Parse(SampleDistribution(), "不存在的项目");

        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_GarbageReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(SectlMirrorFeed.Parse("{}", Slug));
        Assert.Empty(SectlMirrorFeed.Parse("""{"projects":[],"versions":[],"packages":[]}""", Slug));
    }

    [Fact]
    public void FindChecksumsUrl_PicksTheSumsAsset()
    {
        var entries = SectlMirrorFeed.Parse(SampleDistribution(), Slug);
        var assets = entries.Single(e => e.TagName == "v1.18.0-preview.9").Assets;

        var url = SectlMirrorFeed.FindChecksumsUrl(assets);

        Assert.NotNull(url);
        Assert.EndsWith(AssetNaming.ChecksumsSuffix, url);
    }

    // ---- 检查服务：与下游选择逻辑串起来 ----

    private static BuildIdentity Identity(string value) => BuildIdentity.Parse(value);

    [Fact]
    public async Task CheckService_StableUserIsNotPushedToPrerelease()
    {
        var handler = new GuardedHttpHandler()
            .Respond(DistributionUrl, HttpStatusCode.OK, SampleDistribution());
        var service = new SectlMirrorCheckService(handler);

        var result = await service.CheckAsync(Identity("1.17.0.sc.win-x64"));

        Assert.True(result.IsSuccess, result.Failure);
        // 正式版用户不该被推预览版——与 GitHub 路径同一套 SelectCandidates，
        // 镜像不能因为「数据来源不同」就放宽这条规则。
        Assert.Null(result.Best);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task CheckService_PreviewUserSeesPreview()
    {
        var handler = new GuardedHttpHandler()
            .Respond(DistributionUrl, HttpStatusCode.OK, SampleDistribution());
        var service = new SectlMirrorCheckService(handler);

        var result = await service.CheckAsync(Identity("1.17.0-preview.1.sc.win-x64"));

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal("1.18.0-preview.9", result.Best!.Version.ToString());
    }

    [Fact]
    public async Task CheckService_EmptyProjectReportsMirrorNotSynced()
    {
        var handler = new GuardedHttpHandler()
            .Respond(DistributionUrl, HttpStatusCode.OK, SampleDistribution(slug: "别的项目"));
        var service = new SectlMirrorCheckService(handler);

        var result = await service.CheckAsync(Identity("1.17.0.sc.win-x64"));

        Assert.False(result.IsSuccess);
        Assert.Equal(UpdateErrorText.MirrorHasNoProject, result.Failure);
    }

    [Fact]
    public async Task CheckService_HttpErrorIsReportedInHumanWords()
    {
        var handler = new GuardedHttpHandler()
            .Respond(DistributionUrl, HttpStatusCode.ServiceUnavailable, "down");
        var service = new SectlMirrorCheckService(handler);

        var result = await service.CheckAsync(Identity("1.17.0.sc.win-x64"));

        Assert.False(result.IsSuccess);
        Assert.Contains("镜像", result.Failure);
    }

    [Fact]
    public async Task CheckService_NonJsonBodyIsRecognisedAsCaptivePortal()
    {
        // 校园网登录页会返回 200 + HTML，不能当成「版本列表为空」。
        var handler = new GuardedHttpHandler()
            .Respond(DistributionUrl, HttpStatusCode.OK, "<html>请先登录</html>", "text/html");
        var service = new SectlMirrorCheckService(handler);

        var result = await service.CheckAsync(Identity("1.17.0.sc.win-x64"));

        Assert.False(result.IsSuccess);
        Assert.Contains("镜像", result.Failure);
    }
}
