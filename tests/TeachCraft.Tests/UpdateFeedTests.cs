using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// GitHub Releases 归一化与分支选择。
/// 关键点：排序靠语义版本而不是 created_at（历史 tag 是非连续的），
/// 正式版用户只看正式版，预览版用户看「更高的预览 + 更高的正式版」。
/// </summary>
public sealed class UpdateFeedTests
{
    private static BuildIdentity Release()
        => BuildIdentity.Parse("1.17.0.sc.win-x64");

    private static BuildIdentity Preview(int n)
        => BuildIdentity.Parse($"1.17.0-preview.{n}.sc.win-x64");

    private const string Json = """
    [
      { "tag_name": "v1.2.1-preview.5", "draft": false, "prerelease": true,
        "published_at": "2026-10-23T00:00:00Z", "assets": [] },
      { "tag_name": "v1.17.0", "draft": false, "prerelease": false,
        "published_at": "2026-11-01T00:00:00Z",
        "assets": [ { "name": "TeachCraft-v1.17.0-win-x64-sc-Setup.exe", "size": 120000000,
                      "browser_download_url": "https://example/v1.17.0/win-x64-sc.exe" } ] },
      { "tag_name": "v1.10.2-preview.8", "draft": false, "prerelease": true,
        "published_at": "2026-10-27T00:00:00Z", "assets": [] },
      { "tag_name": "v1.17.0-preview.142", "draft": false, "prerelease": true,
        "published_at": "2026-11-02T00:00:00Z",
        "assets": [ { "name": "TeachCraft-v1.17.0-preview.142-win-x64-sc-Setup.exe", "size": 121000000,
                      "browser_download_url": "https://example/preview.142/win-x64-sc.exe" } ] },
      { "tag_name": "v1.18.0-preview.1", "draft": true, "prerelease": true,
        "published_at": "2026-11-03T00:00:00Z", "assets": [] }
    ]
    """;

    [Fact]
    public void Parse_ReadsAllEntries()
    {
        var entries = UpdateFeed.Parse(Json).ToList();

        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, e => e.TagName == "v1.17.0" && !e.IsPrerelease);
        Assert.Contains(entries, e => e.TagName == "v1.17.0-preview.142" && e.IsPrerelease);
    }

    [Fact]
    public void Parse_ExcludesDrafts()
    {
        var entries = UpdateFeed.Parse(Json).ToList();

        Assert.DoesNotContain(entries, e => e.TagName == "v1.18.0-preview.1");
    }

    [Fact]
    public void Parse_SkipsMalformedTagsWithoutThrowing()
    {
        const string json = """
        [ { "tag_name": "not-a-version", "draft": false, "prerelease": false, "assets": [] },
          { "tag_name": "v1.17.0", "draft": false, "prerelease": false, "assets": [] } ]
        """;

        var entries = UpdateFeed.Parse(json).ToList();

        Assert.Single(entries);
        Assert.Equal("v1.17.0", entries[0].TagName);
    }

    [Fact]
    public void Ordering_UsesSemanticVersionNotPublicationDate()
    {
        var entries = UpdateFeed.Parse(Json).ToList();

        // v1.10.2-preview.8 的 published_at 比 v1.2.1-preview.5 晚，但版本更低。
        var ordered = UpdateFeed.OrderNewestFirst(entries).ToList();
        var tags = ordered.Select(e => e.TagName).ToList();

        Assert.Equal("v1.17.0", tags[0]);
        Assert.Equal("v1.17.0-preview.142", tags[1]);
        Assert.True(tags.IndexOf("v1.10.2-preview.8") < tags.IndexOf("v1.2.1-preview.5"),
            "非连续的历史 tag 必须按版本排序，不能按时间");
    }

    [Fact]
    public void PreviewDoesNotOutrankSameNumberedRelease()
    {
        var release = UpdateFeed.Parse("""[{"tag_name":"v1.17.0","draft":false,"prerelease":false,"assets":[]}]""").Single();
        var preview = UpdateFeed.Parse("""[{"tag_name":"v1.17.0-preview.9","draft":false,"prerelease":true,"assets":[]}]""").Single();

        var ordered = UpdateFeed.OrderNewestFirst(new[] { preview, release }).ToList();

        Assert.Equal("v1.17.0", ordered[0].TagName);
    }

    [Fact]
    public void ReleaseInstall_OnlySeesStableReleases()
    {
        var entries = UpdateFeed.Parse(Json).ToList();
        // 当前装在 1.16.0 正式版，应只看到 1.17.0 正式版，看不到任何预览。
        var installed = BuildIdentity.Parse("1.16.0.sc.win-x64");
        var candidates = UpdateFeed.SelectCandidates(entries, installed);

        Assert.All(candidates, c => Assert.False(c.IsPrerelease, "正式版用户不应看到预览版"));
        Assert.Single(candidates);
        Assert.Equal("v1.17.0", candidates[0].TagName);
    }

    [Fact]
    public void PreviewInstall_SeesHigherPreviewAndHigherRelease()
    {
        var entries = UpdateFeed.Parse(Json).ToList();
        // 当前装在 preview.1，应能看到 preview.142（更高预览）与 1.17.0（更高正式版）。
        var candidates = UpdateFeed.SelectCandidates(entries, Preview(1));

        var tags = candidates.Select(c => c.TagName).ToList();
        Assert.Contains("v1.17.0-preview.142", tags);
        Assert.Contains("v1.17.0", tags);
    }

    [Fact]
    public void PreviewInstall_DoesNotSeeLowerPreviews()
    {
        var entries = UpdateFeed.Parse(Json).ToList();
        var candidates = UpdateFeed.SelectCandidates(entries, Preview(142));

        Assert.DoesNotContain(candidates, c => c.TagName == "v1.2.1-preview.5");
        Assert.DoesNotContain(candidates, c => c.TagName == "v1.10.2-preview.8");
    }

    [Fact]
    public void UpToDate_WhenNoCandidateIsHigher()
    {
        var entries = UpdateFeed.Parse(Json).ToList();
        var current = BuildIdentity.Parse("1.17.0.sc.win-x64");

        var best = UpdateFeed.BestCandidate(entries, current);

        Assert.Null(best);
    }

    [Fact]
    public void BestCandidate_PrefersHighestVersion()
    {
        var entries = UpdateFeed.Parse(Json).ToList();
        var best = UpdateFeed.BestCandidate(entries, Preview(1));

        Assert.NotNull(best);
        Assert.Equal("v1.17.0", best!.TagName);
    }

    [Fact]
    public void Parse_HandlesEmptyArray()
    {
        Assert.Empty(UpdateFeed.Parse("[]"));
    }
}
