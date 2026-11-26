using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TeachCraft.Tests;

/// <summary>
/// 面向教师的报错必须是中文：任何状态栏 / 错误条 / 对话框 / 警告列表都不得拼接异常原文。
/// 诊断文件（.error.txt、RawDump）不在此列。
/// </summary>
public sealed class UserFacingErrorTextTests
{
    private static readonly string[] Sinks =
    [
        "SetStatus(", "SetEditorStatus(", "ShowError(", "ShowExcelError(",
        "StatusText =", "ErrorText =", "PreviewEmptyText =", "DialogError =",
        "FailRosterImport(", "warnings.Add(", "warns.Add(", "errors.Add(",
        "issues.Add(", "ResultText =", "ExcelClassSplitStatus =", "PreviewText =",
        "PreviewSummary =", "Conflicts =", "RecognitionFailure",
    ];

    private static readonly Regex ThrowWithRawMessage = new(
        @"throw new \w+Exception\([^;]{0,500}?\{ex\.Message\}", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ThrowRawMessageArgument = new(
        @"throw new \w+Exception\(\s*ex\.Message", RegexOptions.Compiled);

    [Fact]
    public void UserFacingSinks_NeverInterpolateRawExceptionMessages()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in RepoRoot.ReadAll(Path.Combine("src"), ".cs"))
        {
            var relative = Path.GetRelativePath(RepoRoot.Path, path).Replace('\\', '/');
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (!Sinks.Any(s => line.Contains(s, StringComparison.Ordinal))) continue;
                if (line.Contains("ex.Message", StringComparison.Ordinal)
                    || line.Contains("ex.ToString()", StringComparison.Ordinal)
                    || line.Contains("ex.GetType().Name", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "面向用户的报错不得拼接异常原文，请改用 ErrorText.Describe(ex, \"动作\")：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ThrownUserFacingExceptions_NeverInterpolateRawExceptionMessages()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in RepoRoot.ReadAll(Path.Combine("src"), ".cs"))
        {
            var relative = Path.GetRelativePath(RepoRoot.Path, path).Replace('\\', '/');
            foreach (Match match in ThrowWithRawMessage.Matches(text))
            {
                offenders.Add($"{relative}: {match.Value.Replace('\n', ' ').Trim()}");
            }
            foreach (Match match in ThrowRawMessageArgument.Matches(text))
            {
                offenders.Add($"{relative}: {match.Value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "抛给教师的异常不得直接使用异常原文，请改用 ErrorText.Describe(ex, \"动作\")：\n" + string.Join("\n", offenders));
    }
}
