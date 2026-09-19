using System;
using System.Collections.Generic;
using System.Text;

namespace FFXIVPenumbraHanhua.Services;

/// <summary>
/// 翻译管线（与独立版语义一致）：
/// 1) 已是中文 -> 剥壳为纯中文（黑名单词整段还原英文）
/// 2) 整条查词典（mods 层 key / terms 层）
/// 3) 整条未命中 -> 按 " - " 拆词块逐块查词典，黑名单词保留英文
/// 输出恒为纯中文（黑名单专名除外）。
/// </summary>
public static class Translator
{
    /// <summary> 翻译单个选项文本。 </summary>
    /// <param name="original">英文原文</param>
    /// <param name="modKey">mods 层整条 key（group文件||Opt/Name||原文），可为空</param>
    /// <param name="dict">词典</param>
    /// <returns>翻译结果（可能为原文，表示无需/无法翻译）</returns>
    public static string Translate(string original, string? modKey, DictionaryService dict)
    {
        var orig = FixRepeatParen(original);
        if (orig.Length == 0) return orig;

        // 已是中文：剥壳为纯中文（黑名单词还原英文原词）
        if (dict.ContainsChinese(orig))
        {
            return ShellToPure(orig, dict);
        }

        // 1) mods 层整条（精确 key）
        if (!string.IsNullOrEmpty(modKey))
        {
            var m = dict.LookupMod(modKey);
            if (!string.IsNullOrEmpty(m)) return ToPureChinese(m, dict);
        }

        // 2) terms 层整条
        var t = dict.LookupTerm(orig);
        if (!string.IsNullOrEmpty(t)) return ToPureChinese(t, dict);

        // 3) 词块拼装（路线 B）
        var assembled = AssembleFromBlocks(orig, dict);
        if (!string.IsNullOrEmpty(assembled)) return assembled;

        return orig; // 未翻译，保留原文
    }

    /// <summary> 词块拼装：按 " - " 拆块，逐块查词典/黑名单。 </summary>
    private static string AssembleFromBlocks(string original, DictionaryService dict)
    {
        var blocks = SplitBlocks(original);
        if (blocks.Count == 0) return "";

        var sb = new StringBuilder();
        var anyZh = false;
        foreach (var (blk, sep) in blocks)
        {
            var t = blk.Trim();
            var zh = "";
            if (t.Length > 0 && !dict.IsBlacklisted(t))
            {
                zh = dict.LookupTerm(t) ?? "";
                // 块内词边界最长子串替换（如 "White lace" 无整条 -> 拆词）
                if (zh.Length == 0 && t.Length > 1)
                {
                    zh = TranslateSubstring(t, dict);
                }
            }
            if (zh.Length == 0)
            {
                sb.Append(blk); // 保留原文块
            }
            else
            {
                sb.Append(zh);
                anyZh = true;
            }
            sb.Append(sep); // 分隔符
        }
        return anyZh ? sb.ToString() : "";
    }

    /// <summary> 词边界最长子串替换（TranslateText 的 C# 版）。 </summary>
    private static string TranslateSubstring(string text, DictionaryService dict)
    {
        var result = new StringBuilder();
        var replaced = false;
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var best = 0;
            var startB = i == 0 || IsBoundary(text[i - 1]);
            if (startB)
            {
                var maxLen = Math.Min(dict.MaxTermLen, n - i);
                for (var len = maxLen; len >= 1; --len)
                {
                    var sub = text.Substring(i, len);
                    var endPos = i + len;
                    var endB = endPos == n || IsBoundary(text[endPos]);
                    if (endB && dict.LookupTerm(sub) is { Length: > 0 })
                    {
                        best = len;
                        break;
                    }
                }
            }
            if (best > 0)
            {
                result.Append(dict.LookupTerm(text.Substring(i, best)));
                i += best;
                replaced = true;
            }
            else
            {
                result.Append(text[i]);
                ++i;
            }
        }
        return replaced ? result.ToString() : "";
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c);

    /// <summary> 拆词块：按 " - "、"/" 拆分，返回 (块原文, 紧随分隔符)。 </summary>
    private static List<(string, string)> SplitBlocks(string original)
    {
        var list = new List<(string, string)>();
        var sb = new StringBuilder();
        for (var i = 0; i < original.Length; i++)
        {
            var c = original[i];
            if (c == '-' && i + 1 < original.Length && original[i + 1] == ' ')
            {
                list.Add((sb.ToString(), "- "));
                sb.Clear();
                i++; // 跳过空格
            }
            else if (c == '/' || c == '－' || c == '—')
            {
                list.Add((sb.ToString(), c.ToString()));
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        list.Add((sb.ToString(), ""));
        return list;
    }

    /// <summary> 把「中文（英文）」或「英文（中文）」剥壳为纯中文；黑名单词整段还原英文。 </summary>
    private static string ShellToPure(string text, DictionaryService dict)
    {
        // 中文（英文）或 英文（中文）
        var open = text.IndexOf('（');
        if (open < 0) open = text.IndexOf('(');
        if (open >= 0)
        {
            var close = text.IndexOf('）', open);
            if (close < 0) close = text.IndexOf(')', open);
            if (close > open)
            {
                var zh = text[..open].Trim();
                var en = text[(open + 1)..close].Trim();
                var suffix = text[(close + 1)..];
                if (zh.Length == 0 || en.Length == 0) return text;
                if (dict.ContainsChinese(zh) && !dict.ContainsChinese(en))
                {
                    // 中文（英文）：黑名单词还原英文
                    if (dict.IsBlacklisted(en)) return en + suffix;
                    return zh + suffix;
                }
                if (dict.ContainsChinese(en) && !dict.ContainsChinese(zh))
                {
                    // 英文（中文）：黑名单词保留英文
                    if (dict.IsBlacklisted(zh)) return zh + suffix;
                    return en + suffix;
                }
            }
        }
        // 英文/中文 斜杠双语
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            var left = text[..slash].Trim();
            var right = text[(slash + 1)..].Trim();
            if (dict.ContainsChinese(left) && !dict.ContainsChinese(right)) return left;
            if (dict.ContainsChinese(right) && !dict.ContainsChinese(left)) return right;
        }
        return text;
    }

    /// <summary> 词典值转纯中文：值可能带「中文（英文）」对照，剥壳只留中文；黑名单词还原英文。 </summary>
    private static string ToPureChinese(string value, DictionaryService dict)
    {
        var v = value.Trim();
        if (!dict.ContainsChinese(v)) return v;
        return ShellToPure(v, dict);
    }

    /// <summary> 修复「英文（英文）」重复格式（如 TBSE（TBSE））。 </summary>
    private static string FixRepeatParen(string s)
    {
        var open = s.IndexOf('（');
        if (open < 0) open = s.IndexOf('(');
        if (open <= 0) return s;
        var close = s.IndexOf('）', open);
        if (close < 0) close = s.IndexOf(')', open);
        if (close != s.Length - 1) return s;
        var inner = s[(open + 1)..close];
        var outer = s[..open];
        return string.Equals(outer, inner, StringComparison.Ordinal) ? outer : s;
    }
}
