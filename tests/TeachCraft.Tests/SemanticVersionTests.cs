using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 构建身份与版本比较。整套更新链路都建立在这两样东西上：
/// 版本比错了会提示降级，身份读错了会变成「认不出自己」。
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.17.0", 1, 17, 0, null)]
    [InlineData("1.17.0-preview.142", 1, 17, 0, 142)]
    [InlineData("0.0.1", 0, 0, 1, null)]
    [InlineData("12.34.56-preview.7", 12, 34, 56, 7)]
    public void Parse_ReadsThreePartAndOptionalPreview(string text, int major, int minor, int patch, int? preview)
    {
        Assert.True(SemanticVersion.TryParse(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(preview, v.Preview);
        Assert.Equal(preview is not null, v.IsPreview);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.17")]
    [InlineData("1.17.0.0")]
    [InlineData("not-a-version")]
    [InlineData("1.17.x")]
    [InlineData("1.17.0-preview")]
    [InlineData("v1.17.0")]
    public void TryParse_RejectsMalformed(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Compare_PreviewSortsBelowSameNumberedRelease()
    {
        var preview = SemanticVersion.Parse("1.17.0-preview.9");
        var release = SemanticVersion.Parse("1.17.0");

        Assert.True(preview < release);
        Assert.True(release > preview);
    }

    [Fact]
    public void Compare_PreviewsOrderByNumber()
    {
        var older = SemanticVersion.Parse("1.17.0-preview.9");
        var newer = SemanticVersion.Parse("1.17.0-preview.142");

        Assert.True(newer > older, "预览序号必须按数值比较，不能按字符串比较（9 与 142）");
    }

    [Fact]
    public void Compare_HigherPatchWinsEvenAgainstPreview()
    {
        var nextPatchRelease = SemanticVersion.Parse("1.17.1");
        var currentPreview = SemanticVersion.Parse("1.17.0-preview.142");

        Assert.True(nextPatchRelease > currentPreview);
    }

    [Fact]
    public void Compare_EqualWhenIdentical()
    {
        Assert.Equal(0, SemanticVersion.Parse("1.17.0").CompareTo(SemanticVersion.Parse("1.17.0")));
        Assert.Equal(0, SemanticVersion.Parse("1.17.0-preview.3").CompareTo(SemanticVersion.Parse("1.17.0-preview.3")));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.Equal("1.17.0-preview.142", SemanticVersion.Parse("1.17.0-preview.142").ToString());
        Assert.Equal("1.17.0", SemanticVersion.Parse("1.17.0").ToString());
    }
}

/// <summary>
/// 构建身份解析。身份串形如 <c>1.17.0-preview.142.sc.win-x64</c>，
/// 由 CI 通过 <c>-p:InformationalVersion</c> 注入。
/// </summary>
public sealed class BuildIdentityTests
{
    [Fact]
    public void Parse_ReadsFullIdentity()
    {
        var id = BuildIdentity.Parse("1.17.0-preview.142.sc.win-x64");

        Assert.False(id.IsLegacy);
        Assert.Equal("1.17.0-preview.142", id.Version.ToString());
        Assert.True(id.Version.IsPreview);
        Assert.Equal("sc", id.Mode);
        Assert.Equal("win-x64", id.Rid);
        Assert.Equal("win", id.Platform);
    }

    [Theory]
    [InlineData("1.17.0.sc.win-x64", "win-x64")]
    [InlineData("1.17.0.fdd.osx-arm64", "osx-arm64")]
    [InlineData("1.17.0.sc.linux-arm64", "linux-arm64")]
    [InlineData("1.17.0.fdd.win-arm64", "win-arm64")]
    public void Parse_HandlesRidsContainingHyphens(string text, string expectedRid)
    {
        var id = BuildIdentity.Parse(text);

        Assert.False(id.IsLegacy, $"应能解析：{text}");
        Assert.Equal(expectedRid, id.Rid);
        Assert.False(id.Version.IsPreview);
    }

    [Fact]
    public void Parse_StripsSdkAppendedGitSha()
    {
        // SDK 在 IncludeSourceRevisionInInformationalVersion 为 true 时会追加 +sha 或 .sha，
        // 老版本本机产物就是这个形态，必须能容忍而不是直接退化成「无身份」。
        var id = BuildIdentity.Parse("1.17.0-preview.142.sc.win-x64+90caf2a9f3e7f55");

        Assert.False(id.IsLegacy);
        Assert.Equal("1.17.0-preview.142", id.Version.ToString());
        Assert.Equal("win-x64", id.Rid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.16.0+90caf2a9f3e7f5546450935e80f482ad12ac9e8a")]
    [InlineData("1.16.0")]
    [InlineData("garbage")]
    public void Parse_FallsBackToLegacyWhenIdentityIsMissing(string? text)
    {
        var id = BuildIdentity.Parse(text);

        Assert.True(id.IsLegacy, "认不出的身份必须显式标记为 legacy，而不是猜一个");
    }

    [Fact]
    public void Parse_LegacyFallbackStillYieldsUsableVersion()
    {
        var id = BuildIdentity.Parse("1.16.0+90caf2a9f3", fallbackAssemblyVersion: "1.16.0.0");

        Assert.True(id.IsLegacy);
        // 裸版本是正式版版本号，不能伪造成 -preview.0（那个号码不代表任何真实预览）。
        Assert.Equal("1.16.0", id.Version.ToString());
        Assert.False(id.Version.IsPreview);
    }

    [Fact]
    public void Platform_IsDerivedFromRid()
    {
        Assert.Equal("win", BuildIdentity.Parse("1.17.0.sc.win-x86").Platform);
        Assert.Equal("osx", BuildIdentity.Parse("1.17.0.sc.osx-x64").Platform);
        Assert.Equal("linux", BuildIdentity.Parse("1.17.0.sc.linux-x64").Platform);
    }

    [Fact]
    public void Describe_IncludesPreviewNumberSoTwoPreviewsAreDistinguishable()
    {
        var id = BuildIdentity.Parse("1.17.0-preview.142.sc.win-x64");
        var legacy = BuildIdentity.Parse(null, fallbackAssemblyVersion: "1.16.0.0");

        Assert.Contains("1.17.0-preview.142", id.Describe());
        // 无身份但版本可解析：如实呈现为正式版号，而不是捏一个预览号。
        Assert.Equal("1.16.0", legacy.Describe());
        Assert.False(legacy.Version.IsPreview);
    }
}
