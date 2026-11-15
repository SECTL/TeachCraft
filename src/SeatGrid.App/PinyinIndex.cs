using System;
using System.Text;
using TinyPinyin;

namespace SeatGrid.App;

/// <summary>
/// 姓名 → 全拼与首字母。拼音是搜索增强，不是业务必需：
/// 任何失败都吞掉返回空串，绝不能因为它让导入失败。
/// </summary>
internal static class PinyinIndex
{
    public static (string Full, string Initials) Build(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (string.Empty, string.Empty);

        var full = new StringBuilder(name.Length * 4);
        var initials = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (c == '·' || char.IsWhiteSpace(c)) continue;

            if (c >= '一' && c <= '鿿')
            {
                try
                {
                    var pinyin = PinyinHelper.GetPinyin(c);
                    full.Append(pinyin);
                    if (pinyin.Length > 0) initials.Append(pinyin[0]);
                }
                catch
                {
                    // 个别生僻字取不到读音：原样保留，仍可按姓名搜到。
                    full.Append(c);
                    initials.Append(c);
                }
            }
            else
            {
                full.Append(c);
                initials.Append(c);
            }
        }

        return (full.ToString().ToLowerInvariant(), initials.ToString().ToLowerInvariant());
    }
}
