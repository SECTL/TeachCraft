using Homework.Core.Export;

namespace TeachCraft.Tests;

/// <summary>
/// 作业成品命名与路径防线。合成班级 / 姓名，不触碰真实数据。
/// </summary>
public sealed class HomeworkNamingTests
{
    // ----------------------------- Sanitize -----------------------------

    [Fact]
    public void Sanitize_ReplacesInvalidFileNameChars()
    {
        var result = HomeworkFileNamer.Sanitize("a/b\\c:d*e?f\"g<h>i|j");
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain(':', result);
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", result);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX", "_AUX")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("com9", "_com9")]
    [InlineData("LPT2", "_LPT2")]
    [InlineData("COM0", "COM0")]
    [InlineData("COM10", "COM10")]
    public void Sanitize_PrefixesReservedDeviceNames(string input, string expected)
    {
        Assert.Equal(expected, HomeworkFileNamer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ReservedNameWithExtension_IsPrefixed()
    {
        Assert.Equal("_CON.txt", HomeworkFileNamer.Sanitize("CON.txt"));
    }

    [Fact]
    public void Sanitize_StripsTrailingDotsAndSpaces()
    {
        Assert.Equal("作业", HomeworkFileNamer.Sanitize("作业. "));
        Assert.Equal("作业", HomeworkFileNamer.Sanitize("作业...   "));
    }

    [Fact]
    public void Sanitize_EmptyFallsBack()
    {
        Assert.Equal("未命名", HomeworkFileNamer.Sanitize(""));
        Assert.Equal("未命名", HomeworkFileNamer.Sanitize("   "));
        Assert.Equal("备用", HomeworkFileNamer.Sanitize("", "备用"));
    }

    [Fact]
    public void Sanitize_CapsAt120Chars()
    {
        var result = HomeworkFileNamer.Sanitize(new string('字', 200));
        Assert.Equal(120, result.Length);
    }

    // ----------------------------- 文件名拼装 -----------------------------

    [Fact]
    public void StudentStem_OmitsEmptyNumberWithoutDoubleUnderscore()
    {
        Assert.Equal("八年级1班_苏晚晴", HomeworkFileNamer.StudentStem("八年级1班", "", "苏晚晴"));
        Assert.Equal("八年级1班_01", HomeworkFileNamer.StudentStem("八年级1班", "01", ""));
        Assert.Equal("八年级1班_01_苏晚晴", HomeworkFileNamer.StudentStem("八年级1班", "01", "苏晚晴"));
    }

    [Fact]
    public void StudentStem_AllBlank_FallsBack()
    {
        Assert.Equal("未命名", HomeworkFileNamer.StudentStem("", "", ""));
    }

    [Fact]
    public void PageSuffix_IsOneBasedChinese()
    {
        Assert.Equal("第1页", HomeworkFileNamer.PageSuffix(1));
        Assert.Equal("第12页", HomeworkFileNamer.PageSuffix(12));
    }

    [Fact]
    public void StudentFileName_NormalizesExtension()
    {
        Assert.Equal("八年级1班_01_苏晚晴_第2页.pdf",
            HomeworkFileNamer.StudentFileName("八年级1班", "01", "苏晚晴", 2, ".PDF"));
        Assert.Equal("八年级1班_01_苏晚晴_第2页.png",
            HomeworkFileNamer.StudentFileName("八年级1班", "01", "苏晚晴", 2, "png"));
    }

    [Fact]
    public void StudentPdfFileName_HasNoPageSuffix()
    {
        Assert.Equal("八年级1班_01_苏晚晴.pdf",
            HomeworkFileNamer.StudentPdfFileName("八年级1班", "01", "苏晚晴"));
    }

    [Fact]
    public void ClassFolderName_IsSanitized()
    {
        Assert.Equal("八年级_1班", HomeworkFileNamer.ClassFolderName("八年级/1班"));
    }

    // ----------------------------- 路径防线 -----------------------------

    [Fact]
    public void IsSamePath_IgnoresCase()
    {
        Assert.True(HomeworkPathGuard.IsSamePath(@"C:\Temp\作业.pdf", @"c:\temp\作业.pdf"));
    }

    [Fact]
    public void IsSamePath_ResolvesRelativePaths()
    {
        Assert.True(HomeworkPathGuard.IsSamePath("作业.pdf", @".\作业.pdf"));
        Assert.False(HomeworkPathGuard.IsSamePath("作业.pdf", "别的.pdf"));
    }

    [Fact]
    public void Normalize_ReturnsFullPath()
    {
        Assert.True(Path.IsPathFullyQualified(HomeworkPathGuard.Normalize("作业.pdf")));
    }

    [Fact]
    public void IsBlockedTarget_MatchesAnySource()
    {
        var sources = new[] { @"C:\导入\名单.xlsx", @"C:\导入\底图.png" };
        Assert.True(HomeworkPathGuard.IsBlockedTarget(@"c:\导入\名单.XLSX", sources));
        Assert.True(HomeworkPathGuard.IsBlockedTarget(@"C:\导入\底图.png", sources));
        Assert.False(HomeworkPathGuard.IsBlockedTarget(@"C:\输出\成品.pdf", sources));
        Assert.False(HomeworkPathGuard.IsBlockedTarget(@"C:\导入\名单.xlsx", []));
    }
}
