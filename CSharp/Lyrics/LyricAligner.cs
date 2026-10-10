using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// 外部歌词行 × B站AI字幕条 全局最优对齐器（Needleman-Wunsch 式 DP）。
///
/// - 全局最优，不会因前几行配错而连锁错位
/// - 跳过AI噪声行、外部多出行（和声/重复段）
/// - 输出可量化质量报告，供管线决定回退
/// 不支持多条B站字幕被多行外部歌词重叠共享（AI粒度 ≤ LRC行粒度，多对一极罕见）。
/// </summary>
public class LyricAligner
{
    private readonly double _tailThresholdSec;

    public LyricAligner(double tailThresholdSec = 15)
    {
        _tailThresholdSec = tailThresholdSec;
    }

    private const double MatchThreshold   = 0.32; // 低于此分的配对视为不可信，不计入锚定
    private const double GapPenalty       = -0.30; // DP跳行惩罚
    private const double MismatchPenalty  = -0.15; // DP低分配对的代价（仍允许配，保留时间轴连续性）

    public Task<AlignResult> AlignAsync(
        List<(double from, string content)> aiLines,
        ScoredLyric external)
    {
        return Task.Run(() => Align(aiLines, external));
    }

    private AlignResult Align(
        List<(double from, string content)> aiLines,
        ScoredLyric external)
    {
        // ── 预处理：过滤AI噪声行（纯符号/空） ──
        var ai = aiLines
            .Where(l => !string.IsNullOrWhiteSpace(l.content))
            .Where(l => l.content.Count(c => char.IsLetterOrDigit(c) ||
                        (c >= 0x4E00 && c <= 0x9FFF)) >= 2)
            .Select(l => (from: l.from, content: l.content.Trim()))
            .ToList();

        var ext = external.TimedLines
            .Where(l => !string.IsNullOrWhiteSpace(l.text))
            .Select(l => (time: l.time, text: l.text.Trim()))
            .ToList();

        int n = ai.Count, m = ext.Count;
        if (n == 0 || m == 0)
            return EmptyResult(external, n + m);

        // ── 预计算相似度矩阵 ──
        var sim = new double[n, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                sim[i, j] = LineSim(ai[i].content, ext[j].text);

        // ── Needleman-Wunsch DP ──
        // dp[i,j]：ai前i行与ext前j行的最优累积分
        var dp = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) dp[i, 0] = dp[i - 1, 0] + GapPenalty;
        for (int j = 1; j <= m; j++) dp[0, j] = dp[0, j - 1] + GapPenalty;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                double s = sim[i - 1, j - 1] >= MatchThreshold
                    ? sim[i - 1, j - 1]                      // 可信配对，奖励
                    : Math.Max(MismatchPenalty, sim[i - 1, j - 1]); // 弱配对也允许，保时间轴连续
                double match = dp[i - 1, j - 1] + s;
                double skipAi = dp[i - 1, j] + GapPenalty;    // AI噪声行
                double skipExt = dp[i, j - 1] + GapPenalty;   // 外部多出行
                dp[i, j] = Math.Max(match, Math.Max(skipAi, skipExt));
            }
        }

        // ── 回溯 ──
        var pairs = new List<(int ai, int ext, double score)>();
        int x = n, y = m;
        while (x > 0 && y > 0)
        {
            double s = sim[x - 1, y - 1] >= MatchThreshold
                ? sim[x - 1, y - 1]
                : Math.Max(MismatchPenalty, sim[x - 1, y - 1]);
            if (Math.Abs(dp[x, y] - (dp[x - 1, y - 1] + s)) < 1e-9)
            {
                pairs.Add((x - 1, y - 1, sim[x - 1, y - 1]));
                x--; y--;
            }
            else if (Math.Abs(dp[x, y] - (dp[x - 1, y] + GapPenalty)) < 1e-9) x--;
            else y--;
        }
        pairs.Reverse();

        // ── 质量报告 ──
        var anchored = pairs.Where(p => p.score >= MatchThreshold).ToList();
        int totalUnits = Math.Min(n, m);
        double coverage = totalUnits > 0 ? (double)anchored.Count / totalUnits : 0;
        double avgScore = anchored.Count > 0 ? anchored.Average(p => p.score) : 0;
        double quality = anchored.Count == 0 ? 0 : avgScore * 0.6 + coverage * 0.4;

        // ── 构建输出时间轴：锚定行用AI时间戳+外部文本 ──
        var extTimeOf = new double?[m];
        foreach (var p in anchored)
            if (!extTimeOf[p.ext].HasValue) extTimeOf[p.ext] = ai[p.ai].from;

        var lines = new List<(double time, string text)>();
        for (int j = 0; j < m; j++)
        {
            if (extTimeOf[j].HasValue)
            {
                lines.Add((time: extTimeOf[j].Value, text: ext[j].text));
            }
            else
            {
                // 未锚定的外部行：在相邻锚定行之间线性插值
                double? prev = null, next = null;
                for (int k = j - 1; k >= 0 && prev == null; k--) prev = extTimeOf[k];
                for (int k = j + 1; k < m && next == null; k++) next = extTimeOf[k];
                double t = Interpolate(prev, next, ext[j].time);
                lines.Add((time: t, text: ext[j].text));
            }
        }

        return new AlignResult
        {
            Quality = quality,
            AnchoredCount = anchored.Count,
            TotalLines = totalUnits,
            Lines = lines.OrderBy(l => l.time).ToList(),
            External = external
        };
    }

    /// <summary>未锚定行的时间插值：优先用相邻锚定的AI时间，无锚定时用外部原始时间</summary>
    private static double Interpolate(double? prev, double? next, double extTime)
    {
        if (prev.HasValue && next.HasValue) return (prev.Value + next.Value) / 2;
        if (prev.HasValue) return prev.Value + 3; // 无下界，往后顺延3s
        if (next.HasValue) return Math.Max(0, next.Value - 3);
        return extTime; // 完全无锚定，回退外部原始时间
    }

    /// <summary>行级相似度：字符bigram Dice + 包含加成</summary>
    private static double LineSim(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;

        // 短行包含：一方是另一方子串且足够长 → 高分
        if (na.Length >= 4 && (na.Contains(nb) || nb.Contains(na))) return 0.95;

        var ga = Bigrams(na);
        var gb = Bigrams(nb);
        if (ga.Count == 0 || gb.Count == 0) return 0;

        int inter = ga.Count(gb.Contains);
        return 2.0 * inter / (ga.Count + gb.Count);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static List<string> Bigrams(string s)
    {
        var list = new List<string>();
        for (int i = 0; i + 1 < s.Length; i++)
            list.Add(s.Substring(i, 2));
        return list;
    }

    private static AlignResult EmptyResult(ScoredLyric external, int total)
        => new AlignResult
        {
            Quality = 0,
            AnchoredCount = 0,
            TotalLines = total,
            Lines = new List<(double time, string text)>(),
            External = external
        };
}

/// <summary>对齐结果：质量报告 + 最终时间轴</summary>
public class AlignResult
{
    public double Quality;                    // 0~1，低于 MinAlignQuality 管线回退
    public int AnchoredCount;                 // 可信锚定行数
    public int TotalLines;                    // 参与对齐的单位数 min(AI行, 外部行)
    public List<(double time, string text)> Lines;
    public ScoredLyric External;              // 来源候选（供日志/调试）

    public string ToLrc()
    {
        var sb = new StringBuilder();
        foreach (var (t, txt) in Lines.OrderBy(l => l.time))
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, t));
            sb.AppendLine($"[{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{txt}");
        }
        return sb.ToString();
    }
}
