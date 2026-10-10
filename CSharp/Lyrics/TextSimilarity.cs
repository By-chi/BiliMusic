using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 文本相似度工具集（针对 AI字幕 vs 正版歌词 场景）。
/// AI字幕典型错误：同音字替换 / 漏字 / 多字 / 标点差异 / 空格差异。
/// 三个互补特征：
///   LCS          —— 顺序敏感，容忍漏字/多字（插入删除）
///   Levenshtein  —— 容忍同音字替换
///   BigramCosine —— 捕捉局部字符共现
/// </summary>
public static class TextSimilarity
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = SubtitleUtils.ToSimplified(text);
        text = text.ToLowerInvariant();
        text = FullWidthToHalf(text);
        text = Regex.Replace(text, @"[\s\p{P}\p{S}]", ""); // 去空白+标点+符号
        return text;
    }
    public static int LcsLength(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x.Length < y.Length) (x, y) = (y, x);
        int m = x.Length, n = y.Length;
        var prev = new int[n + 1]; var curr = new int[n + 1];
        for (int i = 1; i <= m; i++)
        {
            char ca = x[i - 1];
            for (int j = 1; j <= n; j++)
                curr[j] = ca == y[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }
        return prev[n];
    }

    /// <summary>短文本在长文本中的按序包含度 [0,1]，不随长文长度退化</summary>
    public static double ContainmentScore(string shortText, string longText)
    {
        string a = Normalize(shortText);
        if (a.Length == 0) return 0;
        return (double)LcsLength(a, Normalize(longText)) / a.Length;
    }

    private static string FullWidthToHalf(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == 0x3000) sb.Append(' ');                          // 全角空格
            else if (c > 0xFF00 && c < 0xFF5F) sb.Append((char)(c - 0xFEE0));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static double LcsRatio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Length < b.Length) (a, b) = (b, a); // 短的做内层省内存

        int m = a.Length, n = b.Length;
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (int i = 1; i <= m; i++)
        {
            char ca = a[i - 1];
            for (int j = 1; j <= n; j++)
                curr[j] = ca == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }
        return (double)prev[n] / m;
    }

    public static double LevenshteinRatio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;

        int m = a.Length, n = b.Length;
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            char ca = a[i - 1];
            for (int j = 1; j <= n; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return 1.0 - (double)prev[n] / Math.Max(m, n);
    }

    public static double BigramCosine(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1.0 : 0.0;
        var fa = GramFreq(a);
        var fb = GramFreq(b);

        double dot = 0, na = 0, nb = 0;
        foreach (var kv in fa) na += (double)kv.Value * kv.Value;
        foreach (var kv in fb)
        {
            nb += (double)kv.Value * kv.Value;
            if (fa.TryGetValue(kv.Key, out int va)) dot += (double)va * kv.Value;
        }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static Dictionary<string, int> GramFreq(string s)
    {
        var d = new Dictionary<string, int>(s.Length);
        for (int i = 0; i < s.Length - 1; i++)
        {
            string g = s.Substring(i, 2);
            d[g] = d.TryGetValue(g, out int c) ? c + 1 : 1;
        }
        return d;
    }

    /// <summary>输入未归一化原文</summary>
    public static double CombinedScore(string rawA, string rawB)
        => CombinedScoreNormalized(Normalize(rawA), Normalize(rawB));

    /// <summary>输入已归一化文本（对齐器内循环用，避免重复 Normalize）</summary>
    public static double CombinedScoreNormalized(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        return 0.45 * LcsRatio(a, b)
             + 0.35 * LevenshteinRatio(a, b)
             + 0.20 * BigramCosine(a, b);
    }

    /// <summary>
    /// 行对齐匹配分 = 组合分 × 长度一致性惩罚。
    /// 防止一行外部歌词"吞噬"远多于自己内容的B站文本。
    /// </summary>
    public static double MatchScoreNormalized(string a, string b)
    {
        double baseScore = CombinedScoreNormalized(a, b);
        if (baseScore <= 0) return 0;
        double lenRatio = (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        return baseScore * (0.4 + 0.6 * lenRatio);
    }

    /// <summary>整篇文档级比较（候选歌词验证用，千字级毫秒完成）</summary>
    public static double DocumentScore(string docA, string docB)
    {
        string a = Normalize(docA), b = Normalize(docB);
        if (a.Length == 0 || b.Length == 0) return 0;
        return 0.5 * BigramCosine(a, b) + 0.5 * LcsRatio(a, b);
    }
}