using TinyPinyin;

namespace Homework.App;

/// <summary>
/// 姓名 → 字母导航条的分组首字母。拼音只是检索增强，任何取不到的情况都归入「#」，
/// 绝不能因为它让名单导入或界面刷新失败。
/// </summary>
public static class PinyinIndex
{
    /// <summary>
    /// 取名字里第一个有效字符的分组键：汉字走 TinyPinyin 首字母并转大写，
    /// 拉丁字母直接转大写，其余（数字 / 符号 / 生僻字）归入「#」。
    /// </summary>
    public static string Initial(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "#";

        foreach (var c in name)
        {
            // 间隔号与空白不参与分组（「苏 · 晚晴」仍按「苏」分组）。
            if (c == '·' || char.IsWhiteSpace(c)) continue;

            if (c is >= 'a' and <= 'z') return char.ToUpperInvariant(c).ToString();
            if (c is >= 'A' and <= 'Z') return c.ToString();

            if (c is >= '一' and <= '鿿')
            {
                try
                {
                    var pinyin = PinyinHelper.GetPinyin(c);
                    if (!string.IsNullOrEmpty(pinyin))
                    {
                        var initial = char.ToUpperInvariant(pinyin[0]);
                        return initial is >= 'A' and <= 'Z' ? initial.ToString() : "#";
                    }
                }
                catch
                {
                    // 个别生僻字取不到读音：归入「#」，仍可按姓名搜到。
                }
                return "#";
            }

            return "#";
        }

        return "#";
    }
}
